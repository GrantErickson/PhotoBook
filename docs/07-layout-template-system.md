# 07 — Layout Template System

This doc is the full specification of PhotoBook's page templates: the JSON schema every template
file must satisfy, the semantics of image Slots and text slots, caption policies, the left/right
mirroring rule (R20), multi-day section templates (R28), matched spread pairs (R22), the
detach-on-edit rule that keeps the library immutable, the concrete v1 library of ~50 templates,
and the linter that keeps every template printable. The auto-layout engine
([08-auto-layout-engine.md](08-auto-layout-engine.md)) consumes these templates as pure input;
the editor ([09-editor-ux.md](09-editor-ux.md)) swaps and detaches them; styles
([10-styles-and-typography.md](10-styles-and-typography.md)) control how their text renders.

Related docs: [03-domain-model.md](03-domain-model.md) ·
[04-project-format-and-storage.md](04-project-format-and-storage.md) ·
[08-auto-layout-engine.md](08-auto-layout-engine.md) · [09-editor-ux.md](09-editor-ux.md) ·
[10-styles-and-typography.md](10-styles-and-typography.md) · [12-pdf-export.md](12-pdf-export.md)

## Design principles

1. **Templates are data, not code.** A `Template` is a JSON document; adding a layout to the
   library must never require recompiling layout logic. All layout intelligence (which template,
   which photo in which Slot, how to crop) lives in the engine, not in the template.
2. **Normalized geometry.** All rects are normalized `[0,1] × [0,1]` over the single-page **trim**
   box, origin top-left (kernel §3). The same template renders identically at screen preview and
   300 DPI PDF because both go through the same SkiaSharp draw code.
3. **Templates propose; Styles decide appearance.** A template says *where* text and photos go.
   Fonts, sizes, colors, and image borders come from the `Style` cascade (R23) — a template never
   embeds a font name or a color.
