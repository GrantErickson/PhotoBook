# ADR-0008 — Journal parsing with DocumentFormat.OpenXml (no Word interop)

This ADR records how PhotoBook reads the optional dated Word journal (R2): directly parsing the
.docx package with the DocumentFormat.OpenXml SDK, with no dependency on an installed copy of
Microsoft Word.

Related docs: [11-journal-ingestion.md](../11-journal-ingestion.md) · [05-ingestion-and-photo-sources.md](../05-ingestion-and-photo-sources.md) · [04-project-format-and-storage.md](../04-project-format-and-storage.md)

## Status

Accepted — 2026-08-01.

## Context

A journal in Word "may be provided that has dated entries to be interleaved with the pictures"
(R2), and a day's journal text must stay together on the page (R5). The document is family-written
prose: date headings in mixed and unknown formats ("July 4", "7/4/2024", "Friday the 4th"),
inconsistent styles, possibly lists and bold runs. Ingestion must therefore be *tolerant* — a
multi-matcher over date formats — and *honest* — anything it cannot date lands in an Import Report
the user resolves in the UI, never silently dropped. Parsed output is plain structured data
(`JournalEntry` records with date, paragraphs, and source location) written to `journal.json`.

We need read-only extraction of paragraph text, basic run formatting (bold/italic, for cues only),
and paragraph order. We do not need rendering fidelity, and we never write .docx.

## Options considered

### Word COM interop

`Microsoft.Office.Interop.Word` gives perfect fidelity — and is disqualified on the first line:
it requires Word installed and licensed, runs Word as an out-of-process COM server (seconds of
startup, orphaned WINWORD.EXE on crashes), demands STA threading that fights our background job
queue, and is explicitly unsupported by Microsoft for unattended automation. A desktop app that
breaks when Office updates is not shippable.

### docx → HTML/Markdown converters (Pandoc, Mammoth, et al.)

Shelling out to Pandoc means bundling a ~150 MB external toolchain and parsing its HTML output —
a lossy round-trip that discards the paragraph identity we need for the Import Report ("entry at
paragraph 214 could not be dated"). Mammoth's maintained implementations are JavaScript/Python;
the .NET port is stale. Converters solve *presentation* conversion; our problem is *structural
extraction*. Wrong tool.

### Manual XML parsing of the OPC package

A .docx is a zip of XML; `System.IO.Packaging` + `XDocument` over `word/document.xml` works — until
it meets the real spec: runs split mid-word by spell-check markers, `w:br`/`w:tab`, field codes,
revision marks (`w:ins`/`w:del`), numbering definitions in a separate part. Reimplementing WordprocessingML
plumbing by hand is unpaid maintenance with no upside over the SDK that does exactly this.

### DocumentFormat.OpenXml SDK (chosen)

Microsoft's official, MIT-licensed, dependency-free (managed-only) library for OPC/OOXML packages.
Typed access to `Body`, `Paragraph`, `Run`, `Text`, styles, and numbering — read-only use is
mature and fast (a 300-page journal parses in well under a second).

## Decision

> **Decision:** `PhotoBook.Ingestion` parses journals with DocumentFormat.OpenXml only; no Word
> interop, no external converters. Rationale inline: it is the only option that is dependency-free,
> structure-preserving, and safe on a background thread.

Implementation shape (full spec in [11-journal-ingestion.md](../11-journal-ingestion.md)):

- Open read-only: `WordprocessingDocument.Open(path, isEditable: false)`.
- Walk `MainDocumentPart.Document.Body` paragraphs in order; concatenate each paragraph's `Text`
  nodes (handling split runs, `w:br` as line breaks, `w:tab` as spaces); capture bold/heading-style
  flags and the paragraph index.
- Feed paragraph texts to the tolerant date multi-matcher (ordered strategies: explicit formats →
  `DateTime.TryParse` with the user's culture → weekday+ordinal inference anchored to the book
  year). A matched date opens a new `JournalEntry`; following paragraphs attach to it.
- Unmatched leading paragraphs and ambiguous dates (e.g. "3/4") go to the Import Report in
  `journal.json` with their paragraph index and raw text; the UI lets the user assign or discard.
- Embedded images in the journal are ignored in v1 (photos come from the photo source, R1);
  tracked-changes content uses the accepted-state text.

## Consequences

- Works on a clean Windows install; parsing runs on the Channels job queue like any other import
  work ([ADR-0009](0009-app-architecture-mvvm-di-jobs.md)).
- Only .docx is supported. Legacy .doc users must save-as .docx once — documented, not engineered
  around.
- We own semantic edge cases (numbering, field codes) as they surface in real journals; the SDK
  gives us the nodes, our matcher owns the meaning. Real family journals become test fixtures in
  [13-testing-strategy.md](../13-testing-strategy.md).
- Formatting is deliberately flattened to plain text plus emphasis cues; journal typography is
  reapplied by the Style system (R23), keeping book output consistent regardless of Word styling.

## Revisit when

- Real journals surface WordprocessingML constructs the extractor mishandles (fields, content controls).
- A second journal source appears (Markdown, OneNote, plain text) — the date multi-matcher and Import Report should be reused above a new reader.
- Rich formatting preservation (inline photos, per-entry styling) is requested for the book output.
