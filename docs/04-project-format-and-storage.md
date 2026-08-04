# 04 — Project Format & Storage

This document specifies the on-disk project format: the folder layout, every file's content and
serialization rules, schema versioning and migration, the atomic-save/autosave/crash-recovery
protocol, the originals copy-in model, and the cache contract. The entities being serialized are
defined in [03-domain-model.md](03-domain-model.md); this doc owns *how and where* they live on
disk. A PhotoBook project **is** a folder — self-contained, human-diffable, and openable years
from now with nothing but a JSON viewer.

Related docs: [02-architecture.md](02-architecture.md) ·
[03-domain-model.md](03-domain-model.md) ·
[05-ingestion-and-photo-sources.md](05-ingestion-and-photo-sources.md) ·
[06-image-analysis.md](06-image-analysis.md) ·
[12-pdf-export.md](12-pdf-export.md) ·
[13-testing-strategy.md](13-testing-strategy.md)

## 1. Design goals

1. **Self-contained archive.** Originals are copied in (R1); the folder alone is a complete,
   portable backup of the book — copy it to a USB stick and it opens.
2. **Human-diffable.** Indented JSON, stable ordering, sparse nulls. A git repo over a project
   folder produces meaningful diffs; this is a design goal, not an accident.
3. **Chapter = a file.** One JSON file per month (R4) keeps each chapter independently editable,
   diffable, and recoverable as a unit, and keeps any single file small.
4. **Crash-safe by construction.** Atomic replace + rolling backup; no write ever leaves a file in
   a half-written state.
5. **No database.** Under ~2,000 pre-culled photos/year, plain JSON plus a file cache outperforms
   a database on every axis that matters here: transparency, backup, versioning, zero migration
   tooling.

> **Decision:** Project storage is plain JSON files in a self-contained folder — no SQLite, no
> embedded DB — see [ADR-0007](adr/0007-project-storage-json-folder.md).

## 2. Folder layout

Canonical layout (kernel §5); names are fixed:

```
MyBook-2024/
  book.json          — book settings, style, page size, print profile ref, seed
  photos.json        — photo catalog: source ids, dates + dateUncertain flag, adjustments,
                       focus regions, tier (+userTierOverride), excluded flags, person tags
  journal.json       — parsed journal entries + unmatched-import report
  chapters/          — one file per month
    2024-01.json … 2024-12.json
  originals/         — copied source images, content-hash-prefixed filenames, immutable
  cache/             — thumbnails + analysis outputs; 100% regenerable
```

Rules of the layout:

- The folder name is the user's business; the app identifies a project by the presence of
  `book.json` ("Open project" = pick the folder or its `book.json`).
- `chapters/` files exist only for months that have pages; an empty month has no file.
- Chapter filename pattern: `{year}-{month:00}.json` (e.g. `2024-07.json`); it must agree with the
  `year`/`month` fields inside — mismatch is a load error.
- Transient siblings produced by the save protocol (`*.tmp`, `*.bak`, §6) live next to their
  target file and are never listed as project content.
