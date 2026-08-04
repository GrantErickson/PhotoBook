# PhotoBook — Setup

Everything you need to do by hand, in the order it matters. **The app is fully usable after step 1.**
Steps 2 and 3 unlock OneDrive and better automatic cropping, and both can wait until you want them.

---

## 1. Run it (nothing to configure)

Prerequisite: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.
It's already installed on this machine (10.0.301).

```powershell
cd c:\Git\PhotoBook
dotnet run --project src/PhotoBook.App
```

To open a book directly:

```powershell
dotnet run --project src/PhotoBook.App -- --open "D:\Books\Family2024"
```

**First run:** *New book…* → pick an **empty folder** (this becomes the project; originals are copied
into it, so it ends up self-contained and safely backup-able). Then *Import photos…* and choose a
folder of photos. Import files them into months by date, analysis runs automatically, and *Lay out
this month* builds the pages. *Export PDF* writes a print-ready file.

Photo formats read today: **jpg, png, webp, heic, heif, avif, tiff, bmp, gif**. HEIC works with no
Windows codec pack — the decoder is bundled.

---

## 2. OneDrive (optional) — needs an Azure app registration

Only you can create this; it takes about five minutes and costs nothing. Until it exists, the
OneDrive button explains that setup is needed and does nothing else. **Local folder import doesn't
need any of this.**

### Create the registration

1. Go to <https://entra.microsoft.com> and sign in with the Microsoft account that owns the photos.
2. **Applications → App registrations → New registration**.
3. Name: `PhotoBook` (only you ever see it).
4. **Supported account types:** choose
   *"Personal Microsoft accounts only"* — a consumer OneDrive is where your photos live.
5. **Redirect URI:** select platform **Mobile and desktop applications**, and tick the entry
   `https://login.microsoftonline.com/common/oauth2/nativeclient`.
   *(If you'd rather use the loopback flow, add `http://localhost` instead and put that same value
   in `redirectUri` below.)*
6. **Register**.
7. On the app's **Overview** page, copy the **Application (client) ID** — a GUID like
   `11111111-2222-3333-4444-555555555555`.

### Grant the permissions

8. **API permissions → Add a permission → Microsoft Graph → Delegated permissions**.
9. Add **`Files.Read`** and **`User.Read`**. Nothing more — PhotoBook never writes to OneDrive.
10. No admin consent is needed for a personal account; you'll consent yourself at first sign-in.

### Tell PhotoBook the client id

Create `%LOCALAPPDATA%\PhotoBook\onedrive.json`:

```json
{
  "schemaVersion": 1,
  "clientId": "11111111-2222-3333-4444-555555555555",
  "authority": "https://login.microsoftonline.com/consumers",
  "redirectUri": "https://login.microsoftonline.com/common/oauth2/nativeclient",
  "scopes": ["User.Read", "Files.Read"]
}
```

Only `clientId` is required; the rest have those values as defaults.

```powershell
$dir = "$env:LOCALAPPDATA\PhotoBook"
New-Item -ItemType Directory -Force $dir | Out-Null
'{ "schemaVersion": 1, "clientId": "PASTE-YOUR-GUID-HERE" }' | Set-Content "$dir\onedrive.json"
```

Alternatively set the environment variable `PHOTOBOOK_ONEDRIVE_CLIENT_ID`, which wins over the file.
`PHOTOBOOK_ONEDRIVE_CONFIG` points at a different config file if you want one.

**The client id is not a secret** — it identifies the app, not you — but it deliberately lives
outside the project folder so `book.json` stays shareable. Sign-in tokens are cached encrypted under
your Windows account (DPAPI) and never enter the project.

### How the picking workflow goes

Your wife adds photos to a OneDrive **album** (e.g. "Book 2024") from her phone or the web, and
PhotoBook syncs that album. Final trimming happens in the app's grid. A plain OneDrive folder works
as a source too.

**One honest unknown:** exactly which people-tag metadata Microsoft Graph exposes for consumer
OneDrive isn't settled — the design docs flag it as a spike for milestone M1
([docs/05](docs/05-ingestion-and-photo-sources.md),
[ADR-0011](docs/adr/0011-onedrive-graph-ingestion.md)). If the tags come through, named people
become the highest-priority focus regions and cropping gets noticeably smarter. If they don't,
everything else still works and local face detection covers it.

