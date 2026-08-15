# 10 — Styles & Typography

This document owns the `Style` entity: its fields, the global → chapter → page cascade with
override rules (R23), the bundled fonts and default sizes, the overlay-caption scrim, the global
image-border style, the black-background-now / image-background-later hook (R21), and month-title
typography (R24). Style answers "how things look"; *where* text and photos go is owned by
[07-layout-template-system.md](07-layout-template-system.md) and
[08-auto-layout-engine.md](08-auto-layout-engine.md).

Related docs: [03-domain-model.md](03-domain-model.md) ·
[04-project-format-and-storage.md](04-project-format-and-storage.md) ·
[07-layout-template-system.md](07-layout-template-system.md) · [09-editor-ux.md](09-editor-ux.md) ·
[11-journal-ingestion.md](11-journal-ingestion.md) · [12-pdf-export.md](12-pdf-export.md)

## 1. The Style object

`Style` is a plain serializable object (camelCase, `System.Text.Json`, stored per
[04-project-format-and-storage.md](04-project-format-and-storage.md)). The full global style
lives in `book.json`; chapter and page levels store **sparse partial** copies containing only the
fields they override (§2). Defaults below are the shipped values.

```jsonc
{
  "journalText":   { "family": "Source Serif 4",  "sizePt": 10.5, "color": "#FFFFFF",
                     "weight": "regular", "lineHeight": 1.35 },
  "captionText":   { "family": "Source Sans 3",   "sizePt": 8.5,  "color": "#FFFFFF",
                     "weight": "regular", "lineHeight": 1.25 },
  "monthTitle":    { "family": "Playfair Display", "sizePt": 64,  "color": "#FFFFFF",
                     "weight": "regular", "lineHeight": 1.0 },
  "imageBorder":   { "enabled": false, "widthPt": 2.0, "color": "#FFFFFF",
                     "cornerRadiusPt": 0 },
  "overlayScrim":  { "enabled": true, "maxOpacity": 0.6, "paddingPt": 12 },
  "background":    { "kind": "solid", "color": "#000000" }   // "image" reserved — see §6
}
```

Field rules:

- `journalText` and `captionText` are **independent** blocks — R23 explicitly requires journal
  and caption sizes to differ, so nothing is derived from a shared base size.
- Colors are `#RRGGBB` sRGB. Defaults are white-on-black per kernel §3 (`#000000` page,
  `#FFFFFF` text).
- Sizes are points at print scale (1 pt = 1/72 in on the 11 × 8.5 in trim); the renderer scales
  identically for screen and PDF, so a style change is WYSIWYG
  ([ADR-0003](adr/0003-rendering-skiasharp.md)).
- There is **no auto font-shrink** anywhere: a day's journal text is atomic — it may span the two
  pages of one Spread but never crosses Spreads; if text can't fit, the engine must choose
  roomier templates, not squeeze type. Fitting mechanics: [11-journal-ingestion.md](11-journal-ingestion.md)
  and [08-auto-layout-engine.md](08-auto-layout-engine.md).

## 2. Cascade: global → chapter → page (R23)

> **Decision:** Style resolves by a three-level sparse-override cascade — **book** (global, in
> `book.json`) → **chapter** (optional `styleOverride` in `chapters/YYYY-MM.json`) → **page**
> (optional `styleOverride` on the page record). Rationale (inline): R23's core promise is "change
> it once, it changes everywhere" — borders on *all images on all pages*, fonts globally — while
> still allowing a special chapter or page to deviate; a CSS-like cascade with only three fixed
> levels delivers both with trivially predictable resolution. Storage shape per
> [ADR-0007](adr/0007-project-storage-json-folder.md).

Resolution is a **field-level deep merge** at render time — the most specific level that sets a
leaf field wins; unset fields fall through:

```text
effective(page) = merge(bookStyle, chapter.styleOverride?, page.styleOverride?)
// merge is per leaf field: {"captionText": {"sizePt": 9.5}} at chapter level changes
// caption size for that chapter only; family, color, etc. still come from the book.
```

