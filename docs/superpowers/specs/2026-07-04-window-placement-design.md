# Runtime window placement (position + monitor)

Follow-up to the 9.24.0 runtime display-settings work. Adds public window position and
monitor control to KhaozEngine windowing so a consumer game can persist and restore full
window placement (which monitor + position + size) across launches.

## Goal

9.24.0 added `IDisplaySettings` (present mode, frame cap, window mode, size) on
`GameApp.Display`, implemented by `AppWindow`, with the pure `WindowModePlanner` for
headless-testable geometry policy. It has no public window position or monitor get/set.
`AppWindow` already drives `_window.Position` / `_window.Monitor` internally (in the
`WindowMode` planner path) but exposes nothing.

This change exposes position + monitor, extends the `DisplaySettings` snapshot so the whole
placement round-trips in one `ApplyDisplay`, and adds an ensure-visible clamp so a saved
position on a now-gone monitor is corrected on restore. Additive, non-breaking.

## Public API

New members on `IDisplaySettings` (KhaozEngine.Windowing), all implemented on `AppWindow` and
so surfaced on `GameApp.Display` for free:

```csharp
int WindowX { get; }                          // window top-left X in virtual-desktop coords
int WindowY { get; }                          // window top-left Y
void MoveTo(int x, int y);                    // mutator, symmetric with Resize(w, h)

IReadOnlyList<MonitorInfo> Monitors { get; }  // connected monitors (index, name, bounds)
int CurrentMonitorIndex { get; }              // monitor holding the window, -1 if unknown
void MoveToMonitor(int index);                // center on / cover that monitor
void EnsureVisible();                         // clamp the window back on-screen
```

The accessor shape mirrors the established `WindowWidth` / `WindowHeight` getters +
`Resize(w, h)` mutator convention already in the interface (getters for the read side, a verb
method for the mutate side), rather than a settable `WindowPosition` property.

Extended `DisplaySettings` snapshot: two optional-defaulted positional params so the whole
placement round-trips in one call.

```csharp
public readonly record struct DisplaySettings(
    PresentMode PresentMode, int FrameCapHz, WindowMode WindowMode,
    int Width, int Height,
    int X = int.MinValue, int Y = int.MinValue);   // sentinel = "position unspecified"
```

- `int.MinValue` sentinel on X or Y means "leave the window position untouched".
- `CurrentDisplay` fills real `WindowX` / `WindowY`, so a snapshot read from a live window
  round-trips fully.
- A hand-built snapshot without position (the existing 5-arg constructor) leaves the window
  where it is. That 5-arg call still compiles, so this is non-breaking.

## Pure core (headless-testable)

New file `KhaozEngine.Windowing/WindowPlacement.cs`, in the no-Silk / no-GPU style of
`WindowModePlanner`. `AppWindow` stays the only class touching the Silk windowing statics: it
builds the `MonitorInfo` list from `Monitor.GetMonitors(_window)`, reads/writes
`_window.Position`, and delegates all math here.

```csharp
public readonly record struct MonitorInfo(
    int Index, string Name, int X, int Y, int Width, int Height);

public static class WindowPlacement
{
    // Best monitor for a window rect: prefer the monitor containing the window center,
    // else the greatest-overlap monitor, else the nearest by center distance.
    // Returns -1 when the monitor list is empty (headless).
    public static int MonitorIndexFor(int wx, int wy, int ww, int wh,
        IReadOnlyList<MonitorInfo> monitors);

    // Top-left position to center a ww x wh window on a monitor.
    public static (int X, int Y) CenterOn(MonitorInfo m, int ww, int wh);

    // Clamp a window rect back on-screen. If its current monitor is gone (zero overlap
    // everywhere), relocate to the best/nearest monitor and clamp the top-left so the
    // window sits within that monitor (or at its origin when larger than the monitor).
    // Returns the input unchanged when the list is empty or the window is already
    // adequately visible (>= a small min-visible margin on both axes).
    public static (int X, int Y) ClampVisible(int wx, int wy, int ww, int wh,
        IReadOnlyList<MonitorInfo> monitors);
}
```

`ClampVisible` semantics: pick the greatest-overlap monitor. If the overlap is at least
`min(minVisible, windowExtent)` on both axes (default `minVisible` ~48 px), the window is
adequately visible and the rect is returned unchanged. Otherwise relocate onto the target
monitor (greatest overlap, or nearest center when all overlaps are zero, lowest index as the
final tiebreak) and clamp the top-left into `[mon.origin, mon.origin + mon.size - windowSize]`,
positioning at the monitor origin when the window is larger than the monitor (so at least the
title bar is reachable). Position only, no resize.

## Live behavior (AppWindow)

- `MoveTo(x, y)` records a nullable `_windowedPos` and applies `_window.Position` immediately
  when in `WindowMode.Windowed` (symmetric with how `Resize` handles `_windowedSize`).
