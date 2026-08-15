# 00 — Spec Kernel

The single source of truth for the constants, names, schemas, and decisions that more than one doc
depends on. Every other doc in this set cites it as **"kernel §N"**; where a doc and this file
disagree, **this file wins** and the doc gets fixed. Requirements are cited as **R1–R28**, the
numbered items in the repo [readme.md](../readme.md).

Related docs: [README.md](README.md) · [01-vision-and-principles.md](01-vision-and-principles.md)
· [03-domain-model.md](03-domain-model.md) · [15-glossary.md](15-glossary.md)

## 1. Product one-liner

PhotoBook is a single-user Windows desktop app that mostly-automatically lays out yearly family
photo books (one book = one year, one Chapter = one month) from a photo set plus an optional dated
journal in Word, producing a print-ready PDF. North star (the user's words):

> **"The automatic layout being awesome is the most important part. Edits should really be
> tweaks."** (R27)

## 2. Locked decisions

Locked with the user on 2026-08-01 unless noted otherwise.

| Topic | Decision |
|---|---|
| Platform | Windows desktop app, single user — [ADR-0001](adr/0001-platform-dotnet.md) |
| Stack | **.NET 10 / C# 14** — the current LTS, supported through November 2028 (user constraint: "anything except Python"). TFMs: `net10.0` for libraries, `net10.0-windows` for the WPF app — [ADR-0001](adr/0001-platform-dotnet.md) |
| UI | WPF + CommunityToolkit.Mvvm + AvalonDock; Avalonia is the documented runner-up, WinUI 3 rejected for tooling/docking immaturity — [ADR-0002](adr/0002-ui-wpf.md) |
| Renderer | SkiaSharp — the same draw code renders the screen (`SKElement`) and the PDF (`SKDocument`), so preview and print cannot diverge — [ADR-0003](adr/0003-rendering-skiasharp.md) |
| Imaging | Magick.NET-Q8 as the sole decode/edit backend (bundled libheif ⇒ HEIC works with no OS codecs); non-destructive parametric AdjustmentStack; originals immutable — [ADR-0004](adr/0004-imaging-magick-net.md) |
| Local ML | ONNX Runtime: YuNet (faces), U2-Netp (saliency), NIMA/MobileNet (aesthetics), plus classical sharpness/exposure metrics — [ADR-0005](adr/0005-local-ml-onnx-runtime.md) |
| AI strategy | Local-first; Azure AI Vision is an optional plug-in behind the same `IImageAnalyzer` (R27) — [ADR-0010](adr/0010-analysis-plugin-local-first.md) |
| Output | Print-ready generic PDF; print-service specifics are swappable JSON `PrintProfile`s; per-page JPEG possible later — [ADR-0006](adr/0006-pdf-skdocument.md) |
| Page size | 11 × 8.5 in **landscape** default (Spread = 22 × 8.5 in panorama); other sizes supported via `pageSize` (R19) |
| Photo volume | Under ~2,000 photos/year (pre-culled) ⇒ plain JSON plus a file cache; **no database** — [ADR-0007](adr/0007-project-storage-json-folder.md) |
| Originals | **Copied into the project folder**, so a project is a self-contained archive |
| Photo source | OneDrive via Microsoft Graph (MSAL sign-in), with local-folder fallback; OneDrive people tags become named, top-priority Focus Regions — [ADR-0011](adr/0011-onedrive-graph-ingestion.md) |
| Photo selection | **Album + in-app refine** — photos are added to a per-book OneDrive album ("Book 2024") from phone or desktop, the app syncs that album, and final trims happen in the in-app grid. Folder-as-source also supported. Confirmed by the user 2026-08-03 |
| Journal | Word `.docx` via the OpenXML SDK, no Word interop; mixed/unknown date formats ⇒ tolerant multi-matcher plus an Import Report — [ADR-0008](adr/0008-journal-openxml.md) |
| Cover | Deferred to v2 — a documented non-goal; v1 exports the interior PDF only |
| Background | Solid black in v1; image backgrounds later, with the hook documented (R21) |

## 3. Geometry and export constants

Every doc that states one of these must state exactly this value.

| Constant | Value |
|---|---|
| Trim (single page) | 11.0 × 8.5 in landscape |
| Trim (Spread) | 22.0 × 8.5 in |
| Bleed | 0.125 in per outer edge ⇒ single-page bleed box 11.25 × 8.75 in |
| Safe margin | 0.375 in inside Trim — text and faces stay inside |
| Gutter caution zone | 0.5 in either side of the Spread centerline — no faces or text; full-Spread photos accept the center loss (R18) |
| Template coordinates | Normalized `[0,1] × [0,1]` over the single-page trim box, origin top-left, `rect = {x, y, w, h}` |
| Export resolution | 300 DPI downsampled images |
| Export image codec | JPEG quality 90 inside the PDF |
| Color | sRGB |
| Fonts | Bundled OFL fonts, subset-embedded |
| Page background (v1) | `#000000`; default text on background `#FFFFFF` |

## 4. Domain entities and the shared value types

Entity names are fixed: `Book`, `Chapter` (a month), `Page`, `Spread` (a **view** over two facing
pages, never a stored entity), `Template`, `ImageSlot`, `TextSlot`, `Photo`, `CropState`,
`AdjustmentStack`, `FocusRegion`, `QualityScore` (carrying `Tier`), `PersonTag`, `JournalEntry`,
`DayGroup`, `Style`, `PrintProfile`. Full definitions live in
[03-domain-model.md](03-domain-model.md).

Page states: **Pinned** (the user touched it; re-layout leaves it alone) and **Detached** (its
template was hand-edited into an inline snapshot). Photo holding areas: the **Unplaced bin**
(photos on no page), the **Upcoming bin** (photos placed on later pages of the Chapter), and the
**Outside-book tray** (photos whose date change moved them out of the book's year, R6). Photos
with `excluded: true` stay excluded across every re-scan and re-sync (R17).

### CropState — the one crop model

Docs [07](07-layout-template-system.md), [08](08-auto-layout-engine.md), and
[09](09-editor-ux.md) use this verbatim; auto-layout emits exactly what the user hand-tweaks.

```
CropState { zoom: double, offsetX: double, offsetY: double }
```

- `coverScale = max(slotW/imgW, slotH/imgH)` — the scale at which the image exactly covers the Slot.
- Effective scale = `zoom × coverScale`; `zoom = 1.0` is the minimal-crop cover fit and the default.
- `offsetX` / `offsetY` pan the image center relative to the Slot center, in slot-width and
  slot-height units, clamped so no gap can appear while `zoom ≥ 1`.
- `zoom < 1` is legal: the image no longer fills the Slot and the page background shows through as
  a letterbox (R9).

### FocusRegion

```
FocusRegion { rect: Rect (normalized image coords), weight: 0..1,
              kind: user | person | face | saliency, personName?: string }
```

Fusion priority: `user` > `person` (OneDrive people tag) > `face` (YuNet) > `saliency` (U2-Netp).
Smart-crop rule: choose the maximal crop window of the Slot's aspect that contains the primary
Focus Region (highest weight, overlapping regions merged), then express it as a `CropState`.

### QualityScore and Tiers

The fused score (NIMA aesthetic + sharpness + exposure + a face count/size bonus) becomes a
percentile **within the month**, which maps to a Tier: **S** = top 10%, **A** = next 25%,
**B** = next 45%, **C** = bottom 20%. Tier drives slot-size affinity. A user promote/demote sets
the Tier **absolutely** and it is never re-derived (R26).

### AdjustmentStack provenance and auto-adjust

Auto-adjust chooses an `AdjustmentStack` from what a photo's *original* pixels measure plus the
book's `LookProfile`. A photo is therefore in exactly one of three states, derived from two stored
fields and never stored directly:

| State | Stored as | Auto-adjust |
| --- | --- | --- |
| **Untouched** | no `autoAdjust`, identity stack | adjusts it |
| **Automatic** | `autoAdjust` stamp present | re-derives it, so changing the look takes effect |
| **Manual** | `adjustmentsUserEdited`, **or** a non-identity stack with no stamp | never touches it |

> **Decision:** **A hand edit is absolute.** One slider move takes a photo off auto for good, exactly
> as a promote/demote does to a Tier — the engine is opinionated but never entitled to undo a human
> (P6). The only way back is the per-photo *Auto* command, or the explicitly opt-in checkbox on the
> batch, both of which are the user asking.

The third row is what makes this safe to add to an existing book: a `photos.json` written before the
feature has neither field, and adjustments in such a file can only have come from a person.

The stamp carries the measurement, not just a rules version. Measuring is a decode per photo and
choosing is arithmetic, so changing the look settings and re-running is instant rather than a
book-length decode. It carries **no timestamp** — a wall-clock field would rewrite every photo row on
every run and break doc 04 §4 rule 5. Everything is measured on the unadjusted original, which is why
running twice converges instead of correcting an already-corrected photo again.

Auto-straighten is the one part that moves pixels: it invalidates the analysis copy, so a *later*
analysis run measures the straightened, wedge-cropped frame and may re-tier the photo. Nothing
re-runs analysis on its own, so this is never a surprise mid-edit — but it is why straightening is
refused below a confidence threshold and is a separate switch in the `LookProfile`.

## 5. Project folder format

```
MyBook-2024/
  book.json          — book settings, Style, page size, PrintProfile reference, Seed
  photos.json        — catalog: source ids, dates + dateUncertain, adjustments, Focus Regions,
                       Tier (+ userTierOverride), excluded flags, PersonTags
  journal.json       — parsed JournalEntries + the unmatched-import report
  chapters/2024-01.json … 2024-12.json
                     — pages, template refs or Detached snapshots, slot→photo placements with
                       CropState, pinned flags
  originals/         — copied source images, content-hash-prefixed filenames, immutable
  cache/             — thumbnails and analysis outputs; 100% regenerable
```

Serialization: System.Text.Json, camelCase, a `schemaVersion` per file, atomic writes (temp file
then rename), autosave every 30 s and after major operations, rolling `.bak`. Human-diffable
output is a design goal. Per-chapter files are what make a Chapter stand alone (R4).

> **Decision:** **User intent never lives in cache.** Anything the user chose — dates, crops,
> exclusions, Tier overrides, Focus Region edits, placements — lives in the JSON files. `cache/`
> can be deleted at any time and costs only recompute. See
> [04-project-format-and-storage.md](04-project-format-and-storage.md) and
> [ADR-0007](adr/0007-project-storage-json-folder.md).

## 6. Template schema (canonical sketch)

The full specification, field types, and validation rules are in
[07-layout-template-system.md](07-layout-template-system.md).

```jsonc
{
  "id": "t-04-text-a",              // stable string id
  "name": "Four up with journal",
  "pageSize": "11x8.5-landscape",
  "kind": "standard | monthTitle | fullBleed | multiDay | spreadPair",
  "photoCount": 4,                   // 1..8 (R20)
  "slots": [ { "id": "s1", "rect": {"x":0,"y":0,"w":0.5,"h":0.66},
               "aspect": 1.5, "aspectTolerance": 0.35,
               "tierAffinity": "S|A|B|C|any",
               "captionPolicy": "none|below|overlay" } ],
  "textSlots": [ { "id": "t1", "rect": { }, "role": "journal|caption|monthTitle" } ],
  "mirrorable": true,                // authored as a right page; mirrored for left pages
  "sections": [ ]                    // multiDay only: per-day groups of slots + text kept together
}
```

The v1 library targets **~50 templates**: 1-photo (including full-bleed), 2- through 8-photo
layouts, month-title pages, multi-day pages, and a few matched spread pairs. Not every template
has a text slot — textless layouts are the deliberate negative-space option (R20). Manual page
edits **Detach** the page into an inline snapshot; the library template is never mutated.

## 7. Auto-layout engine

A pure, deterministic function of (photos, journal, templates, style, **Seed**) — same inputs
produce the same book. Phases, in order: **day grouping** → **Demand Model** (how much page area
each day wants, from photo count, Tiers, and text length) → **DP page partitioning** over the day
sequence (sparse days merge per R28, big days split) → **template scoring** (hard filters on photo
count and text fit; soft scores for aspect match, Tier match, variety and pacing) → **Hungarian
slot assignment** (photos → Slots, minimizing crop loss plus Tier mismatch) → **smart crop**
(emitting the `CropState` of §4).

A manual edit marks the page **Pinned**. "Auto-layout rest of chapter" regenerates only unpinned
pages and lists the affected page numbers before running (R16). Performance targets: a full-year
re-layout in under 10 s for 2,000 photos with analysis precomputed; full-year analysis under
15 minutes in the background on first import. Full specification:
[08-auto-layout-engine.md](08-auto-layout-engine.md).

## 8. Editor shell

Book dashboard → Month workspace, which has a **Photos** tab (grid: inspect, reorder, re-date,
Tier promote/demote, Focus Region editing, adjustments) and a **Pages** tab (single page or Spread
view, drag-and-drop with **swap semantics**, pan/zoom cropping, a dockable Unplaced/Upcoming bin
panel the user can put at the bottom or the side, empty Slots flagged **amber**, and a per-page
layout override mode). Undo/redo is a command pattern; the model has a single writer; heavy work
runs on a Channels-based background job queue. Full specification:
[09-editor-ux.md](09-editor-ux.md).

## 9. Typography and style defaults

Bundled OFL fonts: journal text **Source Serif 4** at 10.5 pt, captions **Source Sans 3** at
8.5 pt, month titles **Playfair Display** at 64 pt. Overlay captions sit on a bottom scrim (black
gradient, 0 → 60% opacity). The `Style` cascade is global → Chapter → Page, and it controls image
borders across every image on every page, font families, sizes, and colors, with journal and
caption sizes settable independently (R23).

> **Decision:** **No auto font-shrink.** A day's journal text is atomic: it may span the two pages
> of one Spread but never crosses Spreads. If it does not fit, the layout engine must choose a
> roomier template rather than shrink type — see [11-journal-ingestion.md](11-journal-ingestion.md)
> and §12 of [08-auto-layout-engine.md](08-auto-layout-engine.md).

## 10. Dates, ingestion, and analysis

The date chain is EXIF `DateTimeOriginal` → Graph `photo.takenDateTime` → file mtime; the last
resort sets `dateUncertain: true`, which surfaces in the UI and in preflight. Changing a photo's
date can move it to another Chapter or out of the book year entirely, in which case it lands in
the Outside-book tray rather than disappearing (R6).

Thumbnail tiers are fixed at three: 256 px (grid), 1024 px (layout preview), and full resolution
(export only). All pixels go through Magick.NET, with EXIF orientation applied at decode.

Analysis is pluggable behind `IImageAnalyzer`: `LocalOnnxAnalyzer` is the default and
`AzureVisionAnalyzer` is optional and opt-in per book. **Microsoft Graph exposes no people-tag
metadata for consumer OneDrive** — measured 2026-08-04 against a real account and recorded in
[05-ingestion-and-photo-sources.md](05-ingestion-and-photo-sources.md). Local face detection is
therefore the permanent source of face regions; `person` regions come only from the user. The
engine always treated people tags as an optional input, so nothing downstream changes.

## 11. PDF export and preflight

The `SKDocument` pipeline renders each page using the §3 geometry. Print-service specifics live in
a swappable JSON `PrintProfile` (page sizes, bleed, color intent, and spine calculation once
covers arrive in v2). Preflight gates export on: empty Slots, effective image DPI below 200, text
overflow, a non-empty Unplaced bin, and `dateUncertain` photos. Output is deterministic — the same
project yields a byte-stable PDF, with document timestamps pinned via a metadata option. Full
specification: [12-pdf-export.md](12-pdf-export.md).

## 12. Solution layout

```
PhotoBook.sln
  src/PhotoBook.Core        — domain model, project persistence (no UI dependencies)
  src/PhotoBook.Imaging     — Magick.NET decode/edit, thumbnails, cache
  src/PhotoBook.Analysis    — IImageAnalyzer, local ONNX, Azure adapter
  src/PhotoBook.Ingestion   — OneDrive Graph connector, folder import, journal (OpenXML)
  src/PhotoBook.Engine      — auto-layout engine (pure, deterministic)
  src/PhotoBook.Rendering   — SkiaSharp page renderer and PDF export
  src/PhotoBook.App         — WPF shell (MVVM, AvalonDock, job queue)
  tests/…                   — xUnit, golden layout tests, Verify-based snapshots
```

> **Decision:** Dependency rule — App references everything; Engine, Rendering, Analysis, Imaging,
> and Ingestion reference only Core; Core references nothing; nothing references App. **The Engine
> performs no I/O.** See [02-architecture.md](02-architecture.md) and
> [ADR-0009](adr/0009-app-architecture-mvvm-di-jobs.md).

## 13. Canonical file list

The doc set is exactly these files; cross-links must use these names.

```
docs/README.md                       docs/09-editor-ux.md
docs/00-spec-kernel.md               docs/10-styles-and-typography.md
docs/01-vision-and-principles.md     docs/11-journal-ingestion.md
docs/02-architecture.md              docs/12-pdf-export.md
docs/03-domain-model.md              docs/13-testing-strategy.md
docs/04-project-format-and-storage.md docs/14-roadmap.md
docs/05-ingestion-and-photo-sources.md docs/15-glossary.md
docs/06-image-analysis.md            docs/adr/0000-adr-template.md
docs/07-layout-template-system.md    docs/adr/0001-platform-dotnet.md … 0011-onedrive-graph-ingestion.md
docs/08-auto-layout-engine.md
```

## 14. Roadmap milestones

M0 walking skeleton (folder → fixed template → PDF) → M1 the awesome engine (analysis, template
library, DP layout; the Graph spike) → M2 photo viewer and the Graph connector → M3 tweak editing
(drag/swap, crop, bins) → M4 journal and text → M5 polish (styles, page overrides, multi-day,
spreads) → M6 ship (preflight, hardening). Each milestone has explicit exit criteria in
[14-roadmap.md](14-roadmap.md).

## 15. Documentation conventions

- Each doc opens with `# NN — Title`, a one-paragraph purpose, and a "Related docs" line.
- Binding choices are blockquotes beginning `> **Decision:**`, each backed by an ADR link or an
  inline rationale.
- Requirements are cited **R1–R28**, matching the numbered items in the repo
  [readme.md](../readme.md).
- Cross-links are relative markdown links using the §13 filenames.
- Diagrams are Mermaid fenced blocks, kept simple enough to render on GitHub.
- Glossary terms ([15-glossary.md](15-glossary.md)) are capitalized on first use in each doc.
