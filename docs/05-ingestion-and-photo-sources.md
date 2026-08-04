# 05 — Ingestion and Photo Sources

This doc specifies how photos get into a PhotoBook project: the OneDrive connector (MSAL sign-in,
album-based selection, people-tag pull, delta re-sync), the local-folder import path, the
Magick.NET decode pipeline (HEIC included), the date chain that assigns every Photo to a Chapter,
thumbnail tier generation, the non-destructive `AdjustmentStack`, and the cache invalidation
matrix that keeps derived pixels honest. Everything here lives in `src/PhotoBook.Ingestion` and
`src/PhotoBook.Imaging`; both depend only on `PhotoBook.Core`.

Related docs: [02-architecture.md](02-architecture.md) ·
[03-domain-model.md](03-domain-model.md) ·
[04-project-format-and-storage.md](04-project-format-and-storage.md) ·
[06-image-analysis.md](06-image-analysis.md) ·
[09-editor-ux.md](09-editor-ux.md) ·
[11-journal-ingestion.md](11-journal-ingestion.md) ·
[12-pdf-export.md](12-pdf-export.md) ·
[14-roadmap.md](14-roadmap.md)

## Where ingestion sits

Ingestion turns a photo source (OneDrive album, OneDrive folder, or local folder) into three
durable outputs: immutable files in `originals/`, catalog entries in `photos.json`, and queued
background work (thumbnails, then analysis — see [06-image-analysis.md](06-image-analysis.md)).
The user's raw material per R1 is "a folder of photos … in different formats: jpg, heic, webp,
png, etc."; ingestion is the only code that touches a source system, and nothing downstream ever
reads from OneDrive or the user's source folder again — the project folder is a self-contained
archive.

```mermaid
flowchart LR
  A[OneDrive album / folder\nor local folder] --> B[Enumerate + diff\nagainst photos.json]
  B --> C[Download / copy bytes]
  C --> D[SHA-256 content hash]
  D --> E[originals/hash-name.ext\nimmutable]
  E --> F[Decode: Magick.NET\nauto-orient, sRGB]
  F --> G[EXIF + Graph metadata\ndate chain]
  G --> H[photos.json entry]
  F --> I[Thumbnail tiers\n256 / 1024]
  H --> J[Analysis job queue\nsee doc 06]
```

Every step after "enumerate" runs on the background job queue
([09-editor-ux.md](09-editor-ux.md)); the UI stays responsive and photos appear in the grid as
they land, current month first.

## OneDrive connector (Microsoft Graph)

> **Decision:** Photos come from consumer OneDrive via Microsoft Graph with MSAL sign-in;
> selection model is **Album + in-app refine**; a local-folder path is the fallback and always
> works offline — see [ADR-0011](adr/0011-onedrive-graph-ingestion.md).

### Authentication (MSAL)

- **Library:** `Microsoft.Identity.Client` (MSAL.NET), public client application.
- **Authority:** `https://login.microsoftonline.com/consumers` (personal Microsoft accounts —
  this is a family-photos app; work/school accounts are out of scope for v1).
- **Scopes:** `User.Read`, `Files.Read`. Read-only by design — PhotoBook never writes to
  OneDrive, so no write scope is ever requested.
- **Flow:** WAM broker (`WithBroker`) first for silent Windows SSO; interactive system-browser
  fallback. `AcquireTokenSilent` on every sync; interactive prompt only when the refresh token is
  dead.
- **Token cache:** MSAL cache serialized to `%LOCALAPPDATA%\PhotoBook\msal.cache`, encrypted with
  DPAPI (current user). Tokens and client secrets never enter the project folder — `book.json`
  and friends must stay shareable and human-diffable.
- **Sign-out:** clears the MSAL cache; the project keeps working because originals are local.

### Album-based selection ("Album + in-app refine")

The selection workflow, confirmed by the user on 2026-08-03 (kernel §2):

1. The user (in practice: either spouse, from a phone or desktop web) adds candidate photos to a
   per-book OneDrive album, e.g. **"Book 2024"**, over the course of the year or in one sitting.
2. In PhotoBook, *New Book → OneDrive → pick album*. Albums are OneDrive **bundles**:
   `GET /me/drive/bundles?$filter=bundle/album ne null` lists them;
   `GET /me/drive/items/{albumId}/children?$top=200` pages the members (each is a normal
   `driveItem` with `photo`, `image`, and `file` facets).
