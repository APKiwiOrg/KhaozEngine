# Runtime Window Placement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add public window position + monitor control to KhaozEngine windowing so a consumer can persist and restore full window placement (monitor + position + size) across launches.

**Architecture:** A new pure `WindowPlacement` static (+ `MonitorInfo` record) holds all placement math (which monitor, center-on, clamp-visible), headless-testable like `WindowModePlanner`. `AppWindow` stays the only Silk toucher: it builds `MonitorInfo` from Silk, reads/writes `_window.Position`, and delegates math. `DisplaySettings` gains optional X/Y so `ApplyDisplay` round-trips placement; `IDisplaySettings` gains the live accessors.

**Tech Stack:** C# net10.0, Silk.NET.Windowing (`Monitor.GetMonitors`, `IMonitor.Index/Name/Bounds`, `IWindow.Position`), xUnit headless tests.

## Global Constraints

- Only `AppWindow` touches Silk.NET/GLFW windowing statics; all math is pure and headless-testable.
- Additive / non-breaking: existing `DisplaySettings` 5-arg ctor and 7-arg `WindowModePlanner.Compute` calls must still compile.
- No em-dashes / semicolons in prose (comments, docs, CHANGELOG).
- One shared version line `<KhaozEngineVersion>` in `Directory.Build.props`; bump to next free (9.25.0 unless taken), CHANGELOG entry in the same commit, pack to `./local-feed`, tag `vX.Y.Z`, push main + tag.

---

### Task 1: Pure `WindowPlacement` + `MonitorInfo`

**Files:**
- Create: `KhaozEngine.Windowing/WindowPlacement.cs`
- Test: `KhaozEngine.Tests/Windowing/WindowPlacementTests.cs`

**Interfaces:**
- Produces: `MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height)`; `WindowPlacement.MonitorIndexFor(int wx,int wy,int ww,int wh, IReadOnlyList<MonitorInfo>) -> int`; `WindowPlacement.CenterOn(MonitorInfo, int ww, int wh) -> (int X,int Y)`; `WindowPlacement.ClampVisible(int wx,int wy,int ww,int wh, IReadOnlyList<MonitorInfo>) -> (int X,int Y)`.

- [ ] **Step 1: Write failing tests** in `WindowPlacementTests.cs`:

```csharp
using System.Collections.Generic;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public class WindowPlacementTests
{
    static IReadOnlyList<MonitorInfo> TwoMonitors() => new[]
    {
        new MonitorInfo(0, "Primary",  0, 0, 1920, 1080),
        new MonitorInfo(1, "Right", 1920, 0, 2560, 1440),
    };

    [Fact]
    public void MonitorIndexFor_returns_the_monitor_containing_the_window_center()
    {
        Assert.Equal(0, WindowPlacement.MonitorIndexFor(100, 100, 1280, 720, TwoMonitors()));
        Assert.Equal(1, WindowPlacement.MonitorIndexFor(2100, 100, 1280, 720, TwoMonitors()));
    }

    [Fact]
    public void MonitorIndexFor_off_all_monitors_picks_the_nearest_and_empty_is_minus_one()
    {
        Assert.Equal(1, WindowPlacement.MonitorIndexFor(5000, 100, 100, 100, TwoMonitors())); // nearest = right
        Assert.Equal(-1, WindowPlacement.MonitorIndexFor(0, 0, 100, 100, new List<MonitorInfo>()));
    }

    [Fact]
    public void CenterOn_centers_within_the_monitor_bounds_including_offset()
    {
        Assert.Equal((320, 180), WindowPlacement.CenterOn(new MonitorInfo(0, "m", 0, 0, 1920, 1080), 1280, 720));
        Assert.Equal((2240, 180), WindowPlacement.CenterOn(new MonitorInfo(1, "m", 1920, 0, 1920, 1080), 1280, 720));
    }

    [Fact]
    public void ClampVisible_leaves_an_already_visible_window_untouched()
    {
        Assert.Equal((100, 100), WindowPlacement.ClampVisible(100, 100, 1280, 720, TwoMonitors()));
    }

    [Fact]
    public void ClampVisible_pulls_an_offscreen_window_back_onto_a_monitor()
    {
        // Saved on a now-gone monitor at x=2600 while only a single 1920x1080 monitor remains.
        var one = new[] { new MonitorInfo(0, "Only", 0, 0, 1920, 1080) };
        var (x, y) = WindowPlacement.ClampVisible(2600, 100, 1280, 720, one);
        Assert.Equal(640, x); // 1920 - 1280
        Assert.Equal(100, y);
    }

    [Fact]
    public void ClampVisible_pins_a_window_larger_than_the_monitor_to_its_origin()
    {
        var one = new[] { new MonitorInfo(0, "Only", 0, 0, 1920, 1080) };
        Assert.Equal((0, 0), WindowPlacement.ClampVisible(3000, 3000, 2560, 1440, one));
    }

    [Fact]
    public void ClampVisible_with_no_monitors_returns_the_input()
    {
        Assert.Equal((10, 20), WindowPlacement.ClampVisible(10, 20, 800, 600, new List<MonitorInfo>()));
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WindowPlacementTests` → FAIL (type `WindowPlacement` / `MonitorInfo` not found).