Override rules:

- Overrides are **sparse**: an override object contains only the fields the user changed at that
  level. Removing an override restores inheritance — there is no "copied-down" snapshot to drift.
- There is **no slot-level style** in v1. The only per-slot visual knob is the template's
  `captionPolicy` (`none|below|overlay`, kernel §6), which selects *whether/where* a caption
  renders — its typography always comes from the cascade.
- UI: the style panel ([09-editor-ux.md](09-editor-ux.md)) shows every field with an inheritance
  chip — *Book*, *Chapter*, or *Page* — and a per-field *Reset to inherited* action. Editing a
  field while scoped to Chapter/Page writes it into that level's override.
- Style edits never touch geometry: changing a style cannot move a Slot, so it never Pins or
  Detaches a page. It is one undoable command per edit gesture.

## 3. Bundled fonts and default sizes

> **Decision:** Three OFL (SIL Open Font License) families ship inside the app and are
> subset-embedded in every PDF (kernel §3) — no dependency on installed system fonts, so a book
> renders identically on any machine and at the print service. Inline rationale: OFL permits
> redistribution and embedding; Segoe UI et al. do not travel. Export path:
> [ADR-0006](adr/0006-pdf-skdocument.md).

| Role | Family | Default size | Weight | Line height | Notes |
|---|---|---|---|---|---|
| Journal text | **Source Serif 4** | **10.5 pt** | Regular | 1.35 | Long-form readability at print size |
| Captions | **Source Sans 3** | **8.5 pt** | Regular | 1.25 | Below-image and overlay captions |
| Month titles | **Playfair Display** | **64 pt** | Regular | 1.0 | Display face; R24 title pages only |

Font files live in `PhotoBook.App` resources; the renderer loads them as `SKTypeface` once at
startup. Missing-glyph fallback on screen only: Segoe UI Symbol for the rare emoji/dingbat in
journal text; preflight ([12-pdf-export.md](12-pdf-export.md)) warns when fallback glyphs would
appear in export, because the fallback face is not embedded.

## 4. Captions and the overlay scrim (R5)

A Slot's `captionPolicy` decides placement; style decides appearance:

- **`below`**: caption renders under the photo in `captionText` style, left-aligned to the Slot,
  single paragraph, max 3 lines then hard-truncated with an ellipsis and a preflight warning.
- **`overlay`** (full-bleed and edge-to-edge photos, R5): the caption sits inside the photo's
  bottom edge on a **scrim** so white text stays legible on any image: a vertical linear gradient
  from **0% black at the scrim's top edge to `maxOpacity` (default 60%) black at the photo's
  bottom edge**. Scrim height = caption text block height + 2 × `paddingPt` (default 12 pt);
  text is inset `paddingPt` from the photo's left/bottom edges. `overlayScrim.enabled: false`
  disables the gradient globally for users who prefer raw overlay text.
- Overlay captions respect the safe margin and the Spread gutter caution zone (kernel §3): the
  layout engine may not place an overlay caption within 0.5 in of the spread centerline.

Most photos have no caption (R5); an empty caption renders nothing — no empty scrim.

### The panel scrim — text a template puts *on* a photo

A caption anchored to a photo's bottom edge is one case. The other, added with the deliberate-overlap
templates of [07-layout-template-system.md](07-layout-template-system.md), is a journal block or a
month title that a template places in the quiet part of a photo (`textSlot.scrim: true`). Thirteen
shipped templates do this and it is what makes a single-photo page a full-page photo instead of a
photo with a margin of black beside it.

The bottom-anchored gradient above is the wrong instrument there: these blocks sit *inside* an image,
where a one-sided ramp leaves the first line on bare photo and cuts hard below the last. So they get a
**panel scrim** instead:

- `maxOpacity` black over the laid-out text plus `paddingPt` on every side, fading to nothing over a
  further `paddingPt` — a soft plate, not a box.
