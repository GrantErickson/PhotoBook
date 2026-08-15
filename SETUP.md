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

Sign-in uses the **Windows account broker (WAM)**: you get the native Windows account picker rather
than a browser window, single sign-on from the account you already use on this PC, Windows Hello and
passkeys working the way the OS intends, and refresh tokens held by Windows instead of by a file we
manage. If the broker can't run, MSAL falls back to the system browser on its own — which is why you
register two redirect URIs below.

### Create the registration

1. Go to <https://entra.microsoft.com> and sign in with the Microsoft account that owns the photos.
2. **Applications → App registrations → New registration**.
3. Name: `PhotoBook` (only you ever see it).
4. **Supported account types:** choose
   *"Personal Microsoft accounts only"* — a consumer OneDrive is where your photos live.
5. **Register** (leave the redirect URI blank here; it's easier to add both in one place next).

### Add the redirect URIs

6. Open **Authentication → Add a platform → Mobile and desktop applications**.
   The platform matters: it is what marks the app as a *public client*. Adding these under **Web**
   instead will fail at sign-in with a confusing error.
7. In **Custom redirect URIs**, add both of these, replacing `<client-id>` with the GUID from the
   app's **Overview** page:

   | Redirect URI | Why |
   |---|---|
   | `ms-appx-web://microsoft.aad.brokerplugin/<client-id>` | The WAM broker — the normal path |
   | `http://localhost` | Browser fallback when the broker is unavailable |

8. Save. Then copy the **Application (client) ID** from **Overview**.

### Grant the permissions

9. **API permissions → Add a permission → Microsoft Graph → Delegated permissions**.
10. Add **`Files.Read`** and **`User.Read`**. Nothing more — PhotoBook never writes to OneDrive.
11. No admin consent is needed for a personal account; you'll consent yourself at first sign-in.

### Tell PhotoBook the client id

Put `onedrive.json` in the **repository root** with **your** client id. The build copies it next to
the executable, where the app looks for it — so setting up another machine is *clone, drop this one
file in, run*. It is git-ignored and stays that way (see "Why not just commit it" below):

```json
{
  "schemaVersion": 1,
  "clientId": "00000000-0000-0000-0000-000000000000",
  "authority": "https://login.microsoftonline.com/consumers",
  "scopes": ["User.Read", "Files.Read"]
}
```

`clientId` is the only field you need; the others are already the defaults.

> **Do not set `redirectUri` here.** Leave it out and MSAL uses the broker's own redirect, falling
> back to `http://localhost` for the browser path. Pinning a value overrides both and is the fastest
> way to break sign-in. The field exists only for a registration that deviates from the above.

```powershell
'{ "schemaVersion": 1, "clientId": "PASTE-YOUR-GUID-HERE" }' | Set-Content c:\Git\PhotoBook\onedrive.json
```

The placeholder GUIDs above are rejected on purpose: copying the sample without editing it gives you
the friendly "not set up yet" message rather than an opaque sign-in failure.

### Where the client id can live

First hit wins, most deliberate to most general:

| Where | When you'd use it |
|---|---|
| `PHOTOBOOK_ONEDRIVE_CLIENT_ID` | A one-off run or a CI check; beats every file. |
| `PHOTOBOOK_ONEDRIVE_CONFIG` | Points at a config file anywhere — e.g. one in your own OneDrive, so every machine you own picks up the same file after setting this variable once. |
| `onedrive.json` in the repository root | **The normal choice.** Copied next to the executable at build. |
| `%LOCALAPPDATA%\PhotoBook\onedrive.json` | The per-machine default, and where the app writes a template if you click OneDrive before setting any of the above. |

### Why not just commit it

Tempting — it would make a fresh clone work with nothing to copy. Don't, because **this repository is
public**. The client id is genuinely not a secret (it names the app, not you, and no signature
depends on it), but publishing it means anyone who clones can sign in against *your* app
registration: their consent grants land on it, Graph throttles partly per app id, and someone can
stand up a different app under your id whose Microsoft consent screen still says "PhotoBook". So the
root `onedrive.json` is git-ignored, and the one-file copy is the price of that.

To set up another machine, copy that one file across — or set `PHOTOBOOK_ONEDRIVE_CONFIG` once to a
path that syncs, and skip even that.

Tokens are a different matter and never travel: they live in the Windows broker, with a
DPAPI-encrypted MSAL cache under your Windows account as backup. `%LOCALAPPDATA%\PhotoBook\msal.cache`
is encrypted to *this* Windows account and will not decrypt elsewhere — leave it behind and sign in
again, which is usually one click via single sign-on.

### If sign-in fails

| What you see | What it means |
|---|---|
| "…missing the broker redirect URI" | Step 7's `ms-appx-web://…` entry is absent or has the wrong client id. |
| "…not allowed to sign in personal Microsoft accounts" | Step 4 was set to an organizational option; change it to personal accounts. |
| "OneDrive rejected the application id" | The GUID in `onedrive.json` doesn't match the registration. |
| The account picker never appears | Usually the broker redirect URI; check step 7 before anything else. |

### How the picking workflow goes

Your wife adds photos to a OneDrive **album** (e.g. "Book 2024") from her phone or the web, and
PhotoBook syncs that album. Final trimming happens in the app's grid. A plain OneDrive folder works
as a source too.

**Settled, and the answer was no:** Microsoft Graph does **not** expose OneDrive people tags. This
was measured against your own account on 2026-08-04 — 47 items across 6 albums, no tag or people
property on any of them. So the people you've tagged in OneDrive won't carry into PhotoBook, and
faces come from local detection instead (step 3 below). Everything else about the OneDrive path
works: albums, downloads, and capture dates, which Graph returned for 43 of those 47 items.

Two things that showed up in the same measurement and are worth knowing: your albums are **mostly
auto-generated by OneDrive** ("Your weekend recap…", date-named), so name the curated one clearly
to find it among them; and albums can contain **videos**, which PhotoBook skips — video is out of
scope.

---

## 3. Image analysis — nothing to do

**The neural models now ship with the repository** and are copied next to the executable at build
time, so face detection and saliency work the moment you run the app. There is no download step.

| Model | Purpose | Licence |
|---|---|---|
| YuNet (`face_detection_yunet_2023mar.onnx`, 227 KB) | Face detection — faces become the highest-priority thing smart-crop keeps in frame | MIT |
| U²-Netp (`u2netp.onnx`, 4.4 MB) | Salient-object detection — finds the subject when there is no face | Apache-2.0 |

Both licences permit redistribution, which is why they are committed. Provenance, verified tensor
shapes and the test that guards them are in [models/README.md](models/README.md).

**One stage is still classical:** NIMA aesthetic scoring. No maintained ONNX build of it exists —
every published implementation ships Keras or PyTorch weights, and converting them would add a
Python toolchain plus unclear terms on the AVA-trained weights. So the aesthetic component of the
quality score comes from the classical proxy (sharpness, exposure, colourfulness). The app says so
rather than pretending otherwise: it reports a *partial* ONNX install naming the missing file.

If you ever obtain a NIMA ONNX, drop it into [models/](models/) as `nima-mobilenet.onnx` and it is
picked up automatically — the analyzer checks the output is a 10-bin distribution and ignores the
file if it isn't. That one filename stays git-ignored so a large third-party model is never
committed by accident.

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

| Thing | Why it needs you | Status |
|---|---|---|
| Azure app registration / client id | Requires signing in as the account that owns the photos. | ✅ Done — verified working end to end on 2026-08-04 |
| The two ONNX models | — | ✅ Now bundled in the repo; no action |
| A NIMA aesthetic model | No redistributable ONNX build exists (§3). | Not planned; the classical proxy covers it |
| The OFL fonts | Installed per machine; trivial but manual. | Still yours to do (§4) |
| A real photo set | Everything so far was verified against generated test images. **Point it at a real month of your photos** — that is the only way to judge whether the automatic layout is actually good, which is the whole point of the project. | Still yours to do |

---

## Where things are

| Path | What |
|---|---|
| [docs/](docs/) | The full design set — start at [docs/README.md](docs/README.md); the layout algorithm is [docs/08](docs/08-auto-layout-engine.md) |
| `src/PhotoBook.Core` | Domain model, JSON project format, the 60-template library |
| `src/PhotoBook.Engine` | The auto-layout engine (pure and deterministic) |
| `src/PhotoBook.Rendering` | One SkiaSharp renderer for both screen and PDF, plus preflight |
| `src/PhotoBook.App` | The WPF application |
| [models/](models/) | Bundled YuNet + U²-Netp ONNX models, their licences, and provenance |
| `onedrive.json` (repo root) | Your OneDrive client id; git-ignored, copied next to the exe at build (§2) |
| `%LOCALAPPDATA%\PhotoBook\` | Fallback OneDrive config, MSAL token cache, editor settings |
| `%APPDATA%\PhotoBook\recent.json` | The recent-books list |

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