- [ ] **Step 3: Implement** `WindowPlacement.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Windowing
{
    /// <summary>A connected monitor's identity and bounds in virtual-desktop (window) coordinates.
    /// Silk-free plain data so placement math is headless-testable; <see cref="AppWindow"/> builds
    /// these from Silk's monitor enumeration.</summary>
    public readonly record struct MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height)
    {
        /// <summary>X of the monitor centre.</summary>
        public int CenterX => X + Width / 2;
        /// <summary>Y of the monitor centre.</summary>
        public int CenterY => Y + Height / 2;
    }

    /// <summary>
    /// Pure window-placement policy: which monitor a window rect belongs to, where to centre a window
    /// on a monitor, and how to clamp a window back on-screen. No Silk / GPU access (mirrors
    /// <see cref="WindowModePlanner"/>), so it is fully headless-testable. <see cref="AppWindow"/>
    /// builds the <see cref="MonitorInfo"/> list from Silk and delegates all geometry here.
    /// </summary>
    public static class WindowPlacement
    {
        /// <summary>A window must keep at least this many points visible on both axes (or its whole
        /// extent, when smaller) to count as adequately on-screen.</summary>
        const int MinVisible = 48;

        /// <summary>The monitor a window rect belongs to: the one containing the window centre, else
        /// the one it overlaps most, else the nearest by centre distance. Returns -1 when
        /// <paramref name="monitors"/> is empty (headless / no display).</summary>
        public static int MonitorIndexFor(int wx, int wy, int ww, int wh, IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return -1;
            int cx = wx + ww / 2, cy = wy + wh / 2;

            int bestOverlap = -1; long bestOverlapArea = 0;
            int nearest = 0; long nearestDist = long.MaxValue;
            for (int i = 0; i < monitors.Count; i++)
            {
                MonitorInfo m = monitors[i];
                if (cx >= m.X && cx < m.X + m.Width && cy >= m.Y && cy < m.Y + m.Height)
                    return i; // centre containment wins outright

                long area = OverlapArea(wx, wy, ww, wh, m);
                if (area > bestOverlapArea) { bestOverlapArea = area; bestOverlap = i; }

                long dx = cx - m.CenterX, dy = cy - m.CenterY, dist = dx * dx + dy * dy;
                if (dist < nearestDist) { nearestDist = dist; nearest = i; }
            }
            return bestOverlapArea > 0 ? bestOverlap : nearest;
        }

        /// <summary>The window top-left that centres a <paramref name="ww"/> x <paramref name="wh"/>
        /// window on <paramref name="m"/>.</summary>
        public static (int X, int Y) CenterOn(MonitorInfo m, int ww, int wh)
            => (m.X + (m.Width - ww) / 2, m.Y + (m.Height - wh) / 2);

        /// <summary>Clamp a window rect back on-screen. When the window already keeps at least
        /// <see cref="MinVisible"/> points visible on both axes it is returned unchanged; otherwise it
        /// is relocated onto its best monitor (greatest overlap, else nearest centre) with the top-left
        /// clamped so the window sits inside that monitor, or at the monitor origin when the window is
        /// larger than the monitor. Position only (never resizes). Returns the input unchanged when
        /// <paramref name="monitors"/> is empty.</summary>
        public static (int X, int Y) ClampVisible(int wx, int wy, int ww, int wh, IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return (wx, wy);

            int target = MonitorIndexFor(wx, wy, ww, wh, monitors);
            MonitorInfo m = monitors[target < 0 ? 0 : target];

            int visW = OverlapLength(wx, ww, m.X, m.Width);
            int visH = OverlapLength(wy, wh, m.Y, m.Height);
            if (visW >= Math.Min(MinVisible, ww) && visH >= Math.Min(MinVisible, wh))
                return (wx, wy);

            return (ClampAxis(wx, ww, m.X, m.Width), ClampAxis(wy, wh, m.Y, m.Height));
        }

        static long OverlapArea(int wx, int wy, int ww, int wh, MonitorInfo m)
            => (long)OverlapLength(wx, ww, m.X, m.Width) * OverlapLength(wy, wh, m.Y, m.Height);

        static int OverlapLength(int aStart, int aLen, int bStart, int bLen)
        {
            int lo = Math.Max(aStart, bStart), hi = Math.Min(aStart + aLen, bStart + bLen);
            return Math.Max(0, hi - lo);
        }

        // Clamp a window start so a wLen-long window sits inside [mStart, mStart+mLen); pin to the
        // monitor origin (title bar visible) when the window is larger than the monitor.
        static int ClampAxis(int wStart, int wLen, int mStart, int mLen)
            => wLen >= mLen ? mStart : Math.Clamp(wStart, mStart, mStart + mLen - wLen);
    }
}
```

