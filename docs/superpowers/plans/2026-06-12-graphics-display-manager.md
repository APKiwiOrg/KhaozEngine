# DisplayManager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a centralized display/window configuration component (`DisplayManager`) to KhaozEngine.Graphics so games stop configuring MonoGame's `GraphicsDeviceManager`/`GameWindow` bespoke, shipping as 3.5.0.

**Architecture:** Pure, immutable `DisplaySettings` (+ `DevicePreset` catalog + `WindowMode`) describe the wanted window declaratively and are fully headless-testable. A thin `DisplayManager` instance takes the live `GraphicsDeviceManager` + `GameWindow` ("takes what it needs, no statics", mirroring Camera2D/VirtualResolution) and applies settings, exposes runtime mutators, and enforces a min-size floor via a `ClientSizeChanged` clamp. The branching logic (`ResolveMode`, `ClampToMinimum`) is extracted to internal statics so it is unit-tested without a live device.

**Tech Stack:** C# net10.0, MonoGame.Framework.DesktopGL 3.8, xUnit. All work in the `feat/display-manager` worktree at `/Users/antonio/KhaozEngine-display-manager`.

---

### Task 1: WindowMode enum + internal static helpers (ResolveMode, ClampToMinimum)

These two pure helpers carry the only branching behaviour in the wrapper, so they get the
headless tests. They live as internal statics on `DisplayManager`; tests reach them via
`InternalsVisibleTo`. `DisplayManager` starts as a static-helpers-only shell in this task; its
instance members arrive in Task 4.

**Files:**
- Create: `KhaozEngine.Graphics/WindowMode.cs`
- Create: `KhaozEngine.Graphics/DisplayManager.cs`
- Modify: `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj` (add `InternalsVisibleTo`)
- Test: `KhaozEngine.Tests/DisplayManagerHelperTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/DisplayManagerHelperTests.cs`:

```csharp
using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class DisplayManagerHelperTests
{
    [Theory]
    [InlineData(WindowMode.Windowed, false, true)]
    [InlineData(WindowMode.BorderlessFullscreen, true, false)]
    [InlineData(WindowMode.ExclusiveFullscreen, true, true)]
    public void ResolveMode_MapsEachMode(WindowMode mode, bool isFullScreen, bool hardwareModeSwitch)
    {
        var (fs, hw) = DisplayManager.ResolveMode(mode);
        Assert.Equal(isFullScreen, fs);
        Assert.Equal(hardwareModeSwitch, hw);
    }

    [Fact]
    public void ClampToMinimum_BelowFloor_ClampsPerAxis()
    {
        Assert.Equal(new Point(300, 200), DisplayManager.ClampToMinimum(new Point(100, 50), 300, 200));
    }

    [Fact]
    public void ClampToMinimum_AtOrAboveFloor_PassesThrough()
    {
        Assert.Equal(new Point(400, 300), DisplayManager.ClampToMinimum(new Point(400, 300), 300, 200));
        Assert.Equal(new Point(500, 400), DisplayManager.ClampToMinimum(new Point(500, 400), 300, 200));
    }

    [Fact]
    public void ClampToMinimum_ZeroFloor_IsNoOp()
    {
        Assert.Equal(new Point(120, 80), DisplayManager.ClampToMinimum(new Point(120, 80), 0, 0));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter DisplayManagerHelperTests`
Expected: FAIL — build error, `WindowMode` and `DisplayManager` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Graphics/WindowMode.cs`:

```csharp
namespace KhaozEngine.Graphics;

/// <summary>
/// How the window presents. <see cref="BorderlessFullscreen"/> is windowed fullscreen
/// (no mode switch); <see cref="ExclusiveFullscreen"/> switches the hardware display mode.
/// </summary>
public enum WindowMode
{
    /// <summary>Bordered window at the configured backbuffer size.</summary>
    Windowed,
    /// <summary>Borderless window covering the display (no hardware mode switch).</summary>
    BorderlessFullscreen,
    /// <summary>Exclusive fullscreen with a hardware display-mode switch.</summary>
    ExclusiveFullscreen,
}
```

Create `KhaozEngine.Graphics/DisplayManager.cs` (static helpers only for now):

```csharp
using System;