- App-level preferences (window layout, bin docked bottom vs side per R10/R13, MRU list) live in
  per-user app settings under `%APPDATA%\PhotoBook\`, **not** in the project folder — a project
  moved between machines must not drag UI state with it.

## 3. File-by-file specification

| File | Root object | Contents | Typical size (2,000 photos) |
|---|---|---|---|
| `book.json` | `Book` | title, `year`, `pageSize` (default `11x8.5-landscape`), global `Style`, `printProfileRef`, engine `seed`, source binding (OneDrive album or folder), per-book analysis opt-ins | < 5 KB |
| `photos.json` | `{ schemaVersion, photos: Photo[] }` | the whole catalog: source ids, effective dates + `dateUncertain`, `AdjustmentStack`, `FocusRegion[]`, `QualityScore` + `tier` + `userTierOverride`, `excluded` tombstones (R17), `PersonTag[]`, captions | 1–3 MB |
| `journal.json` | `{ schemaVersion, entries: JournalEntry[], importReport }` | parsed dated entries (R2) plus the unmatched-import report the UI shows after Word import ([11-journal-ingestion.md](11-journal-ingestion.md)) | < 1 MB |
| `chapters/{y}-{m:00}.json` | `Chapter` | pages in order: `templateRef` or Detached inline template snapshot, `mirrored`, `pinned`, placements (`slotId`, `photoId`, `CropState`), journal text-slot bindings | 10–100 KB each |
| `originals/*` | — | copied source bytes, all formats as imported (jpg, heic, webp, png, … per R1); §7 | 2–20 GB |
| `cache/**` | — | thumbnails, raw analysis outputs, lock file; §8 | regenerable |

Field-level shapes for every root object are in [03-domain-model.md](03-domain-model.md); this doc
does not restate them.

## 4. JSON serialization rules

All (de)serialization goes through **System.Text.Json** with one shared `JsonSerializerOptions`
instance defined in `PhotoBook.Core`:

```csharp
static readonly JsonSerializerOptions ProjectJson = new()
{
    PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    WriteIndented          = true,                          // human-diffable
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters             = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    ReadCommentHandling    = JsonCommentHandling.Skip,      // tolerate hand-annotation
    AllowTrailingCommas    = true                           // tolerate hand-editing
};
```

Normative rules:

1. **camelCase** property names everywhere; enums serialize as camelCase strings (`"oneDrive"`,
   `"saliency"`), never integers.
2. **Dates**: ISO 8601. Timestamps that are instants carry `Z` (UTC, e.g. `importedAtUtc`); photo
   `takenAt` is stored as an unzoned local wall-clock time — the time on the camera is what the
   family experienced, and chapter membership must not shift with timezone math. Date-only fields
   use `yyyy-MM-dd`.
3. **Numbers**: invariant culture; normalized rects and weights as doubles in `[0,1]`.
4. **Nulls omitted** on write (`WhenWritingNull`) — absent and null are equivalent on read.
5. **Stable ordering**: properties serialize in declared record order; arrays have defined sort
   keys (`photos` by `id`, `focusRegions` by `kind` then `weight` desc, `entries` by `date`,
   `pages` in reading order). Two saves of the same model are byte-identical — diffs show only
   real changes.
6. **Unknown members are preserved**: every root record carries a `[JsonExtensionData]` bag, so
   data written by a newer minor revision round-trips through an older app instead of being
   silently deleted.
7. **`schemaVersion`** (integer, currently `1`) is the first property of every file's root object.

## 5. Schema versioning and forward migration

- Each of `book.json`, `photos.json`, `journal.json`, and every chapter file versions
  independently via its root `schemaVersion`. Current version for all files: **1**.
- **Additive changes** (new optional field with a default) do **not** bump `schemaVersion`; rule 6
  of §4 makes them round-trip-safe in both directions.
- **Shape changes** (rename, restructure, semantic change) bump the file's `schemaVersion` and
  ship with a pure migration step `vN → vN+1` registered in `PhotoBook.Core`.

Forward-migration policy (normative):

```
Migrate(doc):
  v = doc.schemaVersion
  if v > CurrentVersion:
      open READ-ONLY; tell the user a newer PhotoBook wrote this project   // never guess forward
  while v < CurrentVersion:
      doc = migrations[v](doc)        // pure, in-memory, tested per step
      v += 1
  return doc
```

- Migration happens **in memory on load**; files on disk are rewritten only by the next normal
  save (with its `.bak` safety net, §6). Opening a project never mutates it.
- Migration steps are pure functions with golden-file tests
  ([13-testing-strategy.md](13-testing-strategy.md)); chains are kept forever — a v1 file from
  2026 must open in the 2036 build.

## 6. Atomic saves, autosave, and crash recovery

### Atomic write protocol

Every file save uses temp-file-plus-rename; a reader can never observe a torn file:

```
SaveFile(path, model):
  json = Serialize(model, ProjectJson)
  tmp  = path + ".tmp"
  write json → tmp; FileStream.Flush(flushToDisk: true)
  if Exists(path):
      File.Replace(tmp, path, path + ".bak")   // atomic swap; previous version becomes .bak
  else:
      File.Move(tmp, path)
```

`File.Replace` on NTFS swaps atomically and demotes the previous good version to a rolling `.bak`
in one step — every file therefore always has its last-known-good predecessor beside it.

### Autosave

- A dirty-tracking timer saves **every 30 seconds** — only files whose model actually changed.
- Additional immediate save points: import/sync completion, any auto-layout run, chapter
  re-layout (R16), photo exclusion (R17), date edits (R6), before PDF export, and app exit.
- Manual `Ctrl+S` saves everything dirty. There is no "unsaved document" state to lose — the
  project on disk trails the in-memory model by at most 30 seconds.
- Saves run on the single writer thread ([02-architecture.md](02-architecture.md)); serialization
  of a snapshot happens off the UI thread.

### Crash recovery

On project open:

```
Load(path):
  delete stray *.tmp                       // a .tmp is by definition an incomplete write
  try: return Migrate(Parse(path))
  catch missing-or-malformed:
      if Exists(path + ".bak"):
          model = Migrate(Parse(path + ".bak"))
          notify: "Recovered {file} from backup (last good save: {mtime})"
          return model                     // next save re-establishes path + fresh .bak
      else: fail with the file name and parse position — never open a guessed project
```

Because every mutation window is ≤ 30 s and every file has a `.bak`, worst-case loss after a power
cut is under a minute of tweaks. Chapters are independent files, so a corrupted `2024-07.json`
costs at most one month's layout — never the catalog.

*OneDrive note:* the project folder may live inside a synced folder, but local disk is
recommended; if the sync engine ever produces a conflict copy (`book-Copy.json`), the app ignores
it and says so in a load warning.

## 7. Originals: copy-into-project and immutability

> **Decision:** Source images are **copied into `originals/`** at import, and from that moment are
> **immutable** — the app opens them read-only, sets the read-only file attribute, and no code path
> writes into `originals/` after the copy — see
> [ADR-0007](adr/0007-project-storage-json-folder.md). All edits are parametric
> (`AdjustmentStack`) or geometric (`CropState`) and render non-destructively via Magick.NET
> ([ADR-0004](adr/0004-imaging-magick-net.md)).

### Content-hash naming

On import, each source file's bytes are hashed (**SHA-256**) before copying:

```
originals/{hash16}-{sanitizedOriginalName}.{ext}
originals/3fa9c2d417b25e08-IMG_1234.heic
```

- `hash16` = first 16 lowercase hex chars of the SHA-256 of the original bytes; the full 64-char
  hash is stored in `Photo.contentHash`.
- `Photo.id` = `"ph-" + hash16` — photo identity **is** content identity.
- The original filename (sanitized to NTFS-safe characters, original extension preserved, R1) is
  kept for human recognizability; the hash prefix guarantees uniqueness when two imports share a
  name.
- Bytes are copied first, hashed-name file fsynced, then the `photos.json` row is added — an
  interrupted import leaves at worst an unreferenced file, which import cleanup removes on next run.

### Consequences

- **Dedupe**: re-importing identical bytes (same `contentHash`) is a no-op — same `Photo.id`,
  no second copy.
- **Tombstones hold** (R17): an excluded photo's catalog row matches re-synced content by hash
  (and `driveItemId` for OneDrive), so it never re-enters the book.
- **Verification**: a preflight/maintenance check can re-hash `originals/` and prove the archive
  is bit-perfect years later.
- **Re-linking is a non-problem**: nothing references the source location after import; the
  source binding in `book.json` exists only to *sync more photos*, not to find existing ones.

## 8. Cache: regenerable by construction

`cache/` contents are 100% derivable from `originals/` + the JSON files. The contract, verbatim
from the kernel and enforced in code review: **user intent never lives in cache**.

Deleting `cache/` must lose *nothing* — the app rebuilds it (background job queue,
[02-architecture.md](02-architecture.md)) with identical results. Corollaries: `cache/` is
excluded from backups by definition, is safe for any cleanup tool to purge, and no code may read
user decisions from it.

```
cache/
  .lock                              — exclusive-handle file guarding single-instance access (§9)
  thumbs/256/{contentHash}.jpg       — grid thumbnail tier
  thumbs/1024/{contentHash}.jpg      — layout-preview tier
  thumbs/1024a/{contentHash}.jpg     — pre-adjustment analysis copy
  analysis/{contentHash}.{analyzerId}.json — RAW analyzer output: face boxes, saliency rects,
                                   raw scores, analyzerVersion — inputs to fusion, not results of decisions
```

Thumbnail tiers (256 px grid / 1024 px layout preview / full-res export from originals) follow
[05-ingestion-and-photo-sources.md](05-ingestion-and-photo-sources.md); `analysis/*.json` is
keyed by `contentHash` plus analyzer id and stamped with `analyzerVersion`
([06-image-analysis.md](06-image-analysis.md)), so upgrading a model invalidates exactly the
stale entries.

The litmus table — when adding any new persisted fact, place it by asking *"would the user be
angry if this vanished?"*:

| Fact | Lives in | Why |
|---|---|---|
| User-drawn Focus Region (R25) | `photos.json` | user intent |
| Raw YuNet face boxes / U2-Netp saliency | `cache/analysis/` | recomputable detector output |
| Fused `tier` + `userTierOverride` (R26) | `photos.json` | month-relative result + explicit user intent; must survive cache wipes |
| `excluded` tombstone (R17) | `photos.json` | user intent, load-bearing across re-scans |
| `CropState` per placement | `chapters/*.json` | engine/user intent |
| 256/1024 px thumbnails | `cache/thumbs/` | pixels, rebuildable |
| `dateUncertain` flag / user date edits (R6) | `photos.json` | provenance + user intent |

## 9. Locking and concurrency

- **Single instance per project**: on open, the app takes an exclusive handle
  (`FileShare.None`) on `cache/.lock`. A second instance gets a clear "project is open elsewhere"
  message. A stale lock after a crash is detectable (the handle died with the process) and is
  reclaimed silently.
- **Single writer in-process**: all model mutation and all saves are marshaled through the single
  writer thread defined in [02-architecture.md](02-architecture.md); background jobs (analysis,
  thumbnailing) write only under `cache/` and only via their own job-queue discipline.
- Readers of project JSON at runtime are the loader and the save path — nothing else touches the
  files while a project is open; external edits during a session are unsupported (last save wins).

## 10. Storage invariants

1. A project folder is fully described by §2; the app never creates files outside it except
   per-user settings in `%APPDATA%\PhotoBook\`.
2. Every project JSON file is written atomically and always has, after its second save, a `.bak`
   containing the previous good version.
3. On-disk state trails in-memory state by ≤ 30 s while a project is open.
4. `schemaVersion` is present in every file; loaders migrate forward in memory, never downgrade,
   and never modify files on open.
5. Serialization is deterministic: unchanged model ⇒ byte-identical file.
6. `originals/` is append-only at import time and immutable afterward; filenames are
   content-hash-prefixed; `Photo.contentHash` always matches the bytes.
7. **User intent never lives in cache**; deleting `cache/` is always lossless and always safe.
8. One writing process per project (`cache/.lock`), one writer thread per process.
9. Excluded photos' rows are never deleted from `photos.json` (R17) — the tombstone *is* the
   exclusion mechanism across re-scans.