- [ ] **Step 4: Run to verify pass** — same filter → PASS.
- [ ] **Step 5: Commit** — `windowing: pure WindowPlacement (monitor pick / center / clamp-visible)`.

---

### Task 2: Extend `WindowModePlanner` to restore a windowed position

**Files:**
- Modify: `KhaozEngine.Windowing/WindowModePlanner.cs`
- Test: `KhaozEngine.Tests/Windowing/DisplaySettingsTests.cs` (append)

**Interfaces:**
- Consumes: existing `WindowModePlan`.
- Produces: `WindowModePlanner.Compute(..., bool restoreWindowedPos = false, int windowedX = 0, int windowedY = 0)` — Windowed sets `SetPosition = restoreWindowedPos` at (windowedX, windowedY).

- [ ] **Step 1: Write failing test** (append to `DisplaySettingsTests.cs`):

```csharp
[Fact]
public void Windowed_restores_the_known_windowed_position_when_asked()
{
    var plan = WindowModePlanner.Compute(WindowMode.Windowed,
        monitorX: 0, monitorY: 0, monitorWidth: 2560, monitorHeight: 1440,
        windowedWidth: 1280, windowedHeight: 720,
        restoreWindowedPos: true, windowedX: 200, windowedY: 150);

    Assert.True(plan.SetPosition);
    Assert.Equal(200, plan.X);
    Assert.Equal(150, plan.Y);
    Assert.True(plan.SetSize);
    Assert.Equal(1280, plan.Width);
}
```

(The existing `Windowed_keeps_a_resizable_border_and_the_windowed_size_without_moving` covers the default `SetPosition == false` path.)

- [ ] **Step 2: Run to verify fail** — `--filter DisplaySettingsTests` → FAIL (compile: no such overload).

