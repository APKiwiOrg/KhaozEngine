# DisplayManager - display/window configuration for KhaozEngine.Graphics

> **SUPERSEDED (shipped differently in 9.24.0).** This 2026-06-12 spec targeted the now-deleted MonoGame line
> (`KhaozEngine.Graphics` / `GraphicsDeviceManager` / `GameWindow`) and was never built as written. Its intent -
> runtime window/display configuration as a centralized engine feature - shipped in 9.24.0 on the MonoGame-free
> stack: the `WindowMode { Windowed, BorderlessFullscreen, ExclusiveFullscreen }` enum and `DisplaySettings` record
> carried over (see this spec's naming), but the surface is the `IDisplaySettings` interface on `AppWindow` /
> `GameApp.Display` (present mode, frame cap, window mode, resolution) rather than a `DisplayManager` over MonoGame.
> Kept as a historical design record. See CHANGELOG 9.24.0 and `docs/USING-KHAOZENGINE.md`.

**Date:** 2026-06-12
**Target version:** KhaozEngine 3.5.0 (additive, no breaking changes)
**Package:** KhaozEngine.Graphics

## Problem

Games configure MonoGame's `GraphicsDeviceManager` + `GameWindow` bespoke. Hardpoint sets
its window size inline in `HardpointGame`'s constructor:

```csharp
graphicsDeviceManager.PreferredBackBufferWidth  = 800;
graphicsDeviceManager.PreferredBackBufferHeight = 480;
graphicsDeviceManager.SupportedOrientations =
    DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight;
```

and later reads those preferred values back to build a `Point`. Window/display setup is a
default-centralize domain (rendering/graphics) and all three games (Hardpoint, Nullwake,
SpaceGame) want it. It belongs in the engine, not copied per game.

Hardpoint's concrete first need: default window **932 x 430** (iPhone 14/15 Pro Max logical
points, landscape, 19.5:9). "Set the default window to 932x430 landscape" must be a one-liner.

## Placement

KhaozEngine.Graphics. It already references `MonoGame.Framework.DesktopGL`, which provides
both `GraphicsDeviceManager` and `GameWindow`, so this adds no new dependency and pulls a
MonoGame dep into no package that didn't already have one. Camera2D's headless math stays as
is; the display wrapper is a thin imperative layer beside it.

## Design

Split into **pure data/logic that is headless-testable** and a **thin imperative apply layer**,
mirroring the Camera2D / VirtualResolution "takes what it needs, no hidden statics" style.

### 1. `WindowMode` enum (pure)

```csharp
public enum WindowMode { Windowed, BorderlessFullscreen, ExclusiveFullscreen }
```

Named `WindowMode` (not `DisplayMode`) to avoid colliding with
`Microsoft.Xna.Framework.Graphics.DisplayMode`.

### 2. `DisplaySettings` record (pure, immutable)

The declarative description a game hands in. Init-only properties; `with` expressions cover
runtime tweaks.

```csharp
public sealed record DisplaySettings
{
    public int Width { get; init; }
    public int Height { get; init; }
    public WindowMode Mode { get; init; } = WindowMode.Windowed;
    public bool AllowUserResizing { get; init; }
    public int MinWidth { get; init; }   // 0 = no floor
    public int MinHeight { get; init; }  // 0 = no floor
    public DisplayOrientation SupportedOrientations { get; init; } = DisplayOrientation.Default;
    public string? Title { get; init; }

    public static DisplaySettings Landscape(int width, int height); // landscape orientations
    public static DisplaySettings Portrait(int width, int height);  // portrait orientations
}
```

- `Landscape(w, h)` → `Width = w, Height = h, SupportedOrientations = LandscapeLeft | LandscapeRight`.
- `Portrait(w, h)`  → `Width = w, Height = h, SupportedOrientations = Portrait | PortraitDown`.

`DisplayOrientation` is the MonoGame enum (`Microsoft.Xna.Framework`); using it directly keeps
the API idiomatic and the package already depends on MonoGame.

### 3. `DevicePreset` + `DevicePresets` (pure, broad catalog)

```csharp
public readonly record struct DevicePreset(string Name, int PortraitWidth, int PortraitHeight)
{
    public DisplaySettings Portrait();   // PortraitWidth x PortraitHeight, portrait orientations
    public DisplaySettings Landscape();  // swapped dims, landscape orientations
}
```

Catalog of common iOS logical-point sizes (portrait orientation, width x height):

| Preset            | Portrait | Landscape |
|-------------------|----------|-----------|
| `IPhoneSE`        | 375x667  | 667x375   |
| `IPhone13Mini`    | 375x812  | 812x375   |
| `IPhone15`        | 390x844  | 844x390   |
| `IPhone15Pro`     | 393x852  | 852x393   |
| `IPhone15Plus`    | 428x926  | 926x428   |
| `IPhone15ProMax`  | 430x932  | **932x430** |
| `IPad102`         | 810x1080 | 1080x810  |
| `IPadAir`         | 834x1194 | 1194x834  |
| `IPadPro129`      | 1024x1366| 1366x1024 |

`IPhone15ProMax.Landscape()` yields exactly 932x430 - Hardpoint's concrete need maps to the
catalog. (Same logical points as iPhone 14 Pro Max; named for the latest.)

### 4. `DisplayManager` (thin imperative wrapper)