3. The app syncs the album (download to `originals/`, below) and the photos appear in the Photos
   tab grid.
4. **In-app refine:** final trims happen in the grid, not in OneDrive. Removing a photo there
   sets `excluded: true` in `photos.json` (R17). Exclusion is user intent: it lives in
   `photos.json`, never in `cache/`, and it survives every future re-sync.

Why album-first: it moves curation to where the photos already are (the phone), keeps the album
as a natural shared workspace, and means the in-app grid starts from a pre-culled set well under
the ~2,000-photo/year budget.

### Folder browse fallback

If the user doesn't use albums: *New Book → OneDrive → pick folder* browses the drive tree
(`GET /me/drive/root/children`, then `…/items/{id}/children`). Selecting a folder syncs it,
**recursively by default** (toggle in the picker). Folder-as-source and album-as-source are the
same pipeline after enumeration; a book records exactly one source
(`source: { kind: "onedriveAlbum" | "onedriveFolder" | "localFolder", id, path }`).

### People-tag pull

Consumer OneDrive's Photos experience tags people in photos. The kernel decision is to pull those
tags and use them as **named, top-priority Focus Regions** (`FocusRegion.kind = "person"`,
`personName` set — fusion priority `user > person > face > saliency`, see
[06-image-analysis.md](06-image-analysis.md)). During enumeration the connector requests the
people/tag metadata for each item and stores whatever comes back on the Photo as `PersonTag`
entries in `photos.json`:

```jsonc
"personTags": [ { "name": "Nora", "rect": { "x": 0.61, "y": 0.18, "w": 0.14, "h": 0.22 } } ]
// rect: normalized image coords, top-left origin; omitted if Graph gives name only
```

Whether Graph actually exposes this metadata is an open question — see the spike below. The
connector is written against an internal `IPeopleTagProvider` so the answer changes one adapter,
not the pipeline.

### Download to originals: content-hash naming

> **Decision:** Originals are copied into the project folder and named by content hash; they are
> immutable from the moment they land — see
> [ADR-0007](adr/0007-project-storage-json-folder.md).

- Bytes stream from the item's `@microsoft.graph.downloadUrl` (no auth header needed; URL is
  short-lived, re-fetched per sync). 4 parallel downloads; resume via HTTP `Range` on retry.
- **Pre-download skip:** consumer OneDrive supplies `file.hashes.sha256Hash`. If a catalog entry
  already holds that hash, the bytes are never fetched.
- **Hash:** SHA-256 of the file bytes, computed locally while streaming and verified against the
  Graph-supplied hash when present (mismatch ⇒ re-download once, then surface in the Import
  Report).
- **Filename:** first 16 hex chars of the SHA-256, a dash, then the sanitized original filename
  with its extension: `originals/3f9a1c2b8d4e6f70-IMG_2034.heic`. If the 16-char prefix collides
  with different content (astronomically unlikely), the full 64-char hash is used instead.
- **Immutability:** files under `originals/` are written once via temp-file + rename and never
  modified. All edits are parametric (`AdjustmentStack`, `CropState`); all derived pixels live in
  `cache/`.
- The catalog entry records `sourceId` (Graph `driveItem` id), `contentHash` (full SHA-256),
  `originalFileName`, and the `originals/` relative path.

### Delta re-sync

*Sync* is a button (and runs automatically on book open when the source is OneDrive and the
machine is online). Semantics:

- **Enumeration:** album children are re-listed in full each sync (bundles don't support `delta`;
  ≤ 2,000 pre-culled items paged at 200/page is a handful of requests). Folder sources use
  `GET …/items/{folderId}/delta` with the stored `deltaLink` when available, full re-list
  otherwise.
- **Matching:** incoming items match catalog entries by `sourceId` first, `contentHash` second
  (the hash match catches OneDrive-side moves/renames that change the item id).
- **New item** → download, catalog, thumbnail, analyze; it lands in the Unplaced bin of its
  Chapter, and the Import Report lists it.
- **Excluded item re-appears** (still in the album, or removed and re-added): `excluded: true`
  **wins, always**. Re-sync never resurrects an excluded photo (R17); the kernel rule is
  "excluded photos stay excluded across re-scans/re-syncs". The Import Report notes
  "3 excluded photos skipped" so the behavior is visible, not silent.