- [ ] **Step 3: Implement** — change `Compute` signature + the Windowed (`_ =>`) branch, and update the Windowed doc bullet:

```csharp
public static WindowModePlan Compute(WindowMode mode,
    int monitorX, int monitorY, int monitorWidth, int monitorHeight,
    int windowedWidth, int windowedHeight,
    bool restoreWindowedPos = false, int windowedX = 0, int windowedY = 0)
{
    bool haveMonitor = monitorWidth > 0 && monitorHeight > 0;
    return mode switch
    {
        WindowMode.ExclusiveFullscreen => new WindowModePlan(
            WindowStateTarget.Fullscreen, WindowBorderTarget.Hidden,
            SetPosition: false, 0, 0, SetSize: false, 0, 0),

        WindowMode.BorderlessFullscreen when haveMonitor => new WindowModePlan(
            WindowStateTarget.Normal, WindowBorderTarget.Hidden,
            SetPosition: true, monitorX, monitorY, SetSize: true, monitorWidth, monitorHeight),

        WindowMode.BorderlessFullscreen => new WindowModePlan(
            WindowStateTarget.Normal, WindowBorderTarget.Hidden,
            SetPosition: false, 0, 0, SetSize: true, windowedWidth, windowedHeight),

        _ => new WindowModePlan(
            WindowStateTarget.Normal, WindowBorderTarget.Resizable,
            SetPosition: restoreWindowedPos, windowedX, windowedY, SetSize: true, windowedWidth, windowedHeight),
    };
}
```

Update the `<item>WindowMode.Windowed</item>` doc bullet to: "normal state + resizable border, sized to the windowed size; moved to the given windowed position when <paramref name="restoreWindowedPos"/> is set, otherwise the OS keeps it where it is."

- [ ] **Step 4: Run to verify pass** — `--filter DisplaySettingsTests` → PASS (new + all existing).
- [ ] **Step 5: Commit** — `windowing: WindowModePlanner restores windowed position when known`.

---

### Task 3: Extend `DisplaySettings` record + `IDisplaySettings` interface

**Files:**
- Modify: `KhaozEngine.Windowing/DisplaySettings.cs`
- Test: `KhaozEngine.Tests/Windowing/DisplaySettingsTests.cs` (append)

**Interfaces:**
- Produces: `DisplaySettings(..., int X = int.MinValue, int Y = int.MinValue)` with `const int PositionUnspecified = int.MinValue` and `bool HasPosition`; new `IDisplaySettings` members `int WindowX { get; }`, `int WindowY { get; }`, `void MoveTo(int,int)`, `IReadOnlyList<MonitorInfo> Monitors { get; }`, `int CurrentMonitorIndex { get; }`, `void MoveToMonitor(int)`, `void EnsureVisible()`.

- [ ] **Step 1: Write failing test** (append):

```csharp
[Fact]
public void DisplaySettings_carries_position_and_defaults_to_unspecified()
{
    var full = new DisplaySettings(PresentMode.Vsync, 60, WindowMode.Windowed, 1280, 720, 200, 150);
    Assert.True(full.HasPosition);
    Assert.Equal(200, full.X);
    Assert.Equal(150, (full with { Y = 150 }).Y);

    var noPos = new DisplaySettings(PresentMode.Vsync, 60, WindowMode.Windowed, 1280, 720);
    Assert.False(noPos.HasPosition);
    Assert.Equal(DisplaySettings.PositionUnspecified, noPos.X);
    // Existing 5-arg construction still equals (non-breaking).
    Assert.Equal(new DisplaySettings(PresentMode.Vsync, 60, WindowMode.Windowed, 1280, 720), noPos);
}
```

- [ ] **Step 2: Run to verify fail** — `--filter DisplaySettingsTests` → FAIL (no `X` / `HasPosition`).

