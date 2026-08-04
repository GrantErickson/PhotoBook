# 14 — Roadmap

This doc sequences PhotoBook from empty repo to a shipped v1 in seven milestones, M0–M6. Each milestone lists its scope, the requirements (R1–R28 from the repo `readme.md`) it lands, explicit exit criteria phrased as a demo, and the risks it carries. The ordering enforces the north star — *"the automatic layout being awesome is the most important part; edits should really be tweaks"* — so the engine is built and judged **before** any interactive editing exists.

Related docs: [01-vision-and-principles.md](01-vision-and-principles.md) · [02-architecture.md](02-architecture.md) · [05-ingestion-and-photo-sources.md](05-ingestion-and-photo-sources.md) · [08-auto-layout-engine.md](08-auto-layout-engine.md) · [12-pdf-export.md](12-pdf-export.md) · [13-testing-strategy.md](13-testing-strategy.md)

## Milestone map

> **Decision:** The auto-layout engine (M1) ships before the editor (M3). If the engine's output is mediocre, editing tools just make the user hand-build the book — the exact failure mode this project exists to avoid (R27's "automatic layout being awesome" mandate). The engine is judged on rendered PDFs, which M0 already produces, so no UI is needed to evaluate it. Inline rationale; sequencing, not architecture.

```mermaid
flowchart LR
  M0[M0 walking skeleton] --> M1[M1 awesome engine]
  M1 --> M2[M2 photo viewer + Graph]
  M2 --> M3[M3 tweak editing]
  M3 --> M4[M4 journal and text]
  M4 --> M5[M5 polish]
  M5 --> M6[M6 ship]
```

| Milestone | Theme | Primary R# landed |
|---|---|---|
| M0 | folder → fixed template → PDF | R1, R3, R7 (naive), R21 |
| M1 | analysis + template library + DP layout; Graph spike | R5, R7, R18, R20, R24, R25, R26, R27, R28 |
| M2 | photo viewer + Graph connector | R1, R6, R25 (edit), R26 (edit) |
| M3 | tweak editing: drag/swap, crop, bins | R8, R9, R10, R11, R12, R13, R14, R16, R17 |
| M4 | journal & text | R2, R5 (captions live), R24 (title text) |
| M5 | polish: styles, page overrides, multi-day, spreads | R15, R19, R22, R23, R28 (multiDay templates) |
| M6 | ship: preflight, hardening | R14 (preflight), export quality across all |

Every milestone ends green on the [13-testing-strategy.md](13-testing-strategy.md) PR gate; tests are written inside the milestone that lands the behavior, not deferred.

## Requirement traceability (R1–R28 → milestone)

Where each requirement *fully* lands (earlier milestones may land partial groundwork, noted in the milestone sections):

| R# | Requirement (short) | Lands |
|---|---|---|
| R1 | mixed-format photo folder (jpg/heic/webp/png) | M0 decode, M2 sources complete |
| R2 | dated Word journal interleaved | M4 |
| R3 | one book = one year | M0 |
| R4 | Chapters stand alone, editable as a unit | M0 format, M3 editing |
| R5 | templates with images/text; captions below or overlay | M1 slots, M4 live |
| R6 | month photo UI: inspect, reorder, re-date, basic edits | M2 |
| R7 | initial chronological auto-layout | M0 naive, M1 real |
| R8 | edit as single page or Spread | M3 |
| R9 | drag/drop, auto-crop, pan, zoom, background show-through | M3 |
| R10 | change page layout; extras to Unplaced bin | M3 |
| R11 | advanced edits on placed or binned photos | M3 |
| R12 | fill bigger layouts from Unplaced/Upcoming bins | M3 |
| R13 | bin shows unused + upcoming photos | M3 |
| R14 | empty slots flagged | M3 amber flag, M6 preflight |
| R15 | per-page layout overrides (add/change/delete slots) | M5 |
| R16 | auto-layout rest of chapter, with warning | M3 |
| R17 | remove from bin ⇒ excluded from book | M3 |
| R18 | full-page/full-Spread photo layouts | M1 full-bleed, M5 spreads |
| R19 | different page sizes | M0 data model, M5 exercised |
| R20 | large template library, up to 8 photos, mirroring, textless | M1 |
| R21 | solid black background v1 | M0 |
| R22 | matched two-page Spread layouts (deferrable) | M5 (may slip per R22) |
| R23 | global styles: borders, fonts, sizes, colors | M5 |
| R24 | month title page, oversized type | M1 layouts, M4 typography |
| R25 | focus-area detection + smart crop, user-adjustable | M1 auto, M2 editing |
| R26 | photo goodness drives slot size, promote/demote | M1 auto, M2 editing |
| R27 | local-first analysis, optional Azure | M1 local, M2 adapter |
| R28 | sparse days combine onto one page | M1 merging, M5 multiDay templates |