---

## 3. Better automatic cropping (optional) — three ONNX model files

Out of the box, PhotoBook analyses photos with classical computer vision: spectral-residual
saliency to find the subject, Laplacian variance for sharpness, histogram statistics for exposure,
and a colourfulness metric. **This works with no downloads** and drives both smart cropping and the
S/A/B/C quality ranking.

Adding the neural models improves it — real face detection instead of inferred subjects, and an
aesthetic score trained on human ratings. They aren't bundled because of size and licensing.

Create a `models` folder next to the app executable
(`src\PhotoBook.App\bin\Debug\net10.0-windows\models\`) and drop in:

| File name (exact) | What it does | Size | Where to get it |
|---|---|---|---|
| `face_detection_yunet_2023mar.onnx` | Face detection | ~230 KB | [OpenCV Zoo — face_detection_yunet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) |
| `u2netp.onnx` | Salient-object detection | ~4.5 MB | [U-2-Net releases](https://github.com/xuebinqin/U-2-Net) (the small "u2netp" variant) |
| `nima-mobilenet.onnx` | Aesthetic scoring | ~13 MB | NIMA/MobileNet trained on AVA — e.g. [idealo/image-quality-assessment](https://github.com/idealo/image-quality-assessment), exported to ONNX |

Each file is picked up independently: supply only the face model and you get faces plus classical
everything-else. Missing files are never an error — the app reports which analyzer it used. Model
files are git-ignored.

---

## 4. Fonts (cosmetic, worth doing before you print)

The page designs call for **Source Serif 4** (journal text), **Source Sans 3** (captions), and
**Playfair Display** (month titles). All three are free under the SIL Open Font License. If they
aren't installed, the renderer substitutes a system face and preflight tells you which family it
actually used — so a book still renders and exports, it just isn't the intended typography.

Install from [Google Fonts](https://fonts.google.com): search each name, *Download family*, then
select the `.ttf` files and *Install for all users*.

---

## What I could not do for you

| Thing | Why it needs you |
|---|---|
| Azure app registration / client id | Requires signing in as the account that owns the photos. |
| ONNX model files | Multi-megabyte third-party downloads with their own licences. |
| The OFL fonts | Installed per machine; trivial but manual. |
| A real photo set | Everything so far was verified against generated test images. **Point it at a real month of your photos** — that is the only way to judge whether the automatic layout is actually good, which is the whole point of the project. |

---

## Where things are

| Path | What |
|---|---|
| [docs/](docs/) | The full design set — start at [docs/README.md](docs/README.md); the layout algorithm is [docs/08](docs/08-auto-layout-engine.md) |
| `src/PhotoBook.Core` | Domain model, JSON project format, the 60-template library |
| `src/PhotoBook.Engine` | The auto-layout engine (pure and deterministic) |
| `src/PhotoBook.Rendering` | One SkiaSharp renderer for both screen and PDF, plus preflight |
| `src/PhotoBook.App` | The WPF application |
| `%LOCALAPPDATA%\PhotoBook\` | OneDrive config, MSAL token cache, recent-books list |

A project folder holds `book.json`, `photos.json`, `journal.json`, `chapters/YYYY-MM.json`,
`originals/` (immutable copies) and `cache/`. Everything except `cache/` is human-readable JSON and
worth backing up; `cache/` regenerates itself and can be deleted at any time.

## Running the tests

```powershell
dotnet test          # 205 tests; they generate their own image and .docx fixtures
```

Two env-gated helpers exist for eyeballing real output:

```powershell
$env:PHOTOBOOK_VISUAL_DUMP = "C:\temp\pages"   # renders a laid-out month to PNGs
$env:PHOTOBOOK_DEMO_OUT    = "C:\temp\demo"    # builds a 50-photo project to open in the app
dotnet test --filter "FullyQualifiedName~VisualDumpTests"
dotnet test --filter "FullyQualifiedName~DemoProjectBuilder"
```