- **Item removed from album/folder** → the Photo is *not* deleted. It's flagged
  `removedFromSource: true` and listed in the Import Report; the original stays archived and any
  page placement stands. Deleting from the book is always an explicit in-app act.
- **Item modified at source** (same `sourceId`, new hash — someone edited it in OneDrive) → the
  archived original is kept as-is (immutable), the Photo is flagged `sourceModified: true`, and
  the Import Report offers per-photo **Re-import**. Re-import swaps in the new bytes under the
  new hash, keeps the photo's identity, dates, adjustments, tier override, and placements, and
  rebuilds its caches (new hash ⇒ every cache key changes naturally).
- Re-sync never touches: user-set dates, `AdjustmentStack`, user Focus Regions,
  `userTierOverride`, placements, Pinned pages. User intent lives in project JSON only.

## SPIKE: Graph people-tag availability

Flagged honestly (kernel §10, and scheduled in M1 per [14-roadmap.md](14-roadmap.md)): it is
**unverified** whether consumer OneDrive people tags are readable through Microsoft Graph at all.
Scope of the spike — answer four questions against a real family OneDrive:

1. **Exposure:** does any v1.0 or beta Graph surface (driveItem facet, `$expand`, listItem
   fields, or the legacy OneDrive `tags` facet) return people tags for a photo?
2. **Shape:** names only, or names **with bounding boxes**? (Boxes make tags first-class Focus
   Regions; names-only degrades to a naming/bonus signal — see
   [06-image-analysis.md](06-image-analysis.md).)
3. **Reach:** are tags present on album (bundle) children identically to folder children?
4. **Access:** does `Files.Read` suffice, and is latency sane at ~200 items/page?

Exit criteria: a short written go/no-go with sample payloads. **Fallback (fully functional
either way):** local YuNet face detection only — unnamed `face` regions, no `person` regions,
smart-crop and Tier bonuses work unchanged. The fallback is the floor, not a degraded app; the
spike only decides whether faces get names for free.

## Local-folder import

The no-cloud path (R1), and the offline fallback:

- *New Book → Local folder*. Recursive scan (toggle), accepting: `.jpg` `.jpeg` `.png` `.heic`
  `.heif` `.webp` `.tif` `.tiff` `.bmp`. Anything else is listed in the Import Report as skipped.
- Files are **copied** (never moved) into `originals/` with the same content-hash naming; the
  source folder is never written to.
- Identity across re-scans is `contentHash` (there is no stable source id); a re-scan of the
  folder follows the same delta semantics as OneDrive, including the excluded-stays-excluded
  rule.
