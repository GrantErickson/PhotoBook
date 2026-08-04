# ADR-0001 — Platform: .NET 10 / C#

Records the choice of runtime and language for every project in `PhotoBook.sln`. This is the root
decision: the UI ([ADR-0002](0002-ui-wpf.md)), renderer ([ADR-0003](0003-rendering-skiasharp.md)),
imaging backend ([ADR-0004](0004-imaging-magick-net.md)) and local ML stack
([ADR-0005](0005-local-ml-onnx-runtime.md)) all assume it.

Related docs: [02-architecture.md](../02-architecture.md) · [01-vision-and-principles.md](../01-vision-and-principles.md)

## Status

Accepted — locked by the user 2026-08-01 ("anything except Python"); recorded 2026-08-03.
Amended 2026-08-03: the target framework was corrected from .NET 9 to **.NET 10 (LTS)** at the
user's direction, before any code existed. .NET 9 was a stale default, not a reasoned choice —
see Consequences.

## Context

PhotoBook is a single-user Windows desktop app (kernel §2) built by one developer, expected to
live for years of family books. The workload is unusually wide for a desktop app — five demanding
native-integration surfaces must all be first-class in whatever stack we pick:

1. **Imaging** — decode jpg/heic/webp/png (R1) and apply non-destructive edits (R6, R11).
2. **Local ML inference** — face/saliency/aesthetic models, local-first (R25–R27).
3. **Word ingestion** — parse `.docx` journals without Word installed (R2).
4. **Cloud connectivity** — OneDrive via Microsoft Graph with MSAL sign-in (kernel §2).
5. **2D rendering + PDF** — one WYSIWYG pipeline from editor canvas to print-ready PDF.

The auto-layout engine must be a pure deterministic function (kernel §7), which rewards a strongly
typed language with value semantics and mature test tooling. Hard user constraint: **no Python**.

## Options considered

| Option | Verdict | One-line summary |
|---|---|---|
| .NET 10 / C# | **Chosen** | All five integration surfaces are first-class NuGet packages; current LTS |
| Electron + TypeScript | Rejected | Best UI ecosystem; imaging/ML/PDF all second-class native addons |
| Tauri (Rust + web UI) | Rejected | Great footprint; splits the app across two languages |
| C++ / Qt | Rejected | Everything possible, everything expensive for one developer |
| Kotlin + Compose Desktop | Rejected | Pleasant UI; HEIC and Windows identity stories are weakest |
| Python | Excluded | Ruled out by the user up front |

**Electron + TypeScript.** The richest UI component ecosystem and effortless drag-drop. It lost on
the other four surfaces: `sharp` needs custom builds for HEIC, `onnxruntime-node` works but native
addon churn across Electron versions is a recurring tax, and PDF output means Chromium print — a
second layout engine that breaks WYSIWYG-by-construction (see [ADR-0003](0003-rendering-skiasharp.md)).
The heaviest code would live in native addons anyway, giving us C++ maintenance without C++ control,
plus a 150+ MB baseline footprint for a single-user tool.

**Tauri.** Small binaries and a solid Rust core; the `image` and `ort` crates cover imaging and
ONNX. It lost because the app splits across Rust (core) and TypeScript (UI) with an IPC seam through
the middle of exactly the hot path (canvas editing at 60 fps), docking UIs in HTML are hand-rolled,
and single-developer velocity in Rust is materially lower for this kind of CRUD-plus-canvas app.

**C++ / Qt.** Full control, native speed, real docking (KDDockWidgets). Rejected on cost: every one
of the five surfaces requires hand-integrating C libraries, build times and memory-safety risk fall
on one person, and Qt licensing (LGPL dynamic linking or commercial) adds friction with no offsetting
benefit over managed .NET.

**Kotlin + Compose for Desktop.** Skia-based UI is attractive. Rejected because HEIC decode on the
JVM has no maintained first-class library, MSAL4J and Windows-native integration lag their .NET
equivalents, and the developer's existing C# fluency is worth real schedule.

## Decision

> **Decision:** Build every project in `PhotoBook.sln` on **.NET 10 / C# 14** — the current LTS
> release — with the solution layout in kernel §12 (Core, Imaging, Analysis, Ingestion, Engine,
> Rendering, App, tests). Target framework: `net10.0` for the class libraries and
> `net10.0-windows` for `PhotoBook.App` (WPF).

- Every driver library is first-class, maintained, and one `dotnet add package` away:
  `SkiaSharp` + `SkiaSharp.Views.WPF` (render + PDF), `Microsoft.ML.OnnxRuntime` (local ML),
  `Magick.NET-Q8-AnyCPU` (decode/edit incl. bundled libheif for HEIC),
  `DocumentFormat.OpenXml` (journal `.docx`), `Microsoft.Identity.Client` (MSAL/OneDrive).
- One language across UI, engine, and pipelines: no FFI/IPC seam, one debugger, one test runner
  (xUnit + Verify per [13-testing-strategy.md](../13-testing-strategy.md)).
- C# records + `System.Text.Json` map directly onto the human-diffable JSON project format
  (kernel §5) with no ORM or schema toolchain.
- Deterministic engine (kernel §7) benefits from value types, immutable records, and seeded
  `Random` — same inputs, same book, testable with golden files.

## Consequences

- **We gain:** one-stack development; self-contained `dotnet publish` (~90–130 MB with natives —
  acceptable for a desktop install); direct access to Windows APIs when needed; the entire spec's
  library set resolved with zero custom native builds.
- **We pay:** Windows-only in practice once WPF is chosen ([ADR-0002](0002-ui-wpf.md)) — accepted,
  the product is explicitly a Windows desktop app (kernel §2).
- **Why LTS, and why not .NET 9.** This ADR originally said .NET 9, which was wrong: .NET 10 went
  GA in November 2025 and .NET 9 reaches end of support on **10 November 2026** — inside this
  project's first year, before M6 ships. .NET 10 is an LTS release supported through **November
  2028**, which comfortably outlives the build. There was no technical argument for .NET 9; it was
  simply a stale default carried in from an earlier draft. For a solo, multi-year, single-machine
  desktop project, an LTS target is the obvious call: security patches keep arriving without a
  forced annual retarget, and none of the five driver packages
  (SkiaSharp, ONNX Runtime, Magick.NET, OpenXML, MSAL) needs anything newer than the LTS baseline.
- Native payloads (Magick.NET, ONNX Runtime, SkiaSharp) dominate install size; they are the price
  of first-class HEIC, ML, and PDF and are paid once per install.

## Revisit when

- A genuine cross-platform requirement appears (macOS/Linux users) — pairs with promoting Avalonia
  per [ADR-0002](0002-ui-wpf.md).
- Any of the five driver packages is abandoned upstream with no maintained fork.
- The engine's perf budgets (kernel §7: full-year re-layout < 10 s, analysis < 15 min) prove
  unreachable on managed CPU code — unlikely, and DirectML/AOT are the first resorts before a
  platform change.