## M0 — Walking skeleton

**Scope.** `PhotoBook.sln` with all seven projects and the dependency rule enforced ([02-architecture.md](02-architecture.md)). Local folder import: decode jpg/heic/webp/png via Magick.NET with EXIF orientation and the date chain (EXIF → file mtime; `dateUncertain` flagged). Project folder format created and round-tripped (`book.json`, `photos.json`, `chapters/*.json`, `originals/`, `cache/`). One hard-coded 4-photo template; naive chronological fill with `zoom = 1.0` center crops; SkiaSharp render of the 11 × 8.5 in landscape page with bleed, black background; PDF out via `SKDocument.CreatePdf` at 300 DPI / JPEG q90 / sRGB. CI pipeline live from day one.

**Lands:** R1 (formats decode), R3 (one book = one year), R7 (chronological, naive), R21 (black background). Groundwork for R19 (page size is data, not code).

**Exit criteria — demo:** point the app (CLI shell is fine; no real UI yet) at a folder of ~200 mixed-format photos including HEIC; it produces `MyBook-2024/` and a multi-page PDF, every photo present in date order, correct orientation, bleed boxes verified in Acrobat. Deleting `cache/` and re-running reproduces the identical PDF (byte-stable, timestamps pinned).

**Risks.** HEIC decode via bundled libheif is the one true unknown in the stack — prove it in week one on real phone files. Atomic-write/rename semantics on OneDrive-synced folders can misbehave; test with the project folder inside a synced directory early.

## M1 — Awesome engine

**Scope.** The whole quality bet, judged on PDFs. Local analysis pipeline (`LocalOnnxAnalyzer`): YuNet faces, U2-Netp saliency, NIMA + classical metrics fused into `QualityScore` → month-percentile Tiers S/A/B/C; background job queue does a full-year analysis in < 15 min. Template library authored to the v1 target (~50 templates across 1–8 photos, month titles, full-bleed, textless negative-space layouts) with the linter enforcing it. Full engine per [08-auto-layout-engine.md](08-auto-layout-engine.md): Day Grouping → Demand Model → DP page partitioning (sparse days merge per R28, burst days split) → template scoring → Hungarian slot assignment → smart-crop emitting `CropState`. Golden, determinism, and property test suites land here. **Graph people-tag spike** (below) runs in parallel.

**Lands:** R5 (template caption slots exist), R7 (real), R18 (full-bleed templates), R20, R24 (month-title layouts), R25, R26, R27 (local-first analysis), R28 (day merging in DP).

**Exit criteria — demo:** feed one real family month (~150 photos) with recorded analysis and show a generated chapter PDF the user would *keep*. Checklist:

- No face cropped by an auto `CropState`; primary Focus Region visible in every slot (R25).
- Tier-S photos visibly larger than Tier-C on the same page (R26).
- Sparse days share pages; the 60-photo burst day splits cleanly (R28).
- Month title page present with a photo under the title (R24); pacing varies (no three identical consecutive templates).
- Full-year re-layout of 2,000 synthetic photos < 10 s; same Seed twice ⇒ identical chapter JSON.
- **The gate number:** user reviews three months of real output and scores **≥ 8/10 pages "needs no edit."**

**Risks.** Highest-risk milestone by design. NIMA scores may not match family taste — budget a fusion-weight tuning loop against user-ranked photo sets. DP + Hungarian interplay can produce monotone pacing; the variety penalty needs real-photo iteration. Timebox: if template scoring quality stalls, cut library breadth (not depth) — 30 excellent templates beat 50 mediocre ones.

### Graph people-tag spike (inside M1)

Two-week spike, per [ADR-0011](adr/0011-onedrive-graph-ingestion.md) and [05-ingestion-and-photo-sources.md](05-ingestion-and-photo-sources.md): sign in with MSAL, pull a real OneDrive photo set, and answer with evidence: (a) are people tags exposed to third-party apps via Graph at all, (b) at what fidelity (named region rects vs bare names vs nothing), (c) at what throttling cost for ~2,000 items? **Outcome gates scope:** full rects → `FocusRegion { kind: person }` becomes the top-priority focus signal in M2; names-only → tags become search/filter metadata only; nothing → the documented fallback is local YuNet faces only, and the feature is cut without redesign (the `FocusRegion` fusion order already anticipates absence).

## M2 — Photo viewer + Graph connector

