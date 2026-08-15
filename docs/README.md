# PhotoBook — Design Docs

## Elevator pitch

PhotoBook is a single-user Windows desktop app that mostly-automatically lays out yearly family
photo books — one book per year, one chapter per month — from a folder or OneDrive album of mixed
jpg/heic/webp/png photos plus an optional dated journal in Word, and exports a print-ready,
byte-stable PDF. Local ML finds faces, saliency, and photo quality so the layout engine can crop
smartly, size photos by how good they are, and pace pages by day — and the editor exists only to
nudge the result, not to rescue it. The whole design serves one sentence:

> **"The automatic layout being awesome is the most important part. Edits should really be
> tweaks."** — the user (R27), the north star of
> [01-vision-and-principles.md](01-vision-and-principles.md#north-star)

The 28 numbered requirements this doc set implements live in the repo root
[readme.md](../readme.md); they are cited throughout as **R1–R28** and traced section-by-section
in the [traceability table](#requirements-traceability-r1r28) below.

## Doc index

| Doc | One-line summary |
|---|---|
| [00-spec-kernel.md](00-spec-kernel.md) | The canonical constants, entity names, schemas, and locked decisions every other doc depends on — cited throughout as "kernel §N". |
| [01-vision-and-principles.md](01-vision-and-principles.md) | Why PhotoBook exists, the north star, measurable acceptance targets (C1–C6), design principles, and v1 non-goals. |
| [02-architecture.md](02-architecture.md) | Solution structure, dependency rules, the single-writer threading model, the background job queue, and the memory policy for a 2,000-photo book. |
| [03-domain-model.md](03-domain-model.md) | The entity catalog: Photo, Book/Chapter/Page/Placement, Template, CropState, journal entities, derived bins/trays, and the invariants that bind them. |
| [04-project-format-and-storage.md](04-project-format-and-storage.md) | The on-disk project: JSON folder layout, serialization rules, schema versioning, atomic saves and crash recovery, immutable originals, regenerable cache. |
| [05-ingestion-and-photo-sources.md](05-ingestion-and-photo-sources.md) | Getting photos in: OneDrive/Graph connector, local-folder import, the decode pipeline, the date chain, thumbnail tiers, and the AdjustmentStack. |
| [06-image-analysis.md](06-image-analysis.md) | The IImageAnalyzer contract, the local ONNX pipeline, FocusRegion and QualityScore fusion into Tiers, user overrides, and the optional Azure adapter. |
| [07-layout-template-system.md](07-layout-template-system.md) | Template JSON schema, slot semantics, caption policies, left/right mirroring, multi-day sections, spread pairs, the ~50-template v1 library, and the linter. |
| [08-auto-layout-engine.md](08-auto-layout-engine.md) | The product's core: a pure, deterministic pipeline — day grouping → demand model → DP page partitioning → template scoring → Hungarian slot assignment → smart crop. |
| [09-editor-ux.md](09-editor-ux.md) | The editor: Photos tab (re-date, adjust, exclude), Pages tab (drag/drop, bins, amber flags, layout override), undo model, keyboard map. |
| [10-styles-and-typography.md](10-styles-and-typography.md) | The Style object and its global → chapter → page cascade, bundled fonts, caption scrims, image borders, the black background, month-title type. |
| [11-journal-ingestion.md](11-journal-ingestion.md) | Word journal ingestion: OpenXML parsing, tolerant date matching, segmentation into entries, the import report, re-import semantics, atomic text. |
| [12-pdf-export.md](12-pdf-export.md) | Print output: bleed/trim/safe/gutter geometry, spreads and full-spread photos, image prep, the preflight gate, deterministic byte-stable PDFs. |
| [13-testing-strategy.md](13-testing-strategy.md) | Golden layout tests, determinism tests, property tests for engine invariants, crop math, date-matcher tables, visual regression, CI. |
| [14-roadmap.md](14-roadmap.md) | Milestones M0–M6 with exit-criteria demos, the R1–R28 → milestone map, cross-cutting risks, and what is deferred to v2+. |
| [15-glossary.md](15-glossary.md) | One-line definitions of every capitalized term of art, each linking to its owning doc. |

Architecture Decision Records (in [adr/](adr/), template: [ADR-0000](adr/0000-adr-template.md)):

| ADR | Decision |
|---|---|
| [ADR-0001](adr/0001-platform-dotnet.md) | Platform: .NET 10 (LTS) / C# on Windows. |
| [ADR-0002](adr/0002-ui-wpf.md) | UI: WPF + CommunityToolkit.Mvvm + AvalonDock (dockable bins). |
| [ADR-0003](adr/0003-rendering-skiasharp.md) | One SkiaSharp renderer for screen and PDF — WYSIWYG by construction. |
| [ADR-0004](adr/0004-imaging-magick-net.md) | Magick.NET-Q8 as the sole decode/edit backend (jpg/heic/webp/png…). |
| [ADR-0005](adr/0005-local-ml-onnx-runtime.md) | Local ML: ONNX Runtime with YuNet, U2-Netp, NIMA + classical metrics. |
| [ADR-0006](adr/0006-pdf-skdocument.md) | PDF export via `SKDocument.CreatePdf`. |
| [ADR-0007](adr/0007-project-storage-json-folder.md) | Project storage: human-readable JSON folder, no database. |
| [ADR-0008](adr/0008-journal-openxml.md) | Journal parsing with DocumentFormat.OpenXml — no Word interop. |
| [ADR-0009](adr/0009-app-architecture-mvvm-di-jobs.md) | App architecture: MVVM, DI, command undo, single-writer model, Channels job queue. |
| [ADR-0010](adr/0010-analysis-plugin-local-first.md) | Image analysis: local-first behind an `IImageAnalyzer` plug-in seam. |
| [ADR-0011](adr/0011-onedrive-graph-ingestion.md) | Photo ingestion: OneDrive via MSAL + Microsoft Graph, album-based selection. |

## Recommended reading order

1. **[00](00-spec-kernel.md)** — skim it first and keep it open; it is the reference every other doc cites.
2. **[01](01-vision-and-principles.md)** — the why and the tiebreaker principles.
3. **[ADRs 0001–0011](adr/)** — the technology bets, in numeric order; everything downstream assumes them.
4. **[02](02-architecture.md)** — how the process is shaped: projects, threads, jobs, one renderer.
5. **[03](03-domain-model.md)** — the nouns every later doc uses.
6. **[04](04-project-format-and-storage.md)** — where the nouns live on disk.
7. **[07](07-layout-template-system.md)** — the layout vocabulary (templates, slots, mirroring, spreads).
8. **[08](08-auto-layout-engine.md)** — the engine that is the product; read 07 first or the scoring won't parse.
9. **[05](05-ingestion-and-photo-sources.md)** — how photos and their dates arrive.
10. **[06](06-image-analysis.md)** — how Focus Regions and Tiers are produced for the engine.
11. **[11](11-journal-ingestion.md)** — how journal text arrives and pairs with days.
12. **[09](09-editor-ux.md)** — the tweak surface built on all of the above.
13. **[10](10-styles-and-typography.md)** — the look: styles, fonts, borders, backgrounds.
14. **[12](12-pdf-export.md)** — the end of the pipeline: print geometry and the preflight gate.
15. **[13](13-testing-strategy.md)** — how "awesome" and "deterministic" are enforced.
16. **[14](14-roadmap.md)** — the build order and the R# → milestone map.
17. **[15](15-glossary.md)** — reference; also a fine place to start if a term is unfamiliar.

## Conventions

- **`> **Decision:** …` callouts.** Binding design choices are made inline where the context
  lives, as blockquotes starting with **Decision:** followed by the rationale. Technology-stack
  choices get a full ADR in [adr/](adr/) using the
  [ADR-0000 template](adr/0000-adr-template.md) (Status / Context / Options considered /
  Decision / Consequences / Revisit when).
- **"kernel §N" citations.** Any doc that relies on a shared constant, entity name, or schema
  cites [00-spec-kernel.md](00-spec-kernel.md) by section number rather than restating the
  rationale. The kernel wins every disagreement; a doc that contradicts it is the doc that is
  wrong.
- **R# citations.** `R1`–`R28` cite the numbered requirements in the repo root
  [readme.md](../readme.md) (item *n* = R*n*). Every doc cites the requirements it implements;
  the table below is the reverse index, and
  [14-roadmap.md](14-roadmap.md) maps each R# to the milestone that delivers it.
- **Mermaid diagrams.** Flows, entity maps, and screen maps are ` ```mermaid ` blocks that render
  directly on GitHub — no image files to go stale.
- **Relative links.** Docs cross-reference each other by relative path (`07-layout-template-system.md`,
  `adr/0003-rendering-skiasharp.md`) so the set is navigable offline and in any viewer.

## Requirements traceability (R1–R28)

Every requirement, a short paraphrase, and the section(s) that own it. All 28 have real coverage;
none are gaps.

| R# | Requirement (paraphrase) | Owning section(s) |
|---|---|---|
| R1 | Ingest a provided set of photos in mixed formats (jpg, heic, webp, png, …) | [05 § Local-folder import](05-ingestion-and-photo-sources.md#local-folder-import), [05 § Decode pipeline](05-ingestion-and-photo-sources.md#decode-pipeline), [ADR-0004](adr/0004-imaging-magick-net.md), [ADR-0011](adr/0011-onedrive-graph-ingestion.md) |
| R2 | Optional Word journal with dated entries, interleaved with the photos | [11 § Date detection: the tolerant multi-matcher](11-journal-ingestion.md#date-detection-the-tolerant-multi-matcher), [11 § Segmentation into JournalEntry records](11-journal-ingestion.md#segmentation-into-journalentry-records), [ADR-0008](adr/0008-journal-openxml.md) |
| R3 | Each book contains one year | [03 § 4. Book, Chapter, Page, Placement](03-domain-model.md#4-book-chapter-page-placement), [12 § Export scope](12-pdf-export.md#export-scope) |
| R4 | Each chapter (month) stands alone and is editable — and exportable — as a unit | [03 § 4. Book, Chapter, Page, Placement](03-domain-model.md#4-book-chapter-page-placement), [12 § Export scope](12-pdf-export.md#export-scope) |
| R5 | Templates hold images and text; a day's journal stays together; captions below or overlaid (full-bleed → overlay) | [07 § Caption policies](07-layout-template-system.md#caption-policies), [10 § 4. Captions and the overlay scrim (R5)](10-styles-and-typography.md#4-captions-and-the-overlay-scrim-r5), [11 § Atomic text policy and the engine hook](11-journal-ingestion.md#atomic-text-policy-and-the-engine-hook) |
| R6 | Month grid UI: inspect, reorder, basic edits, and re-dating — which can move a photo to another month or, if the new date leaves the book year, into the **Outside-book tray** (never silently deleted) | [09 § 2. Photos tab](09-editor-ux.md#2-photos-tab) (re-date flow §2.1, adjustments §2.4, grid reorder §2.5), [05 § The date chain and dateUncertain](05-ingestion-and-photo-sources.md#the-date-chain-and-dateuncertain), [05 § AdjustmentStack](05-ingestion-and-photo-sources.md#adjustmentstack), [03 § 9. Derived views: bins, trays, and Spread](03-domain-model.md#9-derived-views-bins-trays-and-spread) (Outside-book tray), [09 § 3.8a Auto-adjust](09-editor-ux.md#38a-auto-adjust-r6-r11), [00 § AdjustmentStack provenance and auto-adjust](00-spec-kernel.md#adjustmentstack-provenance-and-auto-adjust) |
| R7 | Initial automatic layout of a month in chronological order | [08 § 3. Phase 1 — Day grouping](08-auto-layout-engine.md#3-phase-1--day-grouping), [08 § 7. Phase 5 — Hungarian slot assignment](08-auto-layout-engine.md#7-phase-5--hungarian-slot-assignment) (`chronoDisp`) |
| R8 | Pages editable as a single page or a two-page spread | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (Single \| Spread toggle §3.1), [03 § 9. Derived views: bins, trays, and Spread](03-domain-model.md#9-derived-views-bins-trays-and-spread) |
| R9 | Drag/drop with auto-crop to the slot, pan and zoom; if the user under-zooms (`zoom < 1`) the page background shows through as a letterbox | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (drag/drop + swap §3.2, pan/zoom + letterbox §3.3), [03 § 6. CropState — the one crop model](03-domain-model.md#6-cropstate--the-one-crop-model), [08 § 8. Phase 6 — Smart crop: deriving CropState from Focus Regions](08-auto-layout-engine.md#8-phase-6--smart-crop-deriving-cropstate-from-focus-regions) (auto-crop to the slot) |
| R10 | Choosing a template with fewer slots sends displaced photos to the Unplaced bin, docked bottom or side (user's choice) | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (overflow rules §3.4, bin panel + dock choice §3.5), [07 § Detach-on-edit and template swap](07-layout-template-system.md#detach-on-edit-and-template-swap), [ADR-0002](adr/0002-ui-wpf.md) |
| R11 | Advanced image editing (color, etc.) on photos whether placed or in a bin | [05 § AdjustmentStack](05-ingestion-and-photo-sources.md#adjustmentstack), [09 § 2. Photos tab](09-editor-ux.md#2-photos-tab) (Adjust §2.4), [09 § 3.8a Auto-adjust](09-editor-ux.md#38a-auto-adjust-r6-r11) (whole-book and per-photo correction), [03 § AutoAdjustStamp and LookProfile](03-domain-model.md#autoadjuststamp-and-lookprofile) |
| R12 | Choosing a template with more slots: fill the new slots from the Unplaced or Upcoming bin | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (more-slots rule + *Fill from bin…* §3.4, bin panel §3.5), [07 § Detach-on-edit and template swap](07-layout-template-system.md#detach-on-edit-and-template-swap) |
| R13 | The bin shows Unplaced and Upcoming photos; pulling an Upcoming photo forward vacates its later slot, which becomes a flagged amber hole (the source page is deliberately left un-Pinned) | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (§3.5), [03 § 9. Derived views: bins, trays, and Spread](03-domain-model.md#9-derived-views-bins-trays-and-spread) |
| R14 | Empty image slots are flagged as missing | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (amber flagging §3.6), [12 § Preflight gate](12-pdf-export.md#preflight-gate) (empty slots block export) |
| R15 | Per-page layout overrides: move/resize, add/delete image containers, add/remove text containers | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (override mode §3.7), [07 § Detach-on-edit and template swap](07-layout-template-system.md#detach-on-edit-and-template-swap) |
| R16 | "Auto-layout rest of chapter" with an explicit warning, plus "re-layout this day" and "insert pages for Unplaced" | [08 § 10. Pinned, Detached, and the three relayout commands (R16)](08-auto-layout-engine.md#10-pinned-detached-and-the-three-relayout-commands-r16), [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (§3.8) |
| R17 | Removing a photo from the bin excludes it from the book — a permanent tombstone that survives every re-scan/re-sync (re-sync never resurrects it) | [09 § 3. Pages tab](09-editor-ux.md#3-pages-tab) (§3.9), [03 § 3. Photo and its satellite objects](03-domain-model.md#3-photo-and-its-satellite-objects) (tombstone Decision), [05 § OneDrive connector (Microsoft Graph)](05-ingestion-and-photo-sources.md#onedrive-connector-microsoft-graph), [04 § 10. Storage invariants](04-project-format-and-storage.md#10-storage-invariants) |
| R18 | A few layouts support full-page/full-spread photos — supported but not the norm; the ~1 in of image through the binding gutter is an accepted, designed-for loss | [07 § Spread pairs](07-layout-template-system.md#spread-pairs), [12 § Spreads and full-spread photos](12-pdf-export.md#spreads-and-full-spread-photos) |
| R19 | Different page sizes supported | [12 § Other page sizes (R19)](12-pdf-export.md#other-page-sizes-r19) (geometry derived from the profile, not hard-coded), [12 § PrintProfile schema](12-pdf-export.md#printprofile-schema) (`pageSizes`, validated at export), [07 § Template JSON schema](07-layout-template-system.md#template-json-schema) (normalized `[0,1]` coordinates; one template set per `pageSize` id), [ADR-0006](adr/0006-pdf-skdocument.md) |
| R20 | Many templates: up to 8 photos, varied slot aspects/sizes, mostly-full pages plus deliberate negative space, optional text areas — and reflection to left/right pages (mirroring is automatic; the page's side decides) | [07 § The v1 template library](07-layout-template-system.md#the-v1-template-library), [07 § Mirroring: one template, left and right pages](07-layout-template-system.md#mirroring-one-template-left-and-right-pages), [07 § TextSlot semantics](07-layout-template-system.md#textslot-semantics) |
| R21 | Solid black page background for now; image backgrounds later (scrapbook look) | [10 § 6. Background: black now, image later (R21)](10-styles-and-typography.md#6-background-black-now-image-later-r21) |
| R22 | Facing pages considered as a spread, yet each page stays independently editable; matched left/right pair templates (deferrable — lands in M5) | [07 § Spread pairs](07-layout-template-system.md#spread-pairs), [03 § 9. Derived views: bins, trays, and Spread](03-domain-model.md#9-derived-views-bins-trays-and-spread) |
| R23 | Styles: borders on all images at once, global font sizes/colors, journal vs caption sizes independently | [10 § 2. Cascade: global → chapter → page (R23)](10-styles-and-typography.md#2-cascade-global--chapter--page-r23), [10 § 5. Image borders — one switch, every image (R23)](10-styles-and-typography.md#5-image-borders--one-switch-every-image-r23) |
| R24 | Each month opens with a title page — picture options, month name may overlap the image, larger display font | [10 § 7. Month-title typography (R24)](10-styles-and-typography.md#7-month-title-typography-r24), [07 § The v1 template library](07-layout-template-system.md#the-v1-template-library) (`monthTitle` kind), [08 § 2. Pipeline overview](08-auto-layout-engine.md#2-pipeline-overview) (title-page generation) |
| R25 | Detect each photo's interesting areas (Focus Regions), keep them visible when cropping to any aspect, show them in the grid, let the user correct them | [06 § FocusRegion fusion](06-image-analysis.md#focusregion-fusion) (incl. overlay in the photo grid), [06 § User overrides](06-image-analysis.md#user-overrides), [09 § 2. Photos tab](09-editor-ux.md#2-photos-tab) (Focus Region editing UI §2.2), [08 § 8. Phase 6 — Smart crop: deriving CropState from Focus Regions](08-auto-layout-engine.md#8-phase-6--smart-crop-deriving-cropstate-from-focus-regions) |
| R26 | Score photo goodness relative to its peers to drive container size; user can promote/demote | [06 § QualityScore fusion and Tiers](06-image-analysis.md#qualityscore-fusion-and-tiers), [08 § 4. Phase 2 — the Demand Model](08-auto-layout-engine.md#4-phase-2--the-demand-model), [08 § 7. Phase 5 — Hungarian slot assignment](08-auto-layout-engine.md#7-phase-5--hungarian-slot-assignment) (`tierDist` → slot size), [06 § User overrides](06-image-analysis.md#user-overrides), [09 § 2. Photos tab](09-editor-ux.md#2-photos-tab) (promote/demote UI §2.3) |
| R27 | Detection local if possible, Azure if it's better; the automatic layout being awesome matters most — edits are tweaks | [06 § AzureVisionAnalyzer: the optional cloud adapter](06-image-analysis.md#azurevisionanalyzer-the-optional-cloud-adapter), [ADR-0010](adr/0010-analysis-plugin-local-first.md), [ADR-0005](adr/0005-local-ml-onnx-runtime.md), [01 § North star](01-vision-and-principles.md#north-star) |
| R28 | Sparse days (2–3 days of 2–3 photos + short entries) combine onto one page, each day kept together as a unit | [07 § Multi-day section templates](07-layout-template-system.md#multi-day-section-templates), [08 § 5. Phase 3 — DP page partitioning](08-auto-layout-engine.md#5-phase-3--dp-page-partitioning) |