```csharp
public sealed class DisplayManager
{
    public DisplayManager(GraphicsDeviceManager graphics, GameWindow window, DisplaySettings settings);

    public DisplaySettings Settings { get; }      // current settings
    public int   Width        { get; }            // graphics.PreferredBackBufferWidth
    public int   Height       { get; }            // graphics.PreferredBackBufferHeight
    public Point Size         { get; }            // (Width, Height)
    public bool  IsFullscreen { get; }            // Mode != Windowed

    public void Apply(DisplaySettings settings);                       // set prefs + ApplyChanges + rewire floor
    public void SetResolution(int width, int height);
    public void SetMode(WindowMode mode);
    public void ToggleFullscreen();                                    // Windowed <-> BorderlessFullscreen
    public void SetResizable(bool allow, int minWidth = 0, int minHeight = 0);
}
```

Construction takes the live `GraphicsDeviceManager` and `GameWindow` (both exist in the `Game`
constructor) plus the initial `DisplaySettings`, and applies the preferences:

- `PreferredBackBufferWidth/Height` from `Width/Height`.
- `IsFullScreen` + `HardwareModeSwitch` from `Mode` (see `ResolveMode` below).
- `SupportedOrientations`, `window.AllowUserResizing`, `window.Title` (when set).
- Wires the min-size floor handler on `window.ClientSizeChanged`.

The constructor sets preferences only (no `ApplyChanges` - pre-device that is the normal MonoGame
path). Runtime mutators set the preference then call `ApplyChanges()`. `ToggleFullscreen` uses
`graphics.ToggleFullScreen()`.

**Min-size floor.** MonoGame has no portable native minimum-window-size API. When
`AllowUserResizing` is true and a floor is set, the `ClientSizeChanged` handler reads the current
`window.ClientBounds`, clamps via `ClampToMinimum`, and if the clamp changed anything sets the
preferred backbuffer to the clamped size and calls `ApplyChanges()`. A reentrancy guard (bool flag)
prevents the `ApplyChanges`-triggered `ClientSizeChanged` from recursing.

### 5. Pure helpers (extracted so behaviour is headless-testable)

```csharp
internal static Point ClampToMinimum(Point requested, int minWidth, int minHeight)
    => new(Math.Max(requested.X, minWidth), Math.Max(requested.Y, minHeight));

internal static (bool isFullScreen, bool hardwareModeSwitch) ResolveMode(WindowMode mode) => mode switch
{
    WindowMode.Windowed             => (false, true),
    WindowMode.BorderlessFullscreen => (true,  false), // borderless windowed fullscreen
    WindowMode.ExclusiveFullscreen  => (true,  true),
    _                               => (false, true),
};
```

These carry the real branching behaviour. The ~5-line event-subscription glue in `DisplayManager`
is not unit-tested (needs a live `GraphicsDevice`), consistent with `VirtualResolution.Initialize`.

## Interaction with VirtualResolution

Unchanged. `DisplayManager` owns device configuration; `VirtualResolution` keeps reading
`GraphicsDeviceManager.GraphicsDevice.PresentationParameters` for its scaling. The display
manager just makes the backbuffer size it reads explicit and centralized.

## Hardpoint adoption (illustrative - separate from this engine task)

In `HardpointGame`'s constructor:

```csharp
display = new DisplayManager(graphicsDeviceManager, Window, DevicePresets.IPhone15ProMax.Landscape());
// or the plain form:
display = new DisplayManager(graphicsDeviceManager, Window, DisplaySettings.Landscape(932, 430));
```

`display.Size` then replaces the manual
`new Point(graphicsDeviceManager.PreferredBackBufferWidth, graphicsDeviceManager.PreferredBackBufferHeight)`.

## Tests (headless, xUnit, in KhaozEngine.Tests)

1. `DisplaySettings.Landscape(932, 430)` → Width 932, Height 430, orientations LandscapeLeft|LandscapeRight.
2. `DisplaySettings.Portrait(430, 932)` → portrait orientations, dims preserved.
3. `DevicePresets.IPhone15ProMax.Landscape()` → 932x430 (proves Hardpoint's need maps to catalog).
4. Two more preset dims (e.g. `IPhone15.Portrait()` 390x844, `IPadPro129.Landscape()` 1366x1024).
5. `DevicePreset.Landscape()` swaps portrait dims; `Portrait()` keeps them.
6. `ClampToMinimum`: below floor on each axis clamps up; at/above floor passes through; 0 floor is a no-op.
7. `ResolveMode` for all three `WindowMode` values.

## Release ritual (per CLAUDE.md)

1. Bump `<Version>` in `Directory.Build.props` to `3.5.0`.
2. Add a newest-first `## KhaozEngine 3.5.0` entry to `CHANGELOG.md` (same commit as the bump).
3. New section in `docs/USING-KHAOZENGINE.md` alongside Graphics/Camera2D and VirtualResolution,
   with the 932x430 one-liner example.
4. Update the engine-version line in `docs/CONSUMERS.md`.
5. Update the Graphics package `<Description>` to mention display/window management.
6. `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` green.
7. `dotnet pack -c Release -o <canonical ~/KhaozEngine/local-feed>` (cumulative; don't remove old versions).
8. Commit, `git tag v3.5.0`, push `main` + tag (CI publishes to GitHub Packages on `v*`).

Work is isolated in the `feat/display-manager` worktree per the concurrent-dev rule.