**Scope.** First real UI: WPF shell (MVVM, AvalonDock), Book dashboard, Month workspace **Photos** tab — 256 px thumbnail grid, inspect, reorder, re-date (moves across Chapters or to the Outside-book tray), Tier promote/demote (absolute override), Focus Region overlay editing, basic adjustments (brightness/contrast/crop) on the non-destructive AdjustmentStack. OneDrive connector: MSAL sign-in, album sync ("Book 2024"), originals copied content-hash-named into `originals/`; local-folder source kept as a peer. People tags wired per the spike outcome.

**Lands:** R1 (sources complete), R6 (full), user-editable halves of R25/R26, R27 (Azure adapter stub behind `IImageAnalyzer`, opt-in setting).

**Exit criteria — demo:** the user's wife adds photos to a "Book 2024" OneDrive album from her phone; the app syncs it, shows the month grid with Tier badges and face/saliency overlays; re-dating a photo moves it to the right Chapter (or the Outside-book tray); demoting a photo and re-running layout shrinks it on the page. Album re-sync neither duplicates photos nor resurrects excluded ones.

**Risks.** Graph throttling on bulk download (mitigate: delta queries + resumable copy). MSAL token cache on a shared family PC. UI virtualization for a 500-photo grid must stay at 60 fps — build the grid on `VirtualizingPanel` from the start.

## M3 — Tweak editing

**Scope.** The **Pages** tab: single-page and Spread views (R8); drag-drop between slots with swap semantics; pan/zoom crop editing on the shared `CropState` model including `zoom < 1` letterboxing with background show-through (R9); template picker per page with overflow to the Unplaced bin (R10); dockable bin panel showing Unplaced + Upcoming, user-positioned bottom or side (R13); fill-from-bin into roomier templates (R12); empty slots flagged amber (R14); exclude-from-book with persistence across re-sync (R17); advanced image adjustments from either surface (R11); Pinned-page semantics and "Auto-layout rest of chapter" with the affected-pages warning (R16); undo/redo command stack throughout.

**Lands:** R8, R9, R10, R11, R12, R13, R14, R16, R17.

**Exit criteria — demo:** open an M1-generated month and perform one continuous editing session:

- Swap two photos by dragging (swap semantics, R9); pan a face into view inside its slot.
- Zoom one photo below 1.0 and see the black background letterbox through (R9).
- Switch a 6-photo page to a 4-photo template; two photos land in the Unplaced bin (R10); pull an Upcoming photo back onto the current page (R12/R13).
- Leave one slot empty — it flags amber (R14); exclude a duplicate from the bin, re-sync, it stays gone (R17).
- Hit "Auto-layout rest of chapter": the warning lists exact page numbers; Pinned pages provably untouched (R16 — the property test in [13-testing-strategy.md](13-testing-strategy.md) is the oracle).
- Ctrl+Z walks the entire session back to the opening state.

**Risks.** Undo/redo across drag-drop + crop + relayout is the largest UI-state surface in the app; command-pattern discipline from the first commit, no "fix undo later." Drag-drop hit-testing over a zoomed SKElement canvas needs a prototype before committing to interaction details.

## M4 — Journal & text

**Scope.** Word `.docx` ingestion via OpenXML; tolerant multi-format date matcher with table-driven tests; Import Report UI for unmatched entries with manual assignment (R2). Journal text flows into template text slots — a day's entry is atomic, may span the two pages of one Spread, never crosses Spreads; the Demand Model gains text-length pressure so wordy days get roomier templates. Captions live: below-image and overlay-on-scrim per `captionPolicy` (R5). Month-title typography per [10-styles-and-typography.md](10-styles-and-typography.md) (R24 complete).

**Lands:** R2, R5 (complete), R24 (complete).

**Exit criteria — demo:** import the family's real (messy) journal for a year; the Import Report shows match rate and the user assigns stragglers by hand; regenerated chapters interleave entries with the right days' photos; a long entry spans a Spread without crossing into the next; one full-bleed page shows an overlay caption on the scrim. Match rate on the real journal ≥ 95% before manual fixes.

**Risks.** Real journals are the risk: date formats drift mid-year, entries nest in tables or lists. Mitigation is built-in — every miss becomes a matcher-table row, and the Import Report makes failure visible instead of silent. Text measurement (SkiaSharp shaping) must be finalized here or Demand Model text-pressure numbers are fiction.

## M5 — Polish

