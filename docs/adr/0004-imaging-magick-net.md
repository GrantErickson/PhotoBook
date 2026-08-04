# ADR-0004 — Imaging: Magick.NET-Q8 as the sole decode/edit backend

Records the choice of Magick.NET (Q8 build) as the one and only image decode and pixel-editing
backend, and the Q8-vs-Q16 depth decision. Assumes [ADR-0001](0001-platform-dotnet.md); hands
pixels to the renderer chosen in [ADR-0003](0003-rendering-skiasharp.md).

Related docs: [06-image-analysis.md](../06-image-analysis.md) · [05-ingestion-and-photo-sources.md](../05-ingestion-and-photo-sources.md) · [04-project-format-and-storage.md](../04-project-format-and-storage.md)

## Status

Accepted — locked by the user 2026-08-01; recorded 2026-08-03.

## Context

Source folders mix jpg, heic, webp, and png (R1) — and **HEIC is non-negotiable**: it is the iPhone
default, and a family photo pipeline that can't open half the photos is dead on arrival. Windows
does not guarantee HEIC decode (the HEVC extension is a paid Store install), so the codec must ship
with the app. Beyond decode, the app needs: EXIF orientation applied at decode (kernel §10),
thumbnail tiers at 256 px and 1024 px plus full-res export (kernel §10), and a non-destructive
parametric `AdjustmentStack` — brightness/contrast and crop as basic edits (R6) plus advanced
color adjustments (R11) — always re-applied from immutable originals in `originals/` (kernel §5),
with outputs cached in the regenerable `cache/`. One backend is strongly preferred: two decoders
means two color/orientation behaviors and subtle mismatches between grid thumbnails and rendered
pages.

## Options considered

| Option | Verdict | One-line summary |
|---|---|---|
| Magick.NET-Q8 | **Chosen** | Every format incl. bundled libheif for HEIC + full edit operator set |
| WIC / System.Drawing | Rejected | HEIC depends on per-machine paid codec; System.Drawing is legacy GDI+ |
| SkiaSharp codecs | Rejected | No HEIC; no edit operators — it's a renderer, not an imaging library |
| ImageSharp | Rejected | Nicest managed API, but no HEIC support |

**WIC / System.Drawing.** WIC is fast, ships with Windows, and integrates naturally with WPF.
Rejected because HEIC decode requires the HEVC Video Extensions to be installed per machine — an
unacceptable setup dependency for the app's most important format — and `System.Drawing.Common` is
Windows-locked legacy GDI+ with a threadbare operator set for R11-grade color work.

**SkiaSharp codecs.** Already in the process via [ADR-0003](0003-rendering-skiasharp.md), and its
jpg/png/webp decode is excellent. Rejected as the imaging backend because stock SkiaSharp builds
exclude libheif (no HEIC), and it offers raw pixel access rather than an editing operator library —
we would hand-write levels, curves, and modulate. Skia remains the *consumer* of pixels, never the
producer.

**ImageSharp.** Fully managed (no native payload), pleasant API, good resamplers. Rejected on the
single decisive fact that it does not support HEIC, and secondarily on the Six Labors license split,
which adds friction with no benefit here. Worth rechecking if HEIC support lands (see Revisit).

## Decision

> **Decision:** `Magick.NET-Q8-AnyCPU` is the sole decode/edit backend in `src/PhotoBook.Imaging`.
> **All pixels flow through Magick.NET** (kernel §10): decode with EXIF auto-orient, apply the
> photo's `AdjustmentStack`, then emit either cache thumbnails or an `SKImage` for rendering.

- Bundled ImageMagick natives include **libheif** — HEIC works on a clean Windows install with
  zero OS codec dependencies (R1).
- One pipeline, one behavior: color handling, orientation, and resampling (Lanczos for thumbnail
  tiers) are identical for the 256 px grid, the 1024 px layout preview, and full-res export.
- `AdjustmentStack` maps directly onto ImageMagick operators (brightness/contrast, modulate,
  levels, gamma, saturation, white balance) and is *parametric*: stored as JSON in `photos.json`
  (kernel §5), re-applied from the original in a single pass — originals stay immutable.
- Exactly one conversion seam to the renderer: Magick pixel buffer → `SKImage` (BGRA8888,
  premultiplied), owned by one function in `PhotoBook.Imaging` so color/format bugs have one home.

### Q8 vs Q16

Q8 (8 bits per channel) over Q16, deliberately:

- Both ends of the pipeline are 8-bit: consumer JPEG/HEIC/WebP sources in, sRGB JPEG-in-PDF at
  quality 90 out (kernel §3). Q16's extra precision would be manufactured on decode and discarded
  on encode.
- Q8 halves working-set memory and is measurably faster — material when background-analyzing and
  thumbnailing ~2,000 photos/year (kernel §2, §7).
- The classic Q8 risk — banding from *iteratively saved* tonal edits — does not apply: the
  `AdjustmentStack` is re-applied from the original in one pass; precision loss never accumulates.

## Consequences

- **We gain:** HEIC everywhere with no user setup; one operator library covering R6 and R11 today
  and headroom (curves, CLAHE, tint) for future "advanced characteristics" work; consistent pixels
  from grid to PDF.
- **We pay:** a ~30–40 MB native payload (accepted, per [ADR-0001](0001-platform-dotnet.md));
  ImageMagick's security surface — mitigated by shipping a locked-down `policy.xml` (delegates and
  network coders disabled; only the R1 format coders enabled; resource caps on width/height/memory);
  Q8 rules out meaningful RAW/16-bit editing — RAW is explicitly out of scope for v1.
- Decode of large HEICs is the slowest step in ingestion; it runs on the background job queue
  (kernel §8) and results are cached, so the UI never blocks on it.

## Revisit when

- RAW or 16-bit source support becomes a requirement — that reopens Q16 (or a hybrid decode path),
  not just a package swap.
- ImageSharp ships supported HEIC decode — re-evaluate for the native-payload and security-surface
  win, contingent on operator parity for the `AdjustmentStack`.
- Profiling shows Magick decode dominating first-import time beyond the 15-minute full-year
  analysis budget (kernel §7) — consider a dedicated libheif-sharp fast path for thumbnails only,
  without changing the edit backend.