- No people tags exist on this path ⇒ local face detection only (the spike fallback is this
  path's normal mode).
- Drag-and-drop of files/folders onto the Photos tab funnels into the same importer.

## Decode pipeline

> **Decision:** Magick.NET-Q8 is the sole decode/edit backend. It bundles libheif, so HEIC/HEIF
> decodes on any Windows box without OS codec packs — no HEVC Store extension roulette — see
> [ADR-0004](adr/0004-imaging-magick-net.md).

Every pixel in the app flows through one function in `PhotoBook.Imaging`:

1. **Read** bytes from `originals/` into a `MagickImage`. Q8 (8-bit/channel) is deliberate: the
   output target is a JPEG-in-PDF book at quality 90; 16-bit fidelity buys nothing here and
   halves memory.
2. **Orient:** `AutoOrient()` applies the EXIF orientation tag and resets it. Downstream code —
   thumbnails, analysis, renderer, export — only ever sees upright pixels. Orientation is
   applied at decode, exactly once, nowhere else.
3. **Color:** if an embedded ICC profile is present (Apple's Display P3 HEICs, CMYK JPEGs),
   transform to **sRGB** and strip the profile; if absent, assume sRGB. The whole pipeline —
   screen via SkiaSharp, PDF export per [12-pdf-export.md](12-pdf-export.md) — is sRGB, so
   conversion happens once, here.
4. **Metadata** is extracted *before* transforms: EXIF `DateTimeOriginal` (+ `SubSecTimeOriginal`
   for intra-second ordering), pixel dimensions, camera model. Stored in `photos.json`.
5. **Apply** the photo's `AdjustmentStack` (below) when producing viewable pixels.

Decode failures (truncated file, unsupported subformat) mark the Photo `decodeFailed: true`,
show a broken-image placeholder in the grid, and appear in the Import Report — one bad file
never aborts an import batch.

## The date chain and dateUncertain

The effective date decides a Photo's Chapter (month) and its position in chronological layout
(R7), so it must be deterministic and its provenance visible. The chain (kernel §10):

| Priority | Source | `dateSource` | `dateUncertain` |
|---|---|---|---|
| 1 | User re-date in the Photos tab | `user` | `false` (cleared) |
| 2 | EXIF `DateTimeOriginal` | `exif` | `false` |
| 3 | Graph `photo.takenDateTime` (OneDrive sources) | `graph` | `false` |
| 4 | File last-modified time (last resort) | `fileMtime` | **`true`** |

- Stored as `takenAt` (local wall-clock ISO 8601, no timezone math — a family book cares about
  the date on the calendar where the photo was taken, and EXIF has no zone anyway) plus
  `dateSource`.
- `dateUncertain: true` photos get a visible badge in the Photos grid and are a preflight
  warning before export ([12-pdf-export.md](12-pdf-export.md)) — mtime dates are usually the
  copy date, not the capture date, and silently wrong chapters are the failure mode to fear.
- **Re-dating** (R6): the user can change any photo's date in the grid. `dateSource` becomes
  `user`, `dateUncertain` clears, and the change is permanent user intent — re-sync never
  overwrites it. A date change can move the photo to a different Chapter, or out of the book's
  year entirely, in which case it goes to the **Outside-book tray** (it is not deleted; moving
  the date back brings it home).
- Re-dating triggers Tier recomputation for both affected months (percentiles are
  within-month — see the matrix below and [06-image-analysis.md](06-image-analysis.md)).

## Thumbnail tiers

Three tiers, fixed (kernel §10):

| Tier | Long edge | Format | Used by | Cache path |
|---|---|---|---|---|
| Grid | **256 px** | JPEG q80 | Photos tab grid, bins | `cache/thumbs/256/{contentHash}.jpg` |
| Layout preview | **1024 px** | JPEG q85 | Pages tab render, analysis input | `cache/thumbs/1024/{contentHash}.jpg` |
| Full-res | native | — | Export only | none — decoded from `originals/` on demand |

- Thumbnails are rendered **with the photo's current `AdjustmentStack` applied** — the grid and
  page preview must show the photo as it will print, or edits feel broken. (Consequence:
  adjustment edits invalidate thumbs; see the matrix.)
- Export never uses cached thumbs: it decodes originals, applies adjustments, and downsamples to
  300 DPI per [12-pdf-export.md](12-pdf-export.md).
- Generation order: current Chapter first, then outward by month distance; 1024 before 256 for
  the visible month (the 256 is downsampled from the 1024 in the same job — one decode, two
  writes).
- The 1024 tier doubles as the **analysis copy**: [06-image-analysis.md](06-image-analysis.md)
  runs its models on the *pre-adjustment* oriented decode, cached separately as
  `cache/thumbs/1024a/{contentHash}.jpg` so tone edits don't force re-analysis (rationale in the
  matrix notes).
- Everything under `cache/` is 100% regenerable. Deleting `cache/` is always safe and is the
  documented fix-anything step; **user intent never lives in cache** (kernel §5).

## AdjustmentStack

> **Decision:** All image editing is non-destructive and parametric: a fixed-order
> `AdjustmentStack` stored per Photo in `photos.json`, applied at render time; originals are
> never touched. Rationale: undo/redo and autosave fall out for free, edits survive re-sync and
> re-import, and the same parameters replay identically at thumbnail, preview, and 300 DPI
> export resolution — see [ADR-0004](adr/0004-imaging-magick-net.md).

Covers R6 ("basic editing like brightness and contrast") and R11 ("advanced editing … color
adjustments") with one model. Four stages, applied in fixed order — **geometry → exposure →
color → finish** — so results are deterministic regardless of the order the user made the edits:

```jsonc
"adjustments": {
  "schemaVersion": 1,
  "geometry": { "rotate": 0,            // 0 | 90 | 180 | 270 (beyond EXIF auto-orient)
                "straighten": 0.0,      // degrees, -15..+15, auto-crops the wedge
                "flipH": false },
  "exposure": { "exposureEv": 0.0,      // -2.0..+2.0
                "brightness": 0,        // -100..100  (R6)
                "contrast": 0,          // -100..100  (R6)
                "highlights": 0, "shadows": 0 },   // -100..100
  "color":    { "temperature": 0,       // -100 (cool) .. +100 (warm)
                "tint": 0,              // -100 (green) .. +100 (magenta)
                "saturation": 0, "vibrance": 0 },  // -100..100
  "finish":   { "sharpen": 0,           // 0..100 → unsharp mask amount
                "vignette": 0,          // 0..100
                "blackAndWhite": false }
}
```

- All-zero/false is the identity stack and is omitted from JSON entirely (human-diffable goal).
- Magick.NET mapping (implementation anchors, `PhotoBook.Imaging`): `exposureEv` →
  `Evaluate(Multiply, 2^ev)`; `brightness`/`contrast` → `BrightnessContrast`;
  `highlights`/`shadows` → level-mask curves; `temperature`/`tint` → per-channel color matrix;
  `saturation`/`vibrance` → `Modulate` (vibrance weighted by inverse saturation);
  `sharpen` → `UnsharpMask`; `blackAndWhite` → `Grayscale` + slight contrast lift.
- **Crop is not an adjustment.** Framing lives in the placement's `CropState`
  (`{ zoom, offsetX, offsetY }`, kernel §4) because it's a property of *photo-in-Slot*, not of
  the photo. The Photos tab edits the stack; the Pages tab edits `CropState`.
- Geometry edits change the effective pixel grid, so Focus Region coordinates must follow:
  `user` regions are **remapped mathematically** (rotation/flip are exact transforms — user
  intent is never discarded), derived regions are recomputed by re-analysis.
- The editor UI for both basic and advanced edits is specified in
  [09-editor-ux.md](09-editor-ux.md); edits apply to photos whether placed on a page or sitting
  in the Unplaced bin (R11).

## Cache invalidation matrix

The contract for which edits dirty which derived artifacts. "Rebuild" = delete + regenerate in
the background; "keep" = untouched. Analysis cache and Tier semantics are owned by
[06-image-analysis.md](06-image-analysis.md); this matrix is the single cross-reference.

| Edit | 256 thumb | 1024 preview | 1024a analysis copy | Analysis cache | Tier (photos.json) | Unpinned CropStates |
|---|---|---|---|---|---|---|
| Geometry stage (rotate/straighten/flip) | rebuild | rebuild | rebuild | **rebuild** (user regions remapped, derived recomputed) | keep | re-crop next layout run |
| Exposure / color / finish stage | rebuild | rebuild | keep | keep | keep | keep |
| Re-date (R6) | keep | keep | keep | keep | **recompute both months' percentiles** (overrides untouched) | keep (photo may change Chapter) |
| Focus Region edit (R25) | keep | keep | keep | keep (user regions live in `photos.json`, not cache) | keep | re-crop next layout run |
| Tier promote/demote (R26) | keep | keep | keep | keep | `userTierOverride` set, absolute | keep (affects future template scoring) |
| Exclude / re-include (R17) | keep | keep | keep | keep | recompute month percentiles (excluded photos leave the pool) | n/a |
| `CropState` pan/zoom (R9/R13) | keep | keep | keep | keep | keep | user's own edit; page becomes Pinned |
| Re-import modified source | new hash ⇒ all keys rebuild | ⟵ | ⟵ | ⟵ | recompute month | keep placements (photo identity retained) |
| Delete `cache/` wholesale | rebuild | rebuild | rebuild | rebuild | keep (stored in `photos.json`) | keep |

Notes:

- **Why tone edits don't touch analysis:** quality signals and Focus Regions are measured on the
  *source* photo (the `1024a` pre-adjustment copy). A user brightening a dark photo is fixing
  it, not resubmitting it for judgment — auto-demoting a photo mid-edit would make Tiers feel
  haunted. Geometry is the exception because it moves pixels that coordinates point at.
- Invalidation is implemented as key deletion: every cache artifact is keyed by
  `contentHash` (+ analyzer id/version for analysis, see doc 06), so re-import gets clean caches
  by construction rather than by bookkeeping.
- Cache writes are atomic (temp + rename) like all project writes (kernel §5); a crash
  mid-rebuild leaves a missing key, and missing keys regenerate lazily on first request.
