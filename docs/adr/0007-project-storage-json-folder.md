# ADR-0007 — Project storage: human-readable JSON folder, no database

This ADR records why a PhotoBook project is a plain folder of human-readable JSON files plus copied
originals and a disposable cache — and why SQLite (and every other embedded database) was rejected
for this workload.

Related docs: [04-project-format-and-storage.md](../04-project-format-and-storage.md) · [03-domain-model.md](../03-domain-model.md) · [02-architecture.md](../02-architecture.md)

## Status

Accepted — 2026-08-01.

## Context

One book is one year (R3) with under ~2,000 pre-culled photos, edited by a single user on one
machine. Chapters (months) must "stand alone and be editable as a unit" (R4). The project must be a
self-contained archive — originals are copied in — and the user's creative work (crops, focus
regions, tier overrides, exclusions per R17) is irreplaceable and must be inspectable, diffable,
and trivially backed up. Derived data (thumbnails, analysis) is expensive but 100% regenerable.

At this scale the entire catalog fits comfortably in memory: 2,000 photo records at ~2 KB of JSON
each is ~4 MB; a full parse is well under 200 ms with System.Text.Json. There are no ad-hoc query
needs that in-memory LINQ cannot serve.

## Options considered

### SQLite (or LiteDB)

The default reflex for app storage — and wrong here. It buys indexed queries, partial loads, and
transactional multi-writer access, none of which a 4 MB single-user dataset needs. It costs: an
opaque binary blob (no diff, no eyeball debugging, no hand-repair after a bug), schema-migration
tooling, a native dependency, and a "the project is a database" mental model that fights the
self-contained-folder archive goal. Rejected for under-2,000-photo books.

### One monolithic project.json

Simple, but every autosave rewrites everything; a single corrupt write risks the whole book; and a
month's edits produce a diff tangled with eleven untouched months — directly against R4.

### JSON folder with per-chapter files (chosen)

Small set of purpose-split files; each Chapter serializes alone.

## Decision

> **Decision:** A project is a folder of camelCase System.Text.Json files, exactly the layout in
> [04-project-format-and-storage.md](../04-project-format-and-storage.md); no database at any layer.
> Rationale inline: at this volume a database adds failure modes and removes transparency.

```
MyBook-2024/
  book.json          — book settings, style, page size, print profile ref, seed
  photos.json        — photo catalog: source ids, dates + dateUncertain flag, adjustments,
                       focus regions, tier (+userTierOverride), excluded flags, person tags
  journal.json       — parsed journal entries + unmatched-import report
  chapters/2024-01.json … 2024-12.json — pages, template refs or detached snapshots,
                       slot→photo placements with CropState, pinned flags
  originals/         — copied source images, content-hash-prefixed filenames, immutable
  cache/             — thumbnails + analysis outputs; 100% regenerable
```

Rules with teeth:

- **Per-chapter files satisfy R4.** `chapters/2024-07.json` is the complete editable state of July:
  its pages, template refs or Detached snapshots, placements with `CropState`, and Pinned flags.
  Re-laying-out July rewrites one file; a version-control diff shows exactly one month changed; a
  chapter can be reverted from `.bak` without touching its neighbors.
- **Atomic writes.** Serialize to `<name>.json.tmp` in the same directory, flush to disk, then
  `File.Replace(tmp, target, target + ".bak")` — the rename is atomic on NTFS, and the previous
  good version becomes the rolling `.bak`. A crash mid-save can never leave a half-written file as
  the only copy.
- **Autosave** every 30 s (dirty files only) and immediately after major operations (re-layout,
  import, chapter switch).
- **`schemaVersion`** (integer) at the top of every file; loaders upgrade old versions forward on
  read; writers always emit current. No migration framework — a switch statement.
- **User intent never lives in cache.** Everything the user decided — focus-region edits, tier
  promote/demote, exclusions, crops, re-dates — lives in `photos.json` or a chapter file. Deleting
  `cache/` costs recompute time only, never work. This is the invariant that keeps "regenerable"
  honest, and it is enforced in review: no writer in `PhotoBook.Imaging`/`Analysis` may touch the
  three root JSON files.
- **Referential integrity by id**, checked on load: placements reference photo ids; dangling refs
  (photo excluded after placement) surface as empty slots flagged amber, never as crashes.

## Consequences

- The whole model loads into memory at open; all queries are LINQ. Simple, fast, testable.
- Users (and Claude, during support) can read and hand-fix a project in a text editor; projects
  diff cleanly in git — an explicit design goal.
- No partial loading: a pathological 50,000-photo folder would need paging we don't have. Out of
  scope by the locked <2,000 decision.
- Concurrency is single-instance: the app takes a lock file in the project folder on open; a second
  instance opens read-only.
- `photos.json` is the largest file (~4 MB worst case); rewriting it on autosave is measured in
  tens of milliseconds — no sharding needed.

## Revisit when

- A book meaningfully exceeds ~5,000 photos or `photos.json` autosave exceeds ~250 ms.
- Multi-machine or multi-user editing appears (would need real merge semantics, not just SQLite).
- Cloud sync of *projects* (not photos) becomes a feature and file-level atomicity stops being enough.
