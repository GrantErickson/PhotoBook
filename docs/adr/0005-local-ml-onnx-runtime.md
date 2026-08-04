# ADR-0005 — Local ML: ONNX Runtime with YuNet, U2-Netp, NIMA + classical metrics

Records the choice of ONNX Runtime as the local inference engine and the specific model lineup
behind Focus Region detection and photo quality scoring. Assumes
[ADR-0001](0001-platform-dotnet.md); sits behind the analyzer plug-in seam described in
[ADR-0010](0010-analysis-plugin-local-first.md).

Related docs: [06-image-analysis.md](../06-image-analysis.md) · [08-auto-layout-engine.md](../08-auto-layout-engine.md)

## Status

Accepted — locked by the user 2026-08-01; recorded 2026-08-03.

## Context

The engine's "awesome by default" promise rests on two analysis outputs: **Focus Regions** so
smart-crop keeps the important parts of a photo visible at any Slot aspect (R25, kernel §4), and a
**QualityScore → Tier** so better photos get bigger Slots (R26, kernel §4). The user wants this
local if possible, with Azure only if it is clearly better (R27) — and these are photos of
children, so local-first is also the right privacy default. Constraints: CPU-only must work (no
GPU assumption), full-year analysis of ~2,000 photos must finish in **< 15 minutes** in the
background (kernel §7), results must be cacheable and reproducible (cache is regenerable,
kernel §5), and everything hides behind `IImageAnalyzer` with `LocalOnnxAnalyzer` as the default
and `AzureVisionAnalyzer` as an opt-in per-book alternative (kernel §10).

## Options considered

| Option | Verdict | One-line summary |
|---|---|---|
| ONNX Runtime + published vision models | **Chosen** | Runs exactly the models we want, CPU-first, tiny API surface |
| ML.NET | Rejected | Wraps ONNX Runtime anyway; pipeline ceremony buys nothing for pure inference |
| OpenCVSharp DNN | Rejected | Second ~100 MB native stack; DNN opset coverage lags ORT |
| Cloud-only (Azure AI Vision) | Rejected as default | Violates local-first (R27); retained as opt-in plug-in |

**ML.NET.** The idiomatic .NET ML story, and good if we were *training* models. Rejected because
for pure inference of third-party ONNX vision models, `Microsoft.ML.OnnxTransformer` is a wrapper
over ONNX Runtime that adds `IDataView` pipeline ceremony, image-preprocessing friction, and a
second versioning axis — with zero capability we can't get from ORT directly.

**OpenCVSharp DNN.** Attractive because OpenCV ships `FaceDetectorYN` (YuNet) natively and its
classical image ops are excellent. Rejected because it drags a second large native stack
(~100 MB) alongside Magick.NET and SkiaSharp, its DNN module trails ONNX Runtime in opset/model
coverage (a problem the day we swap in a newer saliency or aesthetic model), and our classical
metrics are simple enough to implement directly on pixel buffers.

**Cloud-only Azure AI Vision.** Genuinely stronger at face/people analysis and rich tagging, and
zero local compute. Rejected *as the default* because it inverts R27's local-first instruction,
requires connectivity for the core feature, meters cost per image (~$1–2 per 1,000 analyses adds
up across re-scans), and uploads children's photos as a baseline behavior. It survives as the
`AzureVisionAnalyzer` opt-in (per-book setting) so the M1 spike can measure whether its results
beat local fusion enough to recommend it.

## Decision

> **Decision:** `src/PhotoBook.Analysis` uses **`Microsoft.ML.OnnxRuntime`** (CPU execution
> provider by default) inside `LocalOnnxAnalyzer : IImageAnalyzer`, running three small published
> models plus classical metrics; outputs are the canonical `FocusRegion` and `QualityScore`/Tier
> shapes of kernel §4 and are cached in `cache/`.

Model lineup (all inference on the 1024 px cache thumbnail, never full-res):

| Model / metric | Purpose | Input | Output → canonical shape |
|---|---|---|---|
| **YuNet** (~0.3 MB) | Face detection | 640 px long edge | `FocusRegion { kind: face }` + face count/size for the score bonus |
| **U2-Netp** (~4.7 MB) | Saliency | 320×320 | Mask → bounding regions → `FocusRegion { kind: saliency }` |
| **NIMA** (MobileNet, ~13 MB) | Aesthetic score | 224×224 | Mean-opinion score, normalized 0..1 |
| Laplacian variance | Sharpness | grayscale thumb | 0..1 after per-month normalization |
| Histogram analysis | Exposure/clipping | thumb histogram | 0..1 penalty-based score |

- Fusion follows kernel §4 exactly: Focus Region priority `user > person > face > saliency`;
  quality = weighted blend (initial weights `0.5·nima + 0.2·sharpness + 0.15·exposure +
  0.15·faceBonus`, tunable in [06-image-analysis.md](../06-image-analysis.md)) → percentile within
  the month → Tier S/A/B/C. User promote/demote is absolute and never re-derived.
- Budget check: ~450 ms/photo end-to-end on 4 cores meets 2,000 photos in < 15 min; the three
  models total ~18 MB on disk and run comfortably in CPU-only ORT.
- Reproducibility: ORT version pinned, model files content-hashed and shipped with the app,
  graph optimizations pinned to a fixed level — same photo, same scores, so cached analysis and
  the deterministic engine (kernel §7) stay consistent.

## Consequences

- **We gain:** fully offline, private, zero-marginal-cost analysis; freedom to swap any future
  ONNX model without changing the runtime; one small API (`InferenceSession.Run`) to maintain;
  DirectML execution provider available later as a pure speed upgrade.
- **We pay:** local models are worse than cloud state-of-the-art at hard cases (occluded faces,
  aesthetic judgment) — mitigated by the fusion design (user and OneDrive `person` regions outrank
  model output) and by the Azure opt-in; ~18 MB of model files plus ONNX Runtime natives in the
  install; preprocessing code (resize/normalize/letterbox per model) is ours to test — covered by
  golden-output tests in [13-testing-strategy.md](../13-testing-strategy.md).
- Model licensing is compatible with shipping (YuNet MIT, U2-Net Apache-2.0); NIMA weights'
  provenance must be verified before M1 exit — flagged as a checklist item, with a
  MobileNet-based re-train as fallback.

## Revisit when

- The M1 Azure spike shows `AzureVisionAnalyzer` producing materially better layouts on the family
  test corpus — flip the recommended default, keep local as offline fallback.
- A better small model appears (e.g. a stronger open aesthetic scorer or lighter saliency net) —
  drop-in via the same `IImageAnalyzer` seam, requiring only new golden files.
- Analysis blows the 15-minute budget on real libraries — enable the DirectML EP or reduce
  saliency to Tier-S/A candidates only before touching the architecture.