4. **The library is immutable.** User edits to a page's geometry produce a Detached inline
   snapshot in the chapter file; library templates are never mutated (see
   [Detach-on-edit and template swap](#detach-on-edit-and-template-swap)).
5. **Deliberate negative space is a feature.** Not every template fills the page, and not every
   template has a text slot — textless layouts are the negative-space option (R20).

> **Decision:** Templates ship as embedded JSON resources in `PhotoBook.Core`
> (`Templates/{id}.json`, one file per template), loaded at startup into an in-memory
> `TemplateLibrary`, linted, and sorted by `id` before being handed to the engine. Rationale:
> `PhotoBook.Engine` must stay pure and I/O-free (kernel §12), the library must version with the
> app (a project references templates by `id`), and deterministic ordering feeds the engine's
> same-inputs-same-book guarantee (kernel §7). Storage of *detached* snapshots follows
> [ADR-0007](adr/0007-project-storage-json-folder.md).

Useful normalized constants for the default `11x8.5-landscape` page (kernel §3). Divide inches by
11 for x-axis values and by 8.5 for y-axis values:

| Physical quantity | x (÷ 11) | y (÷ 8.5) |
|---|---|---|
| Bleed 0.125 in | 0.0114 | 0.0147 |
| Safe margin 0.375 in | 0.0341 | 0.0441 |
| Gutter caution 0.5 in | 0.0455 | — |
| Minimum Slot edge 1.5 in | 0.1364 | 0.1765 |
| Below-caption band 0.30 in | — | 0.0353 |

## Template JSON schema

`schemaVersion: 1`. System.Text.Json, camelCase (kernel §5). Complete annotated example — this is
the kernel §6 example `t-04-text-a` fully worked:

```jsonc
{
  "schemaVersion": 1,
  "id": "t-04-text-a",                  // stable string id; never reused or renamed
  "name": "Four up with journal",
  "pageSize": "11x8.5-landscape",
  "kind": "standard",                   // standard | monthTitle | fullBleed | multiDay | spreadPair
  "photoCount": 4,                      // 1..8 (R20); must equal total slot count
  "mirrorable": true,                   // engine may mirror horizontally for left pages (R20)
  "slots": [
    { "id": "s1", "rect": { "x": 0.00, "y": 0.00, "w": 0.50, "h": 0.66 },
      "aspect": 0.98, "aspectTolerance": 0.35,
      "tierAffinity": "S", "captionPolicy": "below", "bleed": false },
    { "id": "s2", "rect": { "x": 0.52, "y": 0.00, "w": 0.48, "h": 0.42 },
      "aspect": 1.48, "aspectTolerance": 0.35,
      "tierAffinity": "A", "captionPolicy": "none", "bleed": false },
    { "id": "s3", "rect": { "x": 0.76, "y": 0.44, "w": 0.20, "h": 0.24 },
      "aspect": 1.08, "aspectTolerance": 0.30,
      "tierAffinity": "B", "captionPolicy": "none", "bleed": false },
    { "id": "s4", "rect": { "x": 0.00, "y": 0.68, "w": 0.50, "h": 0.28 },
      "aspect": 2.31, "aspectTolerance": 0.40,
      "tierAffinity": "any", "captionPolicy": "below", "bleed": false }
  ],
  "textSlots": [
    { "id": "t1", "rect": { "x": 0.55, "y": 0.72, "w": 0.41, "h": 0.22 },
      "role": "journal", "align": "left" }
  ],
  "pair": null,                         // spreadPair only: { "pairId": "sp-a", "side": "left" }
  "sections": null                      // multiDay only: see Multi-day section templates
}
```

Top-level fields:

| Field | Type | Rules |
|---|---|---|
| `schemaVersion` | int | Required. Currently `1`. Loader rejects unknown major versions. |
| `id` | string | Required, unique library-wide, stable forever (chapter files reference it). Naming convention: `t-<NN>-<text\|notext>-<letter>` for standard (`NN` = photoCount, zero-padded), `t-01-fb-<letter>` full-bleed, `t-title-<letter>` month title, `t-md-<letter>` multi-day, `t-sp-<letter>-left/-right` spread pairs. |
| `name` | string | Required. Human display name shown in the layout picker. |
| `pageSize` | string | Required. Page-size id from `book.json` (R19). v1 ships `11x8.5-landscape` only; a template applies only to pages whose size id matches exactly. New sizes get their own template variants — no automatic re-stretching across aspect families. |
| `kind` | enum | `standard \| monthTitle \| fullBleed \| multiDay \| spreadPair` (kernel §6). Drives engine eligibility: `monthTitle` only for the Chapter title page (R24), `multiDay` only for merged Day Groups (R28), `spreadPair` only when both facing pages are available. |
| `photoCount` | int | 1..8 (R20). Must equal `slots.length` (`multiDay`: total across sections; `spreadPair`: this side's slots, with each `spanId` slot counted once per pair). |
| `mirrorable` | bool | Required. See [Mirroring](#mirroring-one-template-left-and-right-pages). Must be `false` for `spreadPair`. |
| `slots` | ImageSlot[] | Required, ≥ 1. |
| `textSlots` | TextSlot[] | Required, may be empty (`[]`) — textless templates are legal and encouraged (R20). |
| `pair` | object? | `spreadPair` only: `{ "pairId": string, "side": "left" \| "right" }`. |
| `sections` | Section[]? | `multiDay` only. |

Unknown fields are a load error for library templates (they indicate a version skew) but are
preserved round-trip on detached snapshots for forward compatibility.

## ImageSlot semantics

| Field | Type | Rules |
|---|---|---|
| `id` | string | Unique in template. Convention `s1..sN` in reading order (top-left → bottom-right); the Unplaced-bin fill order and empty-slot amber flags (R14) follow this order. |
| `rect` | `{x,y,w,h}` | Normalized over the trim box. Must lie within `[0,1]²` unless `bleed: true`. |
| `aspect` | double | The Slot's **physical** width/height in inches: `aspect = (w × 11) / (h × 8.5)` for the default page. Declared redundantly so authors and the assignment scorer never re-derive it; the linter enforces consistency (rule L5). |
| `aspectTolerance` | double | 0..0.6. A photo with native aspect `p` fits with zero aspect penalty when `max(p/aspect, aspect/p) ≤ 1 + aspectTolerance`; beyond that, the engine's crop-loss cost ramps up (scoring formula in [08-auto-layout-engine.md](08-auto-layout-engine.md)). Default 0.35. |
| `tierAffinity` | enum | `S \| A \| B \| C \| any`. **Soft** preference, never a hard filter: the Hungarian assignment adds cost for tier mismatch (a Chapter may simply have no S-tier photos left). Authoring rule of thumb: Slots covering ≥ 30% of the page get `S`, 12–30% get `A`, 5–12% get `B`, thumbnails get `C` or `any`. This is how Tier drives photo size on the page (R26). |
| `captionPolicy` | enum | `none \| below \| overlay` — see [Caption policies](#caption-policies). |
| `bleed` | bool | Default `false`. When `true`, the rect may cross the trim edge and must extend fully to the bleed box edge on every side it crosses (bleed box = trim + 0.125 in per outer edge, i.e. x ∈ [−0.0114, 1.0114], y ∈ [−0.0147, 1.0147]). No partial-bleed slivers. |
| `spanId` | string? | `spreadPair` templates only: slots in the left and right templates sharing a `spanId` render **one** photo continuously across the gutter (R18). |

A full-bleed single-photo slot is exactly:

```jsonc
{ "id": "s1", "rect": { "x": -0.0114, "y": -0.0147, "w": 1.0228, "h": 1.0294 },
  "aspect": 1.286, "aspectTolerance": 0.25, "tierAffinity": "S",
  "captionPolicy": "overlay", "bleed": true }
```

The photo placed in a Slot always cover-fills the rect via `CropState { zoom, offsetX, offsetY }`
(kernel §4) — auto-layout emits a CropState from Focus Regions (R25) and the user hand-tweaks the
*same* CropState by panning/zooming (R9, R13); `zoom < 1` letterboxes and the black page background
shows through (R9). Faces stay inside the safe margin and out of the gutter caution zone via
smart-crop, not via template geometry — Slots themselves may run to the trim edge.

## TextSlot semantics

| Field | Type | Rules |
|---|---|---|
| `id` | string | Unique in template. Convention `t1..tN`. |
| `rect` | `{x,y,w,h}` | Must lie fully inside the safe area **and** clear the gutter caution zone: `x ≥ 0.0455`, `x + w ≤ 0.9659`, `y ≥ 0.0441`, `y + h ≤ 0.9559` (as authored — templates are authored as right pages, see Mirroring). |
| `role` | enum | `journal \| caption \| monthTitle` (kernel §6). `journal` holds a day's journal text; `caption` is a rare standalone caption block placed beside an image; `monthTitle` holds the Chapter title (R24) and is the one role allowed to overlap image slots. |
| `align` | enum? | `left \| center \| right`. Default `left` for `journal`/`caption`, `center` for `monthTitle`. |
| `attachedTo` | string? | `caption` role only: the `id` of the ImageSlot this caption belongs to. |

What a TextSlot does **not** contain: font family, size, color, line spacing. All of that comes
from the Style cascade — journal text defaults to Source Serif 4 10.5 pt, captions Source Sans 3
8.5 pt, month titles Playfair Display 64 pt, all overridable globally (R23, R24); see
[10-styles-and-typography.md](10-styles-and-typography.md).

Text capacity is *derived*, not declared: the engine measures whether a Day Group's journal text
fits the slot at current style metrics, and text-fit is a **hard filter** in template scoring.
There is no auto font-shrink: a day's journal text is atomic — it may span the two pages of one
Spread but never crosses Spreads; if it can't fit, the engine must pick a roomier template
(kernel §9, [08-auto-layout-engine.md](08-auto-layout-engine.md)). A page whose template has a
journal slot but whose day has no journal entry renders the slot empty — black negative space, by
design (R20).

## Caption policies

Most photos have no caption; some do (R5). Captions are **not** authored as TextSlots — each
ImageSlot declares a `captionPolicy` telling the renderer where a caption goes *if* the placed
photo has one:

- **`none`** — this Slot cannot show a caption. If the user types a caption for a photo placed
  here, the editor warns and offers to move the photo to a captionable Slot
  ([09-editor-ux.md](09-editor-ux.md)).
- **`below`** — a 0.30 in caption band (normalized h = 0.0353) is reserved at the bottom of the
  Slot rect *only when a caption is present*; the image cover-fills the remaining
  `h − 0.0353`. No caption → the image gets the full rect. Max two lines, ellipsis beyond;
  metrics in [10-styles-and-typography.md](10-styles-and-typography.md).
- **`overlay`** — the caption renders inside the image on a bottom scrim: black vertical gradient
  0% → 60% opacity, scrim height 18% of the Slot height with a 0.5 in minimum. This is the policy
  for full-bleed heroes where a below-band is impossible (R5). Scrim spec is owned by
  [10-styles-and-typography.md](10-styles-and-typography.md).

Authoring guidance: give `below` to large Slots with room to spare, `overlay` only to `bleed`
slots and heroes, `none` to thumbnails under 2.5 in wide (an 8.5 pt caption under a tiny image
reads as clutter).

## Mirroring: one template, left and right pages

> **Decision:** Every template is authored as a **right page** (gutter edge at `x = 0`); when the
> engine or the user places a `mirrorable: true` template on a left page, geometry is mirrored
> horizontally and automatically. Rationale (R20): one authored file serves both page sides, the
> visual weight of a layout stays biased toward the outer edge on both sides of a Spread, and
> gutter-safety constraints can be linted against a single known gutter edge.

The transform, applied at load time to produce the left-page variant (never persisted):

```
mirror(rect)  = { x: 1 − rect.x − rect.w, y: rect.y, w: rect.w, h: rect.h }
```

- Applied to every Slot and TextSlot rect; `bleed` slots mirror into the opposite bleed edge.
- Slot/TextSlot `id`s, ordering, `aspect`, `tierAffinity`, and `captionPolicy` are unchanged —
  a photo placement (`slotId` → photo + CropState) survives a page moving sides untouched, except
  the engine recomputes CropState pan clamping (slot geometry moved, image didn't).
- Text `align` is **not** flipped: journal stays left-aligned regardless of page side; reading
  direction beats symmetry.
- `mirrorable: false` templates render identically on both sides. Use it for symmetric layouts
  where mirroring is a visual no-op (centered grids) and for `spreadPair` sides (mandatory).
- v1 has no manual mirror toggle; the page's side (even page index = left) decides.

## Multi-day section templates

Sparse days must share a page: 2–3 days with 2–3 photos each and short journal entries combine
onto one page, and each day's photos + text stay together as a unit (R28). `kind: "multiDay"`
templates express this with `sections`:

```jsonc
"sections": [
  { "id": "d1", "slotIds": ["s1", "s2"], "textSlotIds": ["t1"] },
  { "id": "d2", "slotIds": ["s3"],       "textSlotIds": ["t2"] },
  { "id": "d3", "slotIds": ["s4"],       "textSlotIds": [] }
]
```

| Section field | Rules |
|---|---|
| `id` | Unique in template; convention `d1..dN` in day order = reading order (top→bottom as authored). |
| `slotIds` | ≥ 1 ImageSlot ids. Every Slot in a multiDay template belongs to exactly one section (linter L8). |
| `textSlotIds` | 0..1 `journal`-role TextSlot ids. Empty means this section's day shows photos only (its journal, if any, makes the template ineligible for that day). |

Engine contract ([08-auto-layout-engine.md](08-auto-layout-engine.md)): the DP page partitioner
may map 2–3 *consecutive* Day Groups onto one multiDay page; Day Group *k* fills section *k* in
order. Hard filters per section: the day's placed-photo count equals `slotIds.length`, and the
day's journal text fits the section's text slot (or the section has no text slot and the day has
no journal). Sections never share photos or text across days — that is the R28 invariant, enforced
structurally rather than by scoring. v1 ships sections separated by whitespace only (≥ 0.15 in
between section bounding boxes); no rule lines — the black background provides separation.

## Spread pairs

R22: facing pages should be *considered* together, yet each Page stays independently editable, and
the whole feature may slip — it lands in **M5** ([14-roadmap.md](14-roadmap.md)). A Spread is a
view over two facing pages, never a stored entity (kernel §4), so spread pairs are two ordinary
templates linked by metadata:

```jsonc
// t-sp-a-left.json                          // t-sp-a-right.json
"kind": "spreadPair",                        "kind": "spreadPair",
"mirrorable": false,                         "mirrorable": false,
"pair": { "pairId": "sp-a", "side": "left" } "pair": { "pairId": "sp-a", "side": "right" }
```

- **Pairing is a soft bonus, not a constraint.** The engine's template scorer awards a pair bonus
  when both pages of a Spread take the two sides of one `pairId`. If the user later swaps or
  detaches one side, the other side keeps its template — nothing breaks, no warning beyond the
  normal re-layout preview (R22: "pages editable and layouts changeable independently").
- **Gutter-spanning photos** (R18): slots sharing a `spanId` across the two sides hold the *same*
  photo. The engine computes one CropState over the virtual 22 × 8.5 in Spread canvas and each
  page renders its half; the centerline falls inside the photo, which accepts center loss, while
  smart-crop keeps faces ≥ 0.5 in from the centerline (kernel §3). In `photos → slots`
  accounting the spanned photo counts once.
- Non-spanning pair templates (matched left/right compositions) are just aesthetic bookends —
  symmetric weight around the gutter.

> **Decision:** No stored `Spread` entity and no hard left/right coupling — pairing lives entirely
> in template metadata plus an engine scoring bonus. Rationale: R22 explicitly demands independent
> page editing; a coupled entity would force cascade edits and conflict with Pinned/Detached
> semantics. Storage stays per-page per [ADR-0007](adr/0007-project-storage-json-folder.md).

## Detach-on-edit and template swap

Two different user actions, two different behaviors:

**Template swap (not a detach).** Picking a different library template for a page (R10) keeps the
page referencing the library by `templateId`. Photos re-flow into the new Slots in Slot order
(`s1..sN`) preserving photo order; smart-crop recomputes each CropState for the new Slot geometry
(user pan/zoom is Slot-relative and does not survive a shape change). Fewer Slots than photos →
overflow photos go to the Unplaced bin (R10). More Slots than photos → extra Slots render empty
and are flagged amber (R14), fillable from the Unplaced or Upcoming bin (R12, R13).

**Geometry edit (detach).** Any per-page layout modification — move/resize a Slot, add or delete
an image or text container (R15) — copies the template JSON *inline* into the page record in the
chapter file as a `detachedTemplate` snapshot; the page state becomes **Detached** (which implies
**Pinned** — re-layout never touches it, kernel §7). The snapshot drops `id` and gains
`"basedOn": "<original id>"`. The library template is never mutated; other pages using it are
unaffected. Snapshot schema = template schema, linted with editor-grade severity (errors become
badges, never save blockers — see [Template linter](#template-linter)).

```mermaid
stateDiagram-v2
    [*] --> LibraryRef : page created (templateId)
    LibraryRef --> LibraryRef : template swap (R10)
    LibraryRef --> Detached : geometry edit (R15) — snapshot copied inline, page Pinned
    Detached --> Detached : further edits mutate the snapshot only
    Detached --> LibraryRef : "Reset to template" — snapshot discarded
```

"Reset to template" restores `basedOn` as the live `templateId` and re-runs slot assignment; it
does not clear the Pinned flag (the user still touched the page — only "auto-layout rest of
chapter" with the page explicitly unpinned regenerates it, R16).

## The v1 template library

Per kernel §6 the target is **~50 templates**: 54 single-page templates in the category counts
below, plus 3 spread pairs (6 template files, counted as 3 units). Every id follows the naming
convention from the schema table. `Text` = has a journal TextSlot. `Mir` = `mirrorable`.

### 1-photo — 6 (incl. full-bleed)

| Id | Kind | Text | Mir | Description |
|---|---|---|---|---|
| `t-01-fb-a` | fullBleed | no | no | Full-bleed hero, overlay caption on bottom scrim (R5, R18-feel on one page). |
| `t-01-fb-b` | fullBleed | no | no | Full-bleed hero, no caption — pure image page. |
| `t-01-text-a` | standard | yes | yes | Large photo on outer two-thirds, journal column at the gutter side. |
| `t-01-text-b` | standard | yes | yes | Tall portrait photo outer, wide journal block inner, generous whitespace. |
| `t-01-notext-a` | standard | no | no | One large centered photo in an even black field — the quiet page. |
| `t-01-notext-b` | standard | no | yes | Small photo pushed to the upper-outer corner; deliberate negative space (R20). |

### 2-photo — 8

| Id | Text | Mir | Description |
|---|---|---|---|
| `t-02-text-a` | yes | yes | Two landscapes stacked on the outer half, journal column inner. |
| `t-02-text-b` | yes | no | Side-by-side pair on top, full-width journal band below. |
| `t-02-text-c` | yes | yes | Big hero top (S), small photo bottom-outer, journal bottom-inner. |
| `t-02-text-d` | yes | yes | Tall portrait outer edge, landscape inner above the journal block. |
| `t-02-notext-a` | no | no | Two equal landscapes side by side, vertically centered. |
| `t-02-notext-b` | no | yes | Dominant square + small offset portrait, asymmetric balance. |
| `t-02-notext-c` | no | no | Two portraits as a centered diptych. |
| `t-02-notext-d` | no | no | Two stacked full-width panorama strips (aspect ≈ 2.6). |

### 3-photo — 8

| Id | Text | Mir | Description |
|---|---|---|---|
| `t-03-text-a` | yes | yes | Hero on outer half, two stacked photos inner, journal below them. |
| `t-03-text-b` | yes | no | Three across the top band, journal across the bottom. |
| `t-03-text-c` | yes | yes | Hero top, two smalls below, journal column on the outer edge. |
| `t-03-text-d` | yes | yes | Two stacked outer + hero inner, journal under the hero. |
| `t-03-notext-a` | no | yes | One big + two small in an L arrangement. |
| `t-03-notext-b` | no | no | Three equal portrait columns — triptych. |
| `t-03-notext-c` | no | no | Hero square center, two smalls in opposite corners. |
| `t-03-notext-d` | no | yes | Diagonal cascade of three mixed-aspect photos. |

### 4-photo — 8

| Id | Text | Mir | Description |
|---|---|---|---|
| `t-04-text-a` | yes | yes | "Four up with journal" — the schema example: hero + wrap of three, journal bottom-inner. |
| `t-04-text-b` | yes | yes | 2×2 grid on the outer side, journal column at the gutter. |
| `t-04-text-c` | yes | no | Filmstrip of four across the top, journal band below. |
| `t-04-text-d` | yes | no | Journal band on top, four equal photos below. |
| `t-04-notext-a` | no | no | 2×2 equal grid with 0.15 in gutters. |
| `t-04-notext-b` | no | yes | Hero on outer two-thirds + three stacked in an inner rail. |
| `t-04-notext-c` | no | yes | Mosaic: one big, one medium, two small. |
| `t-04-notext-d` | no | no | Four portraits in a row, edge to edge. |

### 5-photo — 6

| Id | Text | Mir | Description |
|---|---|---|---|
| `t-05-text-a` | yes | yes | Hero + 2×2 small grid, journal column inner. |
| `t-05-text-b` | yes | no | Three across the top, two below-left, journal below-right. |
| `t-05-text-c` | yes | no | Journal band top-outer, filmstrip of five across the bottom. |
| `t-05-notext-a` | no | yes | Mosaic: one big + four descending sizes. |
| `t-05-notext-b` | no | no | Quincunx — four corners + one center square. |
| `t-05-notext-c` | no | no | Two rows: two large above three small. |

### 6-photo — 5

| Id | Text | Mir | Description |
|---|---|---|---|
| `t-06-text-a` | yes | yes | 3×2 grid on the outer side, narrow journal rail at the gutter. |
| `t-06-text-b` | yes | yes | 2×2 grid + two stacked, journal band beneath. |
| `t-06-notext-a` | no | no | 3×2 equal grid — the workhorse. |
| `t-06-notext-b` | no | yes | Hero + five-photo mosaic. |
| `t-06-notext-c` | no | no | Two triptych rows of mixed aspects. |

### 7–8-photo — 4

| Id | Text | Mir | Description |
|---|---|---|---|
| `t-07-text-a` | yes | yes | Hero + six thumbnails, journal strip along the bottom. |
| `t-07-notext-a` | no | yes | Mosaic of seven mixed sizes, largest at the outer edge. |
| `t-08-notext-a` | no | no | 4×2 equal grid — the contact-sheet page (R20 max). |
| `t-08-notext-b` | no | yes | Two hero squares + six-thumbnail rail. |

### Month title — 4 (kind `monthTitle`, R24)

| Id | Photos | Mir | Description |
|---|---|---|---|
| `t-title-a` | 1 | no | Full-bleed photo, month name overlaid lower-outer on a scrim (name overlaps image per R24). |
| `t-title-b` | 1 | yes | Photo on the top two-thirds, month name in the black field below. |
| `t-title-c` | 3 | no | Three-photo band across the middle, month name centered above. |
| `t-title-d` | 1 | no | Small centered photo above a large centered month name — minimal. |

### Multi-day — 5 (kind `multiDay`, R28)

| Id | Sections | Photos | Description |
|---|---|---|---|
| `t-md-a` | 2 | 2 | Two half-page sections stacked: 1 photo + journal each. |
| `t-md-b` | 2 | 4 | Two side-by-side day columns: 2 photos + journal each. |
| `t-md-c` | 3 | 3 | Three vertical thirds: 1 photo + short journal each. |
| `t-md-d` | 2 | 3 | Hero day (2 photos + journal) over a minor-day strip (1 photo + journal). |
| `t-md-e` | 3 | 6 | Three days × 2 photos + one-line journal each — the dense catch-up page. |

### Spread pairs — 3 pairs (kind `spreadPair`, R22, ships in M5)

| Pair | Files | Description |
|---|---|---|
| `sp-a` | `t-sp-a-left` / `t-sp-a-right` | Panorama: one photo spans the full 22 × 8.5 spread via `spanId` (R18), overlay caption on the right page. |
| `sp-b` | `t-sp-b-left` / `t-sp-b-right` | Mirrored gallery: 3 photos + journal per side, symmetric weight around the gutter. |
| `sp-c` | `t-sp-c-left` / `t-sp-c-right` | Hero spread: full-bleed hero left, 4-photo grid + journal right. |

## Representative sketches

Normalized page outline = trim box; `░` = caption scrim; sketches are proportional, not exact.

`t-01-fb-a` — full-bleed hero, overlay caption:

```
┌────────────────────────────────────────────┐
│                                            │
│                                            │
│              s1  (S, bleed,                │
│               full page)                   │
│                                            │
│░░░░░░░░░░░░ caption on scrim ░░░░░░░░░░░░░░│
└────────────────────────────────────────────┘
```

`t-01-text-a` — photo outer, journal at the gutter (authored as right page; gutter = left):

```
┌────────────────────────────────────────────┐
│  ┌─────────┐   ┌─────────────────────────┐ │
│  │ t1      │   │                         │ │
│  │ journal │   │        s1 (S)           │ │
│  │         │   │                         │ │
│  └─────────┘   └─────────────────────────┘ │
└────────────────────────────────────────────┘
```

`t-03-notext-a` — L arrangement, no text (negative space bottom-right, R20):

```
┌────────────────────────────────────────────┐
│ ┌──────────────────────┐ ┌───────┐         │
│ │                      │ │ s2 (B)│         │
│ │       s1 (S)         │ └───────┘         │
│ │                      │ ┌───────┐         │
│ │                      │ │ s3 (B)│         │
│ └──────────────────────┘ └───────┘         │
└────────────────────────────────────────────┘
```

`t-04-text-a` — the schema example:

```
┌────────────────────────────────────────────┐
│ ┌───────────────────┐ ┌──────────────────┐ │
│ │                   │ │      s2 (A)      │ │
│ │      s1 (S)       │ └──────────────────┘ │
│ │   caption below   │            ┌───────┐ │
│ │                   │            │ s3 (B)│ │
│ └───────────────────┘            └───────┘ │
│ ┌───────────────────┐  ┌─────────────────┐ │
│ │     s4 (any)      │  │ t1 journal      │ │
│ └───────────────────┘  └─────────────────┘ │
└────────────────────────────────────────────┘
```

`t-06-text-a` — 3×2 grid + journal rail at the gutter:

```
┌────────────────────────────────────────────┐
│ ┌────────┐ ┌────────┐ ┌────────┐ ┌───────┐ │
│ │ s1 (A) │ │ s2 (B) │ │ s3 (B) │ │  t1   │ │
│ └────────┘ └────────┘ └────────┘ │journal│ │
│ ┌────────┐ ┌────────┐ ┌────────┐ │       │ │
│ │ s4 (B) │ │ s5 (B) │ │ s6 (C) │ │       │ │
│ └────────┘ └────────┘ └────────┘ └───────┘ │
└────────────────────────────────────────────┘
```

`t-title-a` — month title over full-bleed photo (R24):

```
┌────────────────────────────────────────────┐
│                                            │
│              s1  (S, bleed)                │
│                                            │
│░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  ┌─────────┐ │
│░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │ OCTOBER │ │
│░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  └─────────┘ │
└────────────────────────────────────────────┘
```

`t-md-a` — two-day page, each day's photo + journal stay together (R28):

```
┌────────────────────────────────────────────┐
│ ┌────────────────┐  ┌────────────────────┐ │  section d1
│ │    s1 (A)      │  │ t1 journal (day 1) │ │
│ └────────────────┘  └────────────────────┘ │
│ ┌────────────────────┐  ┌────────────────┐ │  section d2
│ │ t2 journal (day 2) │  │    s2 (A)      │ │
│ └────────────────────┘  └────────────────┘ │
└────────────────────────────────────────────┘
```

`sp-a` — panorama spread pair; one photo spans the gutter via `spanId` (R18, R22):

```
┌────────────────────────────────────────────┬┬────────────────────────────────────────────┐
│                                            ││                                            │
│                 s1 (spanId: pan1,          ││          s1 continues …                    │
│                  bleed, one photo          ││                                            │
│                  across 22 × 8.5)          ││░░░░░░░░░ caption on scrim ░░░░░░░░░░░░░░░░░│
└────────────────────────────────────────────┴┴────────────────────────────────────────────┘
                                        gutter (±0.5 in caution)
```

## Template linter

The linter runs in two places: as an xUnit test over the embedded library — the build fails on
any error ([13-testing-strategy.md](13-testing-strategy.md)) — and inside the editor after every
geometry edit to a Detached snapshot, where errors surface as red/amber badges but never block
saving ([09-editor-ux.md](09-editor-ux.md)). Rules, with severities:

| Rule | Sev | Check |
|---|---|---|
| **L1** slots inside trim | error | Every Slot rect within `[0,1]²`; exceeding trim requires `bleed: true`, and a bleed rect must lie within the bleed box (x ∈ [−0.0114, 1.0114], y ∈ [−0.0147, 1.0147]) and reach the bleed edge exactly on every crossed side. |
| **L2** no slot overlaps | error | Pairwise ImageSlot intersection area ≤ 0.002 of page area (rounding slop only — v1 has no intentional overlaps). |
| **L3** text/image separation | error | `journal` and `caption` TextSlots must not intersect any ImageSlot; `monthTitle` TextSlots may (R24). |
| **L4** text slots inside safe area | error | Every TextSlot: `x ≥ 0.0455` (gutter caution, as authored), `x + w ≤ 0.9659`, `y ≥ 0.0441`, `y + h ≤ 0.9559`. |
| **L5** aspect sanity | error | `abs(aspect − derived)/derived ≤ 0.03` where `derived = (w × pageW) / (h × pageH)`; `aspect ∈ [0.3, 3.5]`; `aspectTolerance ∈ [0, 0.6]`. |
| **L6** photo count | error | `photoCount == slots.length` (multiDay: summed over sections; spreadPair: `spanId` slots counted once per pair); `1 ≤ photoCount ≤ 8` (R20). |
| **L7** printable slot size | error | `w ≥ 0.1364` and `h ≥ 0.1765` (1.5 in minimum edge, keeps effective DPI printable); if `captionPolicy: "below"`, `h ≥ 0.1765 + 0.0353`. |
| **L8** structural integrity | error | Ids unique per template; multiDay `sections` partition all slots and textSlots exactly (each id in exactly one section); `sections` present iff `kind: "multiDay"`; `pair` present iff `kind: "spreadPair"`. |
| **L9** spread pair integrity | error | `spreadPair`: `mirrorable == false`; both `pairId` sides exist in the library; `spanId` sets match 1:1 across the two sides. |
| **L10** coverage sanity | warn | Total Slot area between 0.15 and 0.95 of page area — negative space is legitimate (R20), a near-empty or over-stuffed page is suspicious. |
| **L11** trim-touching slots | warn | A non-bleed Slot edge exactly on trim may show a hairline at trim variance; allowed (black background hides it), flagged for the author. |
| **L12** well-formedness | error | All rect values finite, `w, h > 0`; `schemaVersion` known; unknown fields rejected for library templates, preserved (warn) on Detached snapshots. |

Linter pseudocode shape (library gate):

```
foreach template in library:
    issues = Lint(template, pageSize)          // pure; same rules both contexts
    fail build if issues.Any(i => i.Severity == Error)
assert library ids unique && sorted load order == ordered by id   // determinism (kernel §7)
```
