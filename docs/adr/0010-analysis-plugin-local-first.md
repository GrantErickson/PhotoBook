# ADR-0010 — Image analysis: local-first behind an IImageAnalyzer plug-in seam

This ADR records why image analysis (focus-region detection per R25, quality scoring per R26) runs
locally by default on ONNX Runtime, with Azure AI Vision available as an optional adapter behind
the same interface (R27) — and why the seam, not either backend, is the actual decision.

Related docs: [06-image-analysis.md](../06-image-analysis.md) · [08-auto-layout-engine.md](../08-auto-layout-engine.md) · [02-architecture.md](../02-architecture.md) · [ADR-0005](0005-local-ml-onnx-runtime.md)

## Status

Accepted — 2026-08-01.

## Context

The engine's smart-crop and slot sizing depend entirely on analysis quality: Focus Regions keep the
important parts of a photo visible across aspect-ratio changes (R25), and the fused `QualityScore`
→ Tier drives slot size (R26). The user's stated priority: local algorithms ideally, "but if
better results can be gotten by using AI resources in Azure then I want to do that" (R27) —
automatic layout being awesome outranks purity. Three forces shape the answer:

- **Privacy.** This is years of a family's children's photos. Uploading them anywhere should be an
  explicit opt-in, never a default.
- **Cost.** Azure AI Vision Image Analysis runs roughly $1–1.50 per 1,000 transactions; a 2,000
  photo book with multiple features per photo is real money on every re-analysis if uncached.
- **Offline.** First-import analysis (budget: 2,000 photos in under 15 minutes, background) must
  work on a laptop with no network.

## Options considered

- **Local-only, hard-wired.** Free, private, offline — but forecloses R27's explicit "use Azure if
  better," and cloud person/scene understanding genuinely leads local models in some cases.
- **Cloud-only.** Best raw detection today; fails privacy-by-default, costs per re-run, dies
  offline, and puts the product's core promise at a vendor's mercy. Rejected outright.
- **Plug-in seam, local default, cloud optional (chosen).** The engine consumes analyzer *output*,
  never analyzer identity; backends compete behind one interface.

## Decision

> **Decision:** `PhotoBook.Analysis` defines `IImageAnalyzer`; `LocalOnnxAnalyzer` is the default,
> `AzureVisionAnalyzer` is an optional, opt-in, per-book adapter. The engine and UI depend only on
> the interface. Rationale inline: this is the only shape that satisfies R25/R26 and R27 at once.

```csharp
public interface IImageAnalyzer
{
    string Id { get; }            // "local-onnx" | "azure-vision"
    string ModelVersion { get; }  // cache-key component
    Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken ct);
}

public sealed record AnalysisResult(
    IReadOnlyList<FocusRegion> Regions,   // kind: face | saliency only — never user | person
    QualityMetrics Metrics);              // aesthetic 0..1, sharpness, exposure, face stats
```

- **`LocalOnnxAnalyzer` (default).** ONNX Runtime per [ADR-0005](0005-local-ml-onnx-runtime.md):
  YuNet face detection, U2-Netp saliency, NIMA/MobileNet aesthetic score, plus classical
  sharpness/exposure metrics computed from the decoded pixels.
- **`AzureVisionAnalyzer` (optional).** Azure AI Vision Image Analysis 4.0: people detection and
  caption/quality signals mapped into the same `AnalysisResult`. Enabled per book in `book.json`
  (`analyzerId: "azure-vision"`), off by default; requires the user's own endpoint + key; the
  consent dialog states plainly that photos leave the machine.
- **Analyzers detect; they do not decide.** Fusion happens above the seam, exactly per the domain
  model: FocusRegion priority `user` > `person` > `face` > `saliency`; QualityScore fusion and
  within-month percentile → Tier (S/A/B/C) live in `PhotoBook.Analysis` fusion code, so swapping
  backends never changes ranking semantics. User promote/demote sets Tier absolutely and is never
  re-derived.
- **Caching.** Results are stored in `cache/` keyed by `(contentHash, analyzerId, modelVersion)` —
  derived data only, per [ADR-0007](0007-project-storage-json-folder.md). An Azure re-run on an
  unchanged photo is a cache hit: cloud cost is paid at most once per photo per model version.
  User-authored Focus Regions and Tier overrides live in `photos.json`, never in cache.
- **Jobs.** Analysis runs on the `Analysis` lane of the job queue
  ([ADR-0009](0009-app-architecture-mvvm-di-jobs.md)); the ONNX lane is width 1; Azure calls get a
  4-wide network lane with Retry-After handling.

## Consequences

- Default install is private, free, and offline; the awesome-layout ceiling can still be raised by
  flipping one per-book setting (R27 honored in both directions).
- Two backends means a conformance suite: both run against a labeled family-photo fixture set, and
  fusion tests assert identical downstream behavior for identical `AnalysisResult`s
  ([13-testing-strategy.md](../13-testing-strategy.md)).
- The Azure adapter maps a vendor schema onto ours; vendor API drift is contained to one class.
- Mixed-analyzer books are coherent: the cache key records which analyzer produced each result, and
  the UI badges photos analyzed by a non-current analyzer so the user can re-run consistently.
- A third backend (e.g. a newer local model, or OneDrive-supplied person data flowing in as
  `person` regions via ingestion, [ADR-0011](0011-onedrive-graph-ingestion.md)) slots in without
  engine changes.

## Revisit when

- Side-by-side runs on real family sets show Azure materially beating local on crop quality — consider making opt-in more prominent, never default-on.
- Substantially better local models ship (upgrade `LocalOnnxAnalyzer`, bump `ModelVersion`, cache re-keys itself).
- Azure AI Vision pricing, API, or the Image Analysis 4.0 lifecycle changes.
- Analysis wants richer outputs (pet detection, duplicate clustering) that strain the `AnalysisResult` shape.