- [ ] **Step 3: Implement** — add `using System.Collections.Generic;`, extend the record header, add `PositionUnspecified` + `HasPosition`, and add the interface members (full XML docs):

```csharp
public readonly record struct DisplaySettings(
    PresentMode PresentMode,
    int FrameCapHz,
    WindowMode WindowMode,
    int Width,
    int Height,
    int X = int.MinValue,
    int Y = int.MinValue)
{
    /// <summary>Sentinel for <see cref="X"/>/<see cref="Y"/> meaning "position unspecified" (leave the
    /// window where it is). <see cref="IDisplaySettings.CurrentDisplay"/> fills real coordinates.</summary>
    public const int PositionUnspecified = int.MinValue;

    /// <summary>True when both <see cref="X"/> and <see cref="Y"/> carry a real position (not
    /// <see cref="PositionUnspecified"/>), so <see cref="IDisplaySettings.ApplyDisplay"/> places the window.</summary>
    public bool HasPosition => X != PositionUnspecified && Y != PositionUnspecified;

    // ... existing RequiresFrameCapWarning unchanged ...
}
```

Interface members (insert after `WindowHeight` / `Resize`, before `CurrentDisplay`):

```csharp
/// <summary>Current window top-left X in virtual-desktop (screen) coordinates.</summary>
int WindowX { get; }
/// <summary>Current window top-left Y in virtual-desktop (screen) coordinates.</summary>
int WindowY { get; }
/// <summary>Move the window top-left to (<paramref name="x"/>, <paramref name="y"/>) in virtual-desktop
/// coordinates. Applied immediately when windowed; in a fullscreen mode it is remembered as the windowed
/// position to restore. Symmetric with <see cref="Resize"/>.</summary>
void MoveTo(int x, int y);

/// <summary>The connected monitors (index, name, bounds in window coordinates). Empty when no display
/// is available (headless).</summary>
IReadOnlyList<MonitorInfo> Monitors { get; }
/// <summary>Index into <see cref="Monitors"/> of the monitor currently holding the window (the one
/// containing its centre, else greatest overlap / nearest), or -1 when unknown.</summary>
int CurrentMonitorIndex { get; }
/// <summary>Place the window on the monitor at <paramref name="index"/> into <see cref="Monitors"/>:
/// centred when windowed, covering it when borderless. Out-of-range indices are ignored.</summary>
void MoveToMonitor(int index);
/// <summary>Clamp the window back on-screen (e.g. after restoring a saved position whose monitor is
/// gone). A no-op when the window is already adequately visible.</summary>
void EnsureVisible();
```

Also update the `DisplaySettings` and `IDisplaySettings` type-level `<summary>` prose to mention position/monitor round-trip.