**Scope.** `Style` cascade global → chapter → page: image borders everywhere, font families/sizes/colors, journal vs caption sizes independent (R23). Per-page layout overrides: add/move/resize/delete image and text slots, Detaching the page to an inline template snapshot validated by the linter (R15). `multiDay` sectioned templates ship (R28's dedicated layouts). Matched left/right `spreadPair` templates and full-Spread photos honoring the gutter caution zone (R18, R22 — R22 is explicitly deferrable per the requirement if quality is at risk). Additional `pageSize` presets exercised end-to-end (R19).

**Lands:** R15, R19, R22 (target, may slip by its own terms), R23, R28 (template dimension).

**Exit criteria — demo:** flip one style toggle and every image on every page gains a 2 pt border; change journal font size globally without touching captions; hand-add a slot to one page and confirm the library template is unmutated and re-layout leaves the Detached page alone; a 22 × 8.5 panorama Spread renders with no face in the gutter zone; generate the same book at a second page size.

**Risks.** Slot-editing UI can swallow unlimited time — constrain to rect add/move/resize/delete, no rotation, no z-order UI in v1. Spread pairs interact with the DP partitioner (two pages consumed atomically); if pairing destabilizes M1 quality gates, invoke R22's deferral clause and ship without them.

## M6 — Ship

**Scope.** Preflight gate before export: empty slots, effective DPI < 200, text overflow, non-empty Unplaced bin, `dateUncertain` photos — each with a jump-to-page fix affordance ([12-pdf-export.md](12-pdf-export.md)). Print-profile JSON validated against at least two real print services with physical proof copies. Hardening: crash-safe autosave/`.bak` recovery drill, 2,000-photo endurance run, cache-delete/rebuild drill, installer (MSIX or Squirrel — decide here), versioned `schemaVersion` migration scaffolding. Full nightly suite green for two consecutive weeks.

**Lands:** R14 (preflight dimension); ship-quality across all previously landed requirements.

**Exit criteria — demo:** the real thing — the user produces the complete 2024 family book end-to-end:

- Album sync → engine → tweaks → journal → preflight shows zero blockers → PDF export.
- Upload to a print service with **zero manual PDF surgery**; order the physical book.
- The proof arrives correct: Bleed trimmed clean, blacks solid, faces sharp, nothing lost in the gutter, journal text readable at arm's length.
- Crash-recovery drill passes: kill the app mid-edit, relaunch, autosave/`.bak` restores within one autosave interval (30 s) of work.

That physical book *is* v1 acceptance.

**Risks.** Print-service rejection cycles are slow (days per proof) — order the first proof from M5 output, not M6. Preflight thresholds (DPI 200) need validation against the physical proof, not just on-screen judgment.

## Settled: album selection

> **Decision:** Photo selection is **Album + in-app refine** — the curator adds photos to a per-book OneDrive album ("Book 2024") from phone or desktop web, the app syncs it, and final trims happen in the in-app grid; folder-as-source remains supported as a peer path. Confirmed by the user on 2026-08-03, so M2 builds album sync as the primary path — see [ADR-0011](adr/0011-onedrive-graph-ingestion.md) and [05-ingestion-and-photo-sources.md](05-ingestion-and-photo-sources.md). The one remaining unknown here is the Graph **people-tag** spike in M1, which affects only Focus Region quality, not the selection workflow.

## Cross-cutting risks

Risks that don't belong to one milestone, tracked at every exit review:

- **Engine quality is the product risk.** Everything after M1 assumes the "≥ 8/10 pages need no edit" bar was honestly met on *real family photos*, not fixtures. If M1 exits soft, stop and iterate — M3's editor cannot compensate (north star, R27).
- **Determinism erosion.** Every milestone adds engine-adjacent code; the determinism and golden suites ([13-testing-strategy.md](13-testing-strategy.md)) are the tripwire, and any parallelism added for perf must preserve ordered reduction.
- **Schema drift.** `photos.json` / `chapters/*.json` evolve at nearly every milestone. `schemaVersion` bumps with migration code start at M2 (first milestone where a real family project exists that must survive an upgrade).
- **One-user bus factor on taste.** Tier fusion weights, template variety penalties, and typography defaults all tune against one family's judgment; record the ranked-photo calibration sets used, so retuning after a model swap is reproducible.
- **Model licensing/availability.** YuNet, U2-Netp, and NIMA weights are fetched, not committed; `models.lock.json` pins hashes ([06-image-analysis.md](06-image-analysis.md)) so a upstream re-upload can't silently change analysis output.

## Deferred to v2+

Documented non-goals for v1 — hooks exist, features do not:

- **Cover design** — v1 exports interior PDF only; `PrintProfile` reserves spine-width calculation for when covers arrive.
- **Scrapbook/image page backgrounds** — v1 is solid `#000000` (R21's "for now"); the renderer treats background as a paint layer so image backgrounds slot in later.
- **RAW formats** — Magick.NET can read many, but RAW color pipelines deserve dedicated design; v1 sources are pre-culled phone/camera JPEG/HEIC/WebP/PNG.
- **Multi-user / collaboration** — single-user by locked decision; the shared-album workflow already gives the family a lightweight contribution path.
- Also parked: per-page JPEG export, video stills, non-English journal date matching.