namespace KhaozEngine.Graphics;

public sealed partial class DisplayManager
{
    /// <summary>Maps a <see cref="WindowMode"/> to MonoGame's
    /// (<c>IsFullScreen</c>, <c>HardwareModeSwitch</c>) pair.</summary>
    internal static (bool isFullScreen, bool hardwareModeSwitch) ResolveMode(WindowMode mode) => mode switch
    {
        WindowMode.Windowed             => (false, true),
        WindowMode.BorderlessFullscreen => (true,  false),
        WindowMode.ExclusiveFullscreen  => (true,  true),
        _                               => (false, true),
    };

    /// <summary>Clamps a requested client size up to the per-axis minimum (0 = no floor).</summary>
    internal static Microsoft.Xna.Framework.Point ClampToMinimum(
        Microsoft.Xna.Framework.Point requested, int minWidth, int minHeight) =>
        new(Math.Max(requested.X, minWidth), Math.Max(requested.Y, minHeight));
}
```

(`partial` so Task 4 adds the instance members in the same file without re-touching these.)

Modify `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj` — add an item group after the existing
one so the tests can see internals:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter DisplayManagerHelperTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
cd /Users/antonio/KhaozEngine-display-manager
git add KhaozEngine.Graphics/WindowMode.cs KhaozEngine.Graphics/DisplayManager.cs \
        KhaozEngine.Graphics/KhaozEngine.Graphics.csproj KhaozEngine.Tests/DisplayManagerHelperTests.cs
git commit -m "feat(Graphics): WindowMode + DisplayManager mode/min-size helpers"
```

---

### Task 2: DisplaySettings record

**Files:**
- Create: `KhaozEngine.Graphics/DisplaySettings.cs`
- Test: `KhaozEngine.Tests/DisplaySettingsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/DisplaySettingsTests.cs`:

```csharp
using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class DisplaySettingsTests
{
    [Fact]
    public void Landscape_SetsDimsAndLandscapeOrientations()
    {
        var s = DisplaySettings.Landscape(932, 430);
        Assert.Equal(932, s.Width);
        Assert.Equal(430, s.Height);
        Assert.Equal(DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
            s.SupportedOrientations);
    }

    [Fact]
    public void Portrait_SetsDimsAndPortraitOrientations()
    {
        var s = DisplaySettings.Portrait(430, 932);
        Assert.Equal(430, s.Width);
        Assert.Equal(932, s.Height);
        Assert.Equal(DisplayOrientation.Portrait | DisplayOrientation.PortraitDown,
            s.SupportedOrientations);
    }

    [Fact]
    public void Defaults_AreWindowedNonResizableNoFloor()
    {
        var s = DisplaySettings.Landscape(800, 480);
        Assert.Equal(WindowMode.Windowed, s.Mode);
        Assert.False(s.AllowUserResizing);
        Assert.Equal(0, s.MinWidth);
        Assert.Equal(0, s.MinHeight);
        Assert.Null(s.Title);
    }

    [Fact]
    public void With_ExpressionOverridesSingleProperty()
    {
        var s = DisplaySettings.Landscape(800, 480) with { Mode = WindowMode.BorderlessFullscreen };
        Assert.Equal(WindowMode.BorderlessFullscreen, s.Mode);
        Assert.Equal(800, s.Width); // unchanged
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter DisplaySettingsTests`
Expected: FAIL — `DisplaySettings` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Graphics/DisplaySettings.cs`:

```csharp
using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Declarative description of the wanted window: size, presentation mode, resize behaviour,
/// optional minimum size floor, supported orientations, and title. Immutable — build variants
/// with <c>with</c> expressions. Pure data; <see cref="DisplayManager"/> applies it to the device.
/// </summary>
public sealed record DisplaySettings
{
    /// <summary>Preferred backbuffer width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Preferred backbuffer height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Windowed / borderless / exclusive presentation.</summary>
    public WindowMode Mode { get; init; } = WindowMode.Windowed;

