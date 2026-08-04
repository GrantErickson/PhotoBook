# ADR-0006 — PDF export via SKDocument.CreatePdf

This ADR records why PhotoBook exports its print-ready interior PDF through SkiaSharp's
`SKDocument.CreatePdf` rather than a dedicated PDF library or the Windows print pipeline, and how
we get deterministic, font-subsetted output from it.

Related docs: [12-pdf-export.md](../12-pdf-export.md) · [02-architecture.md](../02-architecture.md) · [10-styles-and-typography.md](../10-styles-and-typography.md) · [ADR-0003](0003-rendering-skiasharp.md)

## Status

Accepted — 2026-08-01.

## Context

PhotoBook's output is a print-ready generic PDF: 11×8.5 in landscape trim, 0.125 in bleed per outer
edge (media box 11.25 × 8.75 in per page), 300 DPI images, JPEG quality 90, sRGB, bundled OFL fonts
(Source Serif 4, Source Sans 3, Playfair Display). Other page sizes must work via the template
`pageSize` field (R19). Two properties are non-negotiable:

1. **WYSIWYG by construction.** ADR-0003 chose SkiaSharp so the *same* draw code renders the screen
   (`SKElement`) and the export. Any export path that re-implements page drawing in a second API
   reintroduces the screen/print divergence we chose SkiaSharp to eliminate.
2. **Determinism.** The auto-layout engine is a pure function of its inputs plus a Seed
   ([08-auto-layout-engine.md](../08-auto-layout-engine.md)); the export must extend that promise:
   same project ⇒ byte-stable PDF, modulo PDF timestamps.

## Options considered

### QuestPDF

A polished .NET PDF library — but it is a *layout engine* with its own fluent document model.
PhotoBook already owns layout (templates + engine); we only need a canvas. Ironically QuestPDF
renders through SkiaSharp internally, so adopting it would wrap our Skia drawing in a second layout
model that fights our templates, and its screen preview is not our editor. Wrong altitude.

### PDFsharp

Draws through its own `XGraphics` API. Every renderer feature (scrims, letterboxing, `CropState`
pan/zoom, image borders) would need a second implementation, and Magick.NET/Skia image objects
would round-trip through GDI+ types. Font embedding works but the code path is entirely separate
from the screen renderer — permanent risk of visual drift.

### Print-to-PDF (WPF printing / XPS → Microsoft Print to PDF)

Zero library cost, but the printer driver controls rasterization DPI, offers no bleed/media-box
control, injects machine-dependent metadata (non-deterministic output), and cannot be driven
headlessly with pinned settings. Fine for proofing on home paper; unacceptable as *the* export.

### SKDocument.CreatePdf (chosen)

Skia's native PDF backend: `SKDocument.CreatePdf(stream, metadata)` returns per-page `SKCanvas`
objects that accept the exact draw calls the screen renderer already issues.

## Decision

> **Decision:** Export through `SKDocument.CreatePdf`, feeding each page's `SKCanvas` to the one
> shared page renderer in `PhotoBook.Rendering`. No second drawing code path exists. Rationale
> inline below; pipeline details are owned by [12-pdf-export.md](../12-pdf-export.md).

Concrete rules:

- **One renderer.** `PageRenderer.Draw(SKCanvas, Page, Style, RenderTarget)` is called with
  `RenderTarget.Screen` and `RenderTarget.Pdf`; the only branch is resolution policy, never geometry.
- **Geometry.** Canvas is sized to the bleed box (11.25 × 8.75 in at 72 pt/in); trim and safe boxes
  come from the constants in the spec kernel and [12-pdf-export.md](../12-pdf-export.md).
- **Images.** Each placed photo is decoded via Magick.NET, adjustments applied, then downsampled to
  its effective placed size at 300 DPI before drawing, and encoded into the PDF as JPEG quality 90,
  sRGB. This caps file size (a 200-page book stays in the hundreds of MB, not GB) and makes pixel
  output independent of source resolution above 300 DPI.
- **Determinism.** `SKDocumentPdfMetadata.Creation` and `.Modification` are pinned to a fixed value
  stored in `book.json` at first export; `RasterDpi = 300`; producer string is constant. Pages are
  emitted in book order; photo downsampling uses a fixed resampling filter. Result: identical
  project folder ⇒ byte-identical PDF. Golden-file tests in
  [13-testing-strategy.md](../13-testing-strategy.md) rely on this.
- **Font subsetting.** All text is drawn with `SKTypeface` instances loaded from the bundled OFL
  font files (never system fonts), so glyph outlines are identical on every machine. Skia's PDF
  backend subsets embedded fonts automatically — only used glyphs ship, keeping the file small and
  the subset stable for byte-comparison. Fallback fonts are disabled at export: a missing glyph is
  a preflight error, not a silent substitution.

## Consequences

- Screen and PDF cannot disagree: any renderer bug appears in both, which is the point.
- Skia's PDF backend writes plain PDF, not PDF/X; color stays sRGB. Print-service specifics live in
  swappable JSON `PrintProfile` files, and a PDF/X post-process can be added later without touching
  the renderer.
- No tagged/accessible PDF and no interactive features — irrelevant for a print interior.
- Text remains vector (crisp at any print size); only photos are rasterized.
- We inherit Skia's PDF backend quirks (e.g., some blend modes rasterize); the renderer restricts
  itself to the safe subset (src-over, plain gradients for caption scrims).
- Per-page JPEG export (some print services want images) is a trivial sibling: same renderer,
  `SKSurface` instead of `SKDocument` — already planned in [14-roadmap.md](../14-roadmap.md).

## Revisit when

- A chosen print service *requires* PDF/X-1a or CMYK — evaluate a post-processing step (e.g. Ghostscript conversion) before evaluating a renderer swap.
- Skia PDF output regresses on a SkiaSharp upgrade (golden-file tests are the tripwire).
- Book file sizes exceed print-service upload limits despite 300 DPI/JPEG-90 policy.
- v2 covers arrive and need spine-width-aware single-canvas output beyond the interior pipeline.
