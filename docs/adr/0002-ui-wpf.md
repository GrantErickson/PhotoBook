# ADR-0002 — UI framework: WPF + CommunityToolkit.Mvvm + AvalonDock

Records the choice of desktop UI framework and its two companion libraries for
`src/PhotoBook.App`. Assumes the .NET 10 platform decision in
[ADR-0001](0001-platform-dotnet.md) and the SkiaSharp canvas decision in
[ADR-0003](0003-rendering-skiasharp.md).

Related docs: [09-editor-ux.md](../09-editor-ux.md) · [02-architecture.md](../02-architecture.md)

## Status

Accepted — locked by the user 2026-08-01; recorded 2026-08-03. Avalonia is the documented
runner-up; WinUI 3 is rejected.

## Context

The editor shell (kernel §8) is a docking-heavy, drag-drop-heavy workspace: a Photos tab (grid with
inspect/reorder/re-date/tier/focus edits, R6, R25, R26), a Pages tab with single-page or Spread
view (R8), drag-drop with swap semantics and pan/zoom cropping (R9), and a dockable Unplaced /
Upcoming bin panel the user can place at the **bottom or side** (R10, R12, R13), with empty slots
flagged amber (R14) and a per-page layout override mode (R15). The page canvas itself is a
SkiaSharp surface (`SKElement`), so the framework's job is chrome, docking, data grids, dialogs,
and input plumbing — not page rendering. Constraints: Windows-only product, one developer, and the
docking + drag-drop requirements are the hardest UI features on the list.

## Options considered

| Option | Verdict | One-line summary |
|---|---|---|
| WPF + CommunityToolkit.Mvvm + AvalonDock | **Chosen** | Only stack where docking, drag-drop, and SKElement are all mature today |
| Avalonia 11 | Runner-up | Best modern XAML; docking and third-party ecosystem not proven enough |
| WinUI 3 | Rejected | Tooling and docking immaturity; no offsetting benefit for a desktop-only app |
| WebView2 / Blazor hybrid | Rejected | Reintroduces the Electron trade rejected in ADR-0001 |

**Avalonia 11 (runner-up).** What it wins: a modern, actively developed XAML dialect, its own
Skia-based compositor (philosophically aligned with our renderer), and a real path to macOS/Linux.
Why it lost, concretely: there is no AvalonDock-class docking library — `Dock.Avalonia` exists but
is far less battle-tested for save/restore layouts and nested tool panes, which R10's
user-movable bin makes a day-one requirement, not a nice-to-have; third-party controls, designer
tooling, and drag-drop edge cases (multi-item drag previews, inter-panel drop targets per R9/R12)
all trail WPF's twenty years of ecosystem. On a single-developer schedule those gaps are rework
risk in exactly the app's hardest UI area. It remains the named escape hatch: the MVVM layer
(CommunityToolkit.Mvvm) and all non-App projects are UI-framework-agnostic, so a future port
rewrites views only.

**WinUI 3 (rejected).** Modern visuals and the nominal "future of Windows UI", but: no mature
docking library at all, Windows App SDK deployment friction, XAML tooling still behind WPF (no
designer, weaker hot reload for this workload), and community answers for hard problems are thin.
Its advantages (touch, modern styling, store packaging) buy nothing for a keyboard-and-mouse
pro tool. Docking/tooling immaturity is disqualifying on its own.

**WebView2 / Blazor hybrid (rejected).** Web UI inside a WPF host means two rendering worlds,
JS-interop across the canvas hot path, and hand-rolled docking — the same trade already rejected
with Electron in [ADR-0001](0001-platform-dotnet.md), minus Electron's ecosystem.

## Decision

> **Decision:** `src/PhotoBook.App` is **WPF on .NET 10** (`net10.0-windows`), with **CommunityToolkit.Mvvm 8.x** for
> MVVM plumbing and **AvalonDock 4.x** for the docking workspace. The page canvas is hosted via
> `SkiaSharp.Views.WPF.SKElement` per [ADR-0003](0003-rendering-skiasharp.md).

- AvalonDock `LayoutAnchorable` panes give the bin panel bottom/side placement, drag-to-redock,
  and layout persistence (serialize to `book.json`-adjacent app settings) — R10 satisfied with
  configuration, not code.
- CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`) keep
  ViewModels boilerplate-free and make the command-pattern undo/redo model (kernel §8) uniform.
- WPF's mature drag-drop (`DragDrop.DoDragDrop`, adorner previews) covers grid reorder (R6),
  slot swap semantics (R9), and bin-to-slot placement (R12) with well-documented patterns.
- Single-writer threading over the model with a Channels-based background job queue (kernel §8);
  ViewModels marshal to the dispatcher, heavy work never touches the UI thread.

## Consequences

- **We gain:** the only combination where every hard UI requirement has an off-the-shelf,
  battle-tested answer today; per-monitor-v2 DPI awareness (declared in the app manifest) for
  crisp thumbnails on mixed-DPI setups; a huge body of prior art for every WPF problem we will hit.
- **We pay:** WPF is in maintenance mode — fine for a stable API, but no new platform features;
  AvalonDock is community-maintained, so we isolate it behind a thin `IWorkspaceLayoutService`
  (dock/undock/persist operations only) to contain a possible future swap; WPF XAML is verbose
  relative to Avalonia's.
- The App project is the **only** assembly with WPF references (dependency rule, kernel §12), so
  the blast radius of this decision is one project.

## Revisit when

- Cross-platform becomes a real requirement — promote Avalonia; the port cost is the App project's
  views plus a docking replacement, and that cost should be re-estimated against Dock.Avalonia's
  maturity at that time.
- AvalonDock stops receiving fixes and a blocking bug appears in layout persistence or nested
  docking.
- WinUI 3 ships a credible docking story and designer/tooling parity — re-evaluate only if we are
  rewriting views anyway.