- `WindowModePlanner.Compute` gains optional windowed-position params (defaults preserve
  today's "windowed leaves position untouched" behavior). When a `_windowedPos` is known,
  returning to Windowed restores it, so a fullscreen toggle round-trips position the same way
  size is already restored. Default-off, so consumers that never call `MoveTo` see no change.
- `Monitors` builds `MonitorInfo` from `Monitor.GetMonitors(_window)` (index, name, bounds);
  empty on headless / exception (same try-guard style as `CurrentMonitorBounds`).
- `CurrentMonitorIndex` = `WindowPlacement.MonitorIndexFor(WindowX, WindowY, WindowWidth,
  WindowHeight, Monitors)`.
- `MoveToMonitor(index)`: bounds-checked. Centers on monitor `index` when windowed; when
  borderless it also pins `_window.Monitor` to the Silk monitor at that index and re-runs
  `ApplyWindowMode` so it covers the new monitor.
- `EnsureVisible()` = `ClampVisible` then `MoveTo`.
- `ApplyDisplay` order: window mode -> resolution -> placement -> frame cap -> present. The
  placement step, when `settings.X` / `settings.Y` are not the sentinel, runs `ClampVisible`
  (against the final size) then `MoveTo`. Clamp is baked into restore, so a stale saved
  position on a now-gone monitor self-corrects. Clamping an already-visible position is a
  no-op.

## HiDPI / multi-monitor

Window `Position` and `Monitor.Bounds` come from the same GLFW screen-coordinate space, so the
clamp / center math is consistent without any DPI conversion. Size fields stay logical points
as today.

## Tests (headless, KhaozEngine.Tests/Windowing)

`WindowPlacement`:
- `MonitorIndexFor`: single monitor contains center -> 0; center on the second monitor -> 1;
  window off all monitors -> nearest; empty list -> -1.
- `CenterOn`: 1280x720 on 1920x1080 at origin -> (320, 180); on an offset monitor adds the
  offset.
- `ClampVisible`: fully-visible unchanged; off-screen-right clamps X into range; position on a
  vanished monitor relocates onto the remaining monitor; window larger than the monitor sits at
  the origin (title visible); multi-monitor picks the correct target.

Extended `WindowModePlanner`: Windowed with a known windowed position -> `SetPosition` true with
that X/Y; without -> `SetPosition` false (existing behavior preserved).

Extended `DisplaySettings`: X/Y round-trip by value + `with`; sentinel default; existing 5-arg
constructor still equals (compile-time proof the change is non-breaking).

The live Silk-window side (real `_window.Position` / monitor enumeration) is not unit-tested,
matching the existing convention that the swapchain / Silk side is exercised by the GPU tests
and the windowed smoke sample, not headless unit tests.

## Docs (full sweep)

- `KhaozEngine.Windowing/README.md` (per-package API README): position / monitor / EnsureVisible
  members, `MonitorInfo`, `WindowPlacement`, `DisplaySettings` X/Y.
- `docs/USING-KHAOZENGINE.md`: a section for the new public API with a persist / restore
  placement example (including the ensure-visible clamp on boot).
- `docs/DEPENDENCY-SEAMS.md`: only if it enumerates `IDisplaySettings` members.
- Root `README.md` catalog table: only if the Windowing one-line summary should change (no
  package added / removed, so likely untouched; verify).
- `CHANGELOG.md`: new entry, first sentence = the one-line digest.
- Guard-checked version strings: `docs/ROADMAP.md` "Current released version" and the
  `README.md` `<PackageReference>` example, bumped to match `<KhaozEngineVersion>`.
- Mechanical check: grep the new type / member names (`WindowPlacement`, `MonitorInfo`,
  `WindowX`, `MoveTo`, `MoveToMonitor`, `EnsureVisible`, `CurrentMonitorIndex`, `Monitors`)
  across all `*.md` + `CLAUDE.md`.

## Release

Own worktree `feature/window-placement`. Bump `<KhaozEngineVersion>` in `Directory.Build.props`
to the next free version (9.25.0 unless a concurrent release took it; re-read the current
version + tags on the up-to-date main right before bumping / tagging and take the next free one).
`CHANGELOG.md` entry in the same commit. `dotnet pack -c Release -o ./local-feed`. Tag
`v9.25.0`. Push main + tag right away per the engine auto-publish policy.

Ruinborne adoption (persist / restore wiring with the ensure-visible clamp) is a follow-up in
the Ruinborne repo, not this change.

## Out of scope

- Ruinborne-side wiring (separate repo).
- Per-monitor DPI-scale reporting or refresh-rate selection (present enumeration only covers
  monitor bounds; video modes are not exposed here).
- Window-size clamping in `EnsureVisible` (position-only; a window larger than every monitor is
  positioned at the origin, not shrunk).