- [ ] **Step 4: Run to verify pass** — `--filter DisplaySettingsTests` → PASS. Note: `AppWindow` will not compile yet (interface unimplemented) — that is Task 4; run only the Windowing test filter here, which does not build `AppWindow` members it lacks... if the test project references the interface, the solution build fails until Task 4. Acceptable: proceed straight to Task 4, then run the full suite. (Commit this task together with Task 4 if the tree does not build standalone.)
- [ ] **Step 5: Commit** (may be folded into Task 4's commit if the solution does not build without the `AppWindow` implementation) — `windowing: DisplaySettings position fields + IDisplaySettings placement members`.

---

### Task 4: Implement live placement on `AppWindow`

**Files:**
- Modify: `KhaozEngine.Windowing/AppWindow.cs`

**Interfaces:**
- Consumes: `WindowPlacement`, extended `WindowModePlanner.Compute`, `DisplaySettings.HasPosition`.
- Produces: `AppWindow` satisfies the extended `IDisplaySettings`.

- [ ] **Step 1: Add field** near `_windowedSize` (line ~98): `Vector2D<int>? _windowedPos;`

- [ ] **Step 2: Add live members** (near the existing `WindowWidth`/`Resize`/`CurrentDisplay`/`ApplyDisplay`), and extract `RealizePlan` from `ApplyWindowMode`:

```csharp
public int WindowX => _window.Position.X;
public int WindowY => _window.Position.Y;

public void MoveTo(int x, int y)
{
    _windowedPos = new Vector2D<int>(x, y);
    if (_windowMode == WindowMode.Windowed) _window.Position = _windowedPos.Value;
}

public IReadOnlyList<MonitorInfo> Monitors => EnumerateMonitors();

public int CurrentMonitorIndex
    => WindowPlacement.MonitorIndexFor(WindowX, WindowY, WindowWidth, WindowHeight, Monitors);

public void MoveToMonitor(int index)
{
    IReadOnlyList<MonitorInfo> monitors = Monitors;
    if (index < 0 || index >= monitors.Count) return;
    MonitorInfo m = monitors[index];
    var (x, y) = WindowPlacement.CenterOn(m, WindowWidth, WindowHeight);
    if (_windowMode == WindowMode.BorderlessFullscreen)
    {
        // Re-cover the chosen monitor directly from its bounds (no _window.Monitor pinning).
        RealizePlan(WindowModePlanner.Compute(WindowMode.BorderlessFullscreen,
            m.X, m.Y, m.Width, m.Height, _windowedSize.X, _windowedSize.Y));
        _windowedPos = new Vector2D<int>(x, y); // remembered for a later return to windowed
    }
    else MoveTo(x, y);
}

public void EnsureVisible()
{
    var (x, y) = WindowPlacement.ClampVisible(WindowX, WindowY, WindowWidth, WindowHeight, Monitors);
    MoveTo(x, y);
}

IReadOnlyList<MonitorInfo> EnumerateMonitors()
{
    var list = new List<MonitorInfo>();
    try
    {
        int i = 0;
        foreach (IMonitor m in Monitor.GetMonitors(_window))
        {
            var b = m.Bounds;
            list.Add(new MonitorInfo(i, m.Name ?? $"Monitor {i}", b.Origin.X, b.Origin.Y, b.Size.X, b.Size.Y));
            i++;
        }
    }
    catch { list.Clear(); }
    return list;
}
```

- [ ] **Step 3: Wire `ApplyWindowMode` + `RealizePlan`** (replace the body of `ApplyWindowMode`):

```csharp
void ApplyWindowMode(WindowMode mode)
{
    var (mx, my, mw, mh) = CurrentMonitorBounds();
    WindowModePlan plan = WindowModePlanner.Compute(mode, mx, my, mw, mh,
        _windowedSize.X, _windowedSize.Y,
        _windowedPos.HasValue, _windowedPos?.X ?? 0, _windowedPos?.Y ?? 0);

    if (mode == WindowMode.ExclusiveFullscreen)
        _window.Monitor ??= Monitor.GetMainMonitor(_window);

    RealizePlan(plan);
    _windowMode = mode;
}

void RealizePlan(WindowModePlan plan)
{
    _window.WindowBorder = plan.Border == WindowBorderTarget.Hidden ? WindowBorder.Hidden : WindowBorder.Resizable;
    _window.WindowState = plan.State == WindowStateTarget.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
    if (plan.SetSize) _window.Size = new Vector2D<int>(plan.Width, plan.Height);
    if (plan.SetPosition) _window.Position = new Vector2D<int>(plan.X, plan.Y);
}
```

- [ ] **Step 4: Update `CurrentDisplay` + `ApplyDisplay`**:

```csharp
public DisplaySettings CurrentDisplay =>
    new(_presentMode, _frameCapHz, _windowMode, WindowWidth, WindowHeight, WindowX, WindowY);

public void ApplyDisplay(in DisplaySettings settings)
{
    if (settings.WindowMode != _windowMode) ApplyWindowMode(settings.WindowMode);
    if (settings.Width > 0 && settings.Height > 0) Resize(settings.Width, settings.Height);
    if (settings.HasPosition)
    {
        var (x, y) = WindowPlacement.ClampVisible(settings.X, settings.Y, WindowWidth, WindowHeight, Monitors);
        MoveTo(x, y);
    }
    FrameCapHz = settings.FrameCapHz;
    PresentMode = settings.PresentMode;
}
```

- [ ] **Step 5: Build + run full suite** — `dotnet build KhaozEngine.Windowing/KhaozEngine.Windowing.csproj -c Debug` then `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. Expected: build OK, all tests PASS.
- [ ] **Step 6: Commit** — `windowing: AppWindow live window position + monitor placement` (fold in Task 3's changes if not already committed).

---

### Task 5: Docs sweep + release (9.25.0)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `KhaozEngine.Windowing/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/ROADMAP.md`, root `README.md`.

- [ ] **Step 1: Re-check for concurrent work** — `git fetch`; if `origin/main` advanced past this branch's base, merge it in first and re-run tests. Re-read `<KhaozEngineVersion>` + `git tag` and take the next FREE version (9.25.0 unless taken).
- [ ] **Step 2: Bump** `<KhaozEngineVersion>` in `Directory.Build.props` to `9.25.0`.
- [ ] **Step 3: CHANGELOG** newest-first entry, first sentence = one-line digest, no em-dash/semicolon. Cover: `IDisplaySettings` position (`WindowX`/`WindowY`/`MoveTo`) + monitor (`Monitors`/`CurrentMonitorIndex`/`MoveToMonitor`) + `EnsureVisible`; `DisplaySettings` X/Y round-trip; pure `WindowPlacement`/`MonitorInfo`; `WindowModePlanner` windowed-position restore. Note non-breaking.
- [ ] **Step 4: Guard-checked version strings** — set `docs/ROADMAP.md` "Current released version" and the root `README.md` `<PackageReference>` example to 9.25.0. Run `scripts/check-doc-versions.sh`.
- [ ] **Step 5: Package API doc** — `KhaozEngine.Windowing/README.md`: document the new placement API + `MonitorInfo` + `WindowPlacement` + `DisplaySettings` X/Y.
- [ ] **Step 6: Usage doc** — `docs/USING-KHAOZENGINE.md`: a persist/restore-placement example (read `CurrentDisplay`, save; on boot `ApplyDisplay` then the clamp is automatic, or call `EnsureVisible`).
- [ ] **Step 7: Root catalog** — check `README.md` Windowing summary; update only if the one-liner should mention placement (no package added/removed).
- [ ] **Step 8: Mechanical grep** — grep `WindowPlacement|MonitorInfo|WindowX|MoveToMonitor|EnsureVisible|CurrentMonitorIndex` across all `*.md` + `CLAUDE.md`; confirm every place that should mention them does and nothing stale remains.
- [ ] **Step 9: Pack** — `dotnet pack -c Release -o ./local-feed`.
- [ ] **Step 10: Commit + tag + push** — commit the bump+docs, `git tag v9.25.0`, per the finish ritual: merge branch to `main`, push `main` + `v9.25.0`.

## Self-Review

- **Spec coverage:** position get/set (Task 3/4), monitor enum+select (Task 1/3/4), snapshot round-trip (Task 3/4 `ApplyDisplay`), ensure-visible clamp (Task 1 `ClampVisible` + Task 4 `EnsureVisible`/`ApplyDisplay`), HiDPI single-coordinate-space (no conversion, documented), pure/headless (Task 1/2), release+docs (Task 5). All covered.
- **Placeholders:** none — every code step has full code.
- **Type consistency:** `MonitorInfo`, `WindowPlacement.{MonitorIndexFor,CenterOn,ClampVisible}`, `DisplaySettings.{X,Y,HasPosition,PositionUnspecified}`, `Compute(...,restoreWindowedPos,windowedX,windowedY)`, `RealizePlan`, `_windowedPos` used consistently across tasks.