    /// <summary>Whether the user can resize the window (desktop).</summary>
    public bool AllowUserResizing { get; init; }

    /// <summary>Minimum client width enforced on resize; 0 = no floor.</summary>
    public int MinWidth { get; init; }

    /// <summary>Minimum client height enforced on resize; 0 = no floor.</summary>
    public int MinHeight { get; init; }

    /// <summary>Supported device orientations (mobile).</summary>
    public DisplayOrientation SupportedOrientations { get; init; } = DisplayOrientation.Default;

    /// <summary>Window title; null leaves the platform/default title untouched.</summary>
    public string? Title { get; init; }

    /// <summary>Landscape settings: the given size with landscape-left/right orientations.</summary>
    public static DisplaySettings Landscape(int width, int height) => new()
    {
        Width = width,
        Height = height,
        SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
    };

    /// <summary>Portrait settings: the given size with portrait/portrait-down orientations.</summary>
    public static DisplaySettings Portrait(int width, int height) => new()
    {
        Width = width,
        Height = height,
        SupportedOrientations = DisplayOrientation.Portrait | DisplayOrientation.PortraitDown,
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter DisplaySettingsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
cd /Users/antonio/KhaozEngine-display-manager
git add KhaozEngine.Graphics/DisplaySettings.cs KhaozEngine.Tests/DisplaySettingsTests.cs
git commit -m "feat(Graphics): DisplaySettings record with Landscape/Portrait factories"
```

---

### Task 3: DevicePreset + DevicePresets catalog

**Files:**
- Create: `KhaozEngine.Graphics/DevicePresets.cs`
- Test: `KhaozEngine.Tests/DevicePresetTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/DevicePresetTests.cs`:

```csharp
using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class DevicePresetTests
{
    [Fact]
    public void IPhone15ProMax_Landscape_Is932x430()
    {
        var s = DevicePresets.IPhone15ProMax.Landscape();
        Assert.Equal(932, s.Width);
        Assert.Equal(430, s.Height);
        Assert.Equal(DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
            s.SupportedOrientations);
    }

    [Fact]
    public void IPhone15ProMax_Portrait_Is430x932()
    {
        var s = DevicePresets.IPhone15ProMax.Portrait();
        Assert.Equal(430, s.Width);
        Assert.Equal(932, s.Height);
        Assert.Equal(DisplayOrientation.Portrait | DisplayOrientation.PortraitDown,
            s.SupportedOrientations);
    }

    [Fact]
    public void Landscape_SwapsPortraitDims()
    {
        var p = new DevicePreset("test", 390, 844);
        Assert.Equal(390, p.Portrait().Width);
        Assert.Equal(844, p.Portrait().Height);
        Assert.Equal(844, p.Landscape().Width);
        Assert.Equal(390, p.Landscape().Height);
    }

    [Fact]
    public void IPadPro129_Landscape_Is1366x1024()
    {
        var s = DevicePresets.IPadPro129.Landscape();
        Assert.Equal(1366, s.Width);
        Assert.Equal(1024, s.Height);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter DevicePresetTests`
Expected: FAIL — `DevicePreset` / `DevicePresets` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Graphics/DevicePresets.cs`:

```csharp
namespace KhaozEngine.Graphics;

/// <summary>
/// A named device size in portrait logical points. Call <see cref="Portrait"/> or
/// <see cref="Landscape"/> to get <see cref="DisplaySettings"/> with the matching orientations.
/// </summary>
public readonly record struct DevicePreset(string Name, int PortraitWidth, int PortraitHeight)
{
    /// <summary>Portrait settings at this preset's size.</summary>
    public DisplaySettings Portrait() => DisplaySettings.Portrait(PortraitWidth, PortraitHeight);

    /// <summary>Landscape settings: width/height swapped from the portrait size.</summary>
    public DisplaySettings Landscape() => DisplaySettings.Landscape(PortraitHeight, PortraitWidth);
}

/// <summary>
/// Common iOS device sizes in logical points (portrait). Convenience over raw width/height;
/// the plain <see cref="DisplaySettings.Landscape(int,int)"/> entry point is always available.
/// </summary>
public static class DevicePresets
{
    /// <summary>iPhone SE (2nd/3rd gen) — 375x667.</summary>
    public static readonly DevicePreset IPhoneSE = new("iPhone SE", 375, 667);

    /// <summary>iPhone 13 mini / 12 mini — 375x812.</summary>
    public static readonly DevicePreset IPhone13Mini = new("iPhone 13 mini", 375, 812);

    /// <summary>iPhone 15 / 14 / 13 — 390x844.</summary>
    public static readonly DevicePreset IPhone15 = new("iPhone 15", 390, 844);

    /// <summary>iPhone 15 Pro / 14 Pro — 393x852.</summary>
    public static readonly DevicePreset IPhone15Pro = new("iPhone 15 Pro", 393, 852);

    /// <summary>iPhone 15 Plus / 14 Plus / 13 Pro Max — 428x926.</summary>
    public static readonly DevicePreset IPhone15Plus = new("iPhone 15 Plus", 428, 926);

    /// <summary>iPhone 15 Pro Max / 14 Pro Max — 430x932 (landscape 932x430).</summary>
    public static readonly DevicePreset IPhone15ProMax = new("iPhone 15 Pro Max", 430, 932);

    /// <summary>iPad 10.2" — 810x1080.</summary>
    public static readonly DevicePreset IPad102 = new("iPad 10.2\"", 810, 1080);

    /// <summary>iPad Air / iPad Pro 11" — 834x1194.</summary>
    public static readonly DevicePreset IPadAir = new("iPad Air", 834, 1194);

    /// <summary>iPad Pro 12.9" — 1024x1366.</summary>
    public static readonly DevicePreset IPadPro129 = new("iPad Pro 12.9\"", 1024, 1366);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter DevicePresetTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
cd /Users/antonio/KhaozEngine-display-manager
git add KhaozEngine.Graphics/DevicePresets.cs KhaozEngine.Tests/DevicePresetTests.cs
git commit -m "feat(Graphics): DevicePreset + DevicePresets iOS size catalog"
```

---

### Task 4: DisplayManager instance (ctor, properties, mutators, min-size floor wiring)

This is the thin imperative layer that touches the live `GraphicsDeviceManager` + `GameWindow`.
It is NOT unit-tested headlessly (those types need a live device/Game, same as
`VirtualResolution.Initialize`); its only branching logic was already extracted and tested in
Task 1. Verification here is a clean build + the full suite still green.

**Files:**
- Modify: `KhaozEngine.Graphics/DisplayManager.cs` (add instance members to the existing `partial`)

- [ ] **Step 1: Add the instance members**

Append the instance half to the existing `partial class DisplayManager` in
`KhaozEngine.Graphics/DisplayManager.cs`. Replace the file's `using System;` line with the
full using block below and add the members. Final file:

```csharp
using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Centralizes MonoGame display/window configuration: backbuffer size, fullscreen mode,
/// resizing + minimum-size floor, orientations, and title. Takes the live
/// <see cref="GraphicsDeviceManager"/> and <see cref="GameWindow"/> ("takes what it needs,
/// no statics"). Construct in the <c>Game</c> constructor; the constructor sets preferences
/// (no <c>ApplyChanges</c>, the normal pre-device path) and runtime mutators apply immediately.
/// </summary>
public sealed partial class DisplayManager
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly GameWindow _window;
    private bool _floorWired;
    private bool _inResize;

    /// <summary>Current applied settings.</summary>
    public DisplaySettings Settings { get; private set; }

    /// <summary>Current preferred backbuffer width.</summary>
    public int Width => _graphics.PreferredBackBufferWidth;

    /// <summary>Current preferred backbuffer height.</summary>
    public int Height => _graphics.PreferredBackBufferHeight;

    /// <summary>Current preferred backbuffer size as a point.</summary>
    public Point Size => new(Width, Height);

    /// <summary>True when the current mode is any fullscreen mode.</summary>
    public bool IsFullscreen => Settings.Mode != WindowMode.Windowed;

    /// <summary>Wraps the device + window and applies the initial settings (preferences only).</summary>
    public DisplayManager(GraphicsDeviceManager graphics, GameWindow window, DisplaySettings settings)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ApplyInternal(settings, applyChanges: false);
    }

    /// <summary>Applies new settings and commits them to the device (<c>ApplyChanges</c>).</summary>
    public void Apply(DisplaySettings settings) =>
        ApplyInternal(settings ?? throw new ArgumentNullException(nameof(settings)), applyChanges: true);

    /// <summary>Sets the backbuffer resolution at runtime.</summary>
    public void SetResolution(int width, int height) =>
        Apply(Settings with { Width = width, Height = height });

    /// <summary>Sets the presentation mode at runtime.</summary>
    public void SetMode(WindowMode mode) => Apply(Settings with { Mode = mode });

    /// <summary>Toggles between windowed and borderless fullscreen.</summary>
    public void ToggleFullscreen() =>
        SetMode(IsFullscreen ? WindowMode.Windowed : WindowMode.BorderlessFullscreen);

    /// <summary>Sets resizing and the optional minimum-size floor (0 = no floor).</summary>
    public void SetResizable(bool allow, int minWidth = 0, int minHeight = 0) =>
        Apply(Settings with { AllowUserResizing = allow, MinWidth = minWidth, MinHeight = minHeight });

    private void ApplyInternal(DisplaySettings settings, bool applyChanges)
    {
        Settings = settings;

        _graphics.PreferredBackBufferWidth = settings.Width;
        _graphics.PreferredBackBufferHeight = settings.Height;

        var (isFullScreen, hardwareModeSwitch) = ResolveMode(settings.Mode);
        _graphics.IsFullScreen = isFullScreen;
        _graphics.HardwareModeSwitch = hardwareModeSwitch;
        _graphics.SupportedOrientations = settings.SupportedOrientations;

        _window.AllowUserResizing = settings.AllowUserResizing;
        if (settings.Title is not null) _window.Title = settings.Title;

        if (!_floorWired)
        {
            _window.ClientSizeChanged += OnClientSizeChanged;
            _floorWired = true;
        }

        if (applyChanges) _graphics.ApplyChanges();
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_inResize) return;
        if (!Settings.AllowUserResizing) return;
        if (Settings.MinWidth <= 0 && Settings.MinHeight <= 0) return;

        Rectangle bounds = _window.ClientBounds;
        Point clamped = ClampToMinimum(new Point(bounds.Width, bounds.Height),
            Settings.MinWidth, Settings.MinHeight);
        if (clamped.X == bounds.Width && clamped.Y == bounds.Height) return;

        _inResize = true;
        _graphics.PreferredBackBufferWidth = clamped.X;
        _graphics.PreferredBackBufferHeight = clamped.Y;
        _graphics.ApplyChanges();
        _inResize = false;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build KhaozEngine.Graphics/KhaozEngine.Graphics.csproj -c Debug`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full test suite to verify nothing regressed**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS — all pre-existing tests plus the 13 new ones from Tasks 1–3 green.

- [ ] **Step 4: Commit**

```bash
cd /Users/antonio/KhaozEngine-display-manager
git add KhaozEngine.Graphics/DisplayManager.cs
git commit -m "feat(Graphics): DisplayManager applies settings + enforces min-size floor"
```

---

### Task 5: Docs + version bump + package Description (3.5.0)

**Files:**
- Modify: `Directory.Build.props` (`<Version>3.4.1</Version>` → `3.5.0`)
- Modify: `CHANGELOG.md` (new newest-first `## KhaozEngine 3.5.0` entry)
- Modify: `docs/USING-KHAOZENGINE.md` (new DisplayManager section)
- Modify: `docs/CONSUMERS.md` (engine-version line → 3.5.0)
- Modify: `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj` (`<Description>` mentions display/window)

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change:
```xml
    <Version>3.4.1</Version>
```
to:
```xml
    <Version>3.5.0</Version>
```

- [ ] **Step 2: Add the CHANGELOG entry**

Insert at the top of `CHANGELOG.md` (read the file first to match its exact heading style; the
newest entry goes above the current newest). Entry content:

```markdown
## KhaozEngine 3.5.0

### KhaozEngine.Graphics — DisplayManager (display/window configuration)

New `DisplayManager` centralizes MonoGame `GraphicsDeviceManager` + `GameWindow` setup so games
stop configuring the device bespoke.

- `DisplaySettings` (immutable record): `Width`/`Height`, `Mode` (`WindowMode.Windowed` /
  `BorderlessFullscreen` / `ExclusiveFullscreen`), `AllowUserResizing`, `MinWidth`/`MinHeight`
  floor, `SupportedOrientations`, `Title`. Factories `DisplaySettings.Landscape(w, h)` and
  `Portrait(w, h)`. Pure and headless-testable; build variants with `with`.
- `DevicePresets` catalog of common iOS logical-point sizes (iPhone SE … 15 Pro Max, iPad …
  Pro 12.9") via `DevicePreset.Portrait()` / `.Landscape()`.
- `DisplayManager(graphics, window, settings)` applies settings to the live device and exposes
  runtime mutators `Apply`, `SetResolution`, `SetMode`, `ToggleFullscreen`, `SetResizable`, plus
  `Width`/`Height`/`Size`/`IsFullscreen`. Enforces the min-size floor by clamping on
  `ClientSizeChanged`. Composes with `VirtualResolution`, which still reads the device for scaling.

One-liner for an iPhone 15 Pro Max landscape window (932x430):

    display = new DisplayManager(graphicsDeviceManager, Window, DisplaySettings.Landscape(932, 430));
```

- [ ] **Step 3: Add the USING-KHAOZENGINE.md section**

Read `docs/USING-KHAOZENGINE.md` to find the Graphics/Camera2D and VirtualResolution sections and
match their heading depth + prose style. Add a new section near them:

```markdown
## DisplayManager (KhaozEngine.Graphics)

`DisplayManager` centralizes window/display configuration so a game does not poke
`GraphicsDeviceManager`/`GameWindow` directly. Construct it in your `Game` constructor (where
`graphicsDeviceManager` and `Window` already exist) with a declarative `DisplaySettings`:

    // 932x430 landscape (iPhone 14/15 Pro Max logical points)
    display = new DisplayManager(graphicsDeviceManager, Window, DisplaySettings.Landscape(932, 430));

    // Or via the device-size catalog (same 932x430):
    display = new DisplayManager(graphicsDeviceManager, Window, DevicePresets.IPhone15ProMax.Landscape());

`DisplaySettings` is an immutable record — `Width`, `Height`, `Mode` (`WindowMode.Windowed` /
`BorderlessFullscreen` / `ExclusiveFullscreen`), `AllowUserResizing`, `MinWidth`/`MinHeight`
floor, `SupportedOrientations`, `Title`. Build variants with `with`, or use the
`DisplaySettings.Landscape(w, h)` / `Portrait(w, h)` factories.

Runtime changes:

    display.SetResolution(1280, 720);
    display.ToggleFullscreen();
    display.SetResizable(true, minWidth: 640, minHeight: 360); // floor enforced on resize

`Width`/`Height`/`Size` report the current backbuffer size (use `display.Size` instead of reading
`PreferredBackBufferWidth/Height`). `VirtualResolution` is unchanged: `DisplayManager` owns the
device config, `VirtualResolution` reads it for its coordinate scaling.
```

- [ ] **Step 4: Update CONSUMERS.md engine-version line**

Read `docs/CONSUMERS.md`, find the current engine-version line (states 3.4.1) and update it to
3.5.0, matching the existing wording. Do not change consumer pins (no consumer has adopted yet).

- [ ] **Step 5: Update the Graphics package Description**

In `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj`, extend `<Description>` so it mentions the
new component, e.g. append:

```
 Plus DisplayManager: declarative window/display config (size, fullscreen mode, resizing + min-size floor, orientations) over GraphicsDeviceManager/GameWindow, with a DevicePresets size catalog.
```

- [ ] **Step 6: Verify the suite is still green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (full suite).

- [ ] **Step 7: Commit (version + changelog together, per the release rule)**

```bash
cd /Users/antonio/KhaozEngine-display-manager
git add Directory.Build.props CHANGELOG.md docs/USING-KHAOZENGINE.md docs/CONSUMERS.md \
        KhaozEngine.Graphics/KhaozEngine.Graphics.csproj
git commit -m "release(3.5.0): DisplayManager docs, changelog, version bump"
```

---

### Task 6: Pack to local-feed (cumulative)

**Files:** none (build output only).

- [ ] **Step 1: Pack Release to the canonical local-feed**

Pack all packages at the new version into the canonical feed (NOT the worktree's local-feed;
consumers restore from `~/KhaozEngine/local-feed`). Cumulative — do not delete old versions.

Run:
```bash
cd /Users/antonio/KhaozEngine-display-manager
dotnet pack -c Release -o /Users/antonio/KhaozEngine/local-feed
```
Expected: Build + pack succeeded; `KhaozEngine.Graphics.3.5.0.nupkg` (and the other
`*.3.5.0.nupkg`) written to `/Users/antonio/KhaozEngine/local-feed`.

- [ ] **Step 2: Verify the new package landed**

Run: `ls /Users/antonio/KhaozEngine/local-feed/KhaozEngine.Graphics.3.5.0.nupkg`
Expected: the file path prints (exists).

---

### Tag + push

Tagging `v3.5.0` and pushing `main` + the tag happen at branch-finish (merge back to `main`
first), per the finishing-a-development-branch flow and CLAUDE.md. Not a plan task — handled when
the work is integrated.

---

## Self-Review

**Spec coverage:**
- Placement in KhaozEngine.Graphics + InternalsVisibleTo — Task 1. ✓
- `WindowMode` + `ResolveMode` — Task 1. ✓
- `ClampToMinimum` — Task 1. ✓
- `DisplaySettings` record + Landscape/Portrait — Task 2. ✓
- `DevicePreset` + broad `DevicePresets` catalog (incl. IPhone15ProMax → 932x430) — Task 3. ✓
- `DisplayManager` ctor/properties/mutators/min-size floor wiring — Task 4. ✓
- VirtualResolution interaction (unchanged) — documented in Tasks 4/5. ✓
- 932x430 one-liner — Tasks 5 (docs) + covered by Task 3 test. ✓
- Headless tests (7 spec items) — Tasks 1–3 cover ResolveMode (3), ClampToMinimum (3 cases),
  Landscape/Portrait orientations + dims, preset 932x430, preset swap, second preset dims. ✓
- Release ritual (version, changelog same commit, USING doc, CONSUMERS, Description, pack, tag) —
  Tasks 5–6 + tag/push note. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code. Doc-edit steps that say
"read the file to match style" still specify the exact content to insert. ✓

**Type consistency:** `DisplayManager.ResolveMode`/`ClampToMinimum` (internal static, Task 1) are
referenced identically in tests (Task 1) and `DisplayManager` instance code (Task 4).
`DisplaySettings` properties (`Width`, `Height`, `Mode`, `AllowUserResizing`, `MinWidth`,
`MinHeight`, `SupportedOrientations`, `Title`) match across Tasks 2/4. `DevicePreset.Portrait()`/
`.Landscape()` consistent across Tasks 3/5. `partial class DisplayManager` declared in Task 1 and
extended in Task 4. ✓