- Sized to the **words**, not to the slot: the widest laid-out line sets the width, alignment decides
  which edge it hangs from, and the height is the block that actually rendered. A two-line entry in a
  tall journal slot gets a two-line plate.
- **Clipped to the photos the text sits on**, so a hand-edited Detached page whose text hangs off its
  photo darkens the photo and not the page around it.
- Same switches as the caption scrim: `overlayScrim.enabled: false` removes it, `maxOpacity` and
  `paddingPt` tune it. A text slot that touches no placed photo never draws one.

Month titles take the same treatment whenever they land on a photo (R24, §7), which is what makes
`t-title-a` — a full-bleed photo with the month name on it — readable on any picture the engine picks.

> **Decision:** **Scrims are built from flat fills and plain linear/radial gradients, never a blur.**
> Those are the primitives `SKDocument.CreatePdf` writes natively; a mask filter would rasterize on the
> PDF path and silently break the one-draw-path guarantee of [ADR-0003](adr/0003-rendering-skiasharp.md).
> The same rule governs the overlap lift of doc 07.

## 5. Image borders — one switch, every image (R23)

`imageBorder` applies to **all placed photos on all pages** in the level's scope — flipping
`enabled` at book level restyles the entire book in one undoable command. Rendering rules:

- The border strokes **inside** the visible image rect (`widthPt` default 2 pt, default white),
  so enabling borders never changes layout geometry or crop math.
- When a photo's `CropState.zoom < 1` (letterbox, [09-editor-ux.md](09-editor-ux.md) §3.3), the
  border wraps the **visible image**, not the empty Slot — a frame around black show-through
  would read as a mistake.
- `cornerRadiusPt` > 0 rounds the image corners and clips the photo to match; the crop math is
  unaffected.
- Full-bleed slots ignore borders on edges that run off the Bleed box (a border cut by trimming
  looks like an error); inner edges still draw.

## 6. Background: black now, image later (R21)

v1 ships exactly one background: `{ "kind": "solid", "color": "#000000" }` — solid black on every
page, per the locked decision (kernel §2). The color is user-editable through the cascade, but the
default and the tested path is black with white text.

The R21 hook is deliberately narrow so the future change stays cheap: the renderer's first pass
per page is `DrawBackground(style.background, pageRect)` — everything else composites above it.
When scrapbook-style image backgrounds arrive, the change is (a) extend `background` with
`kind: "image"`, `photoId`, and an opacity/scrim field, (b) bump `schemaVersion` in `book.json`
with a trivial migration (old files read as `kind: "solid"`), and (c) teach preflight about
low-contrast text over image backgrounds. No template, engine, or editor surface changes —
backgrounds are style, not layout.

## 7. Month-title typography (R24)

Each Chapter opens with a `monthTitle`-kind template
([07-layout-template-system.md](07-layout-template-system.md)) whose `monthTitle` TextSlot
renders in the `monthTitle` style — **Playfair Display 64 pt**, dramatically larger than journal
(10.5 pt) and caption (8.5 pt) text, satisfying R24's "larger font" requirement by an order of
magnitude rather than a nudge. Rules:

- Content is the month name, title-case, localized from the Chapter date (`"June"`); an optional
  year subtitle renders in Source Sans 3 at 14 pt with +0.05 em tracking beneath it.
- The title TextSlot **may overlap a photo** (R24). When the template flags the overlap, the
  title gets the §4 overlay scrim treatment automatically so it stays readable on any image the
  engine picks.
- Titles always stay inside the 0.375 in safe margin and never enter the Spread gutter caution
  zone; the engine's smart-crop keeps faces out from under the title block by treating the title
  rect as an avoid region during slot assignment
  ([08-auto-layout-engine.md](08-auto-layout-engine.md)).
- All three properties (family, size, color) are ordinary cascade fields — a chapter can restyle
  its own title page via a chapter-level override without touching the rest of the book.
