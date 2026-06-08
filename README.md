# KhaozEngine

Shared, game-agnostic **input + screen-stack** foundation for MonoGame games. One implementation, used by three games (Hardpoint, Nullwake, SpaceGame), so a fix written once — the click-through fix in particular — propagates to all of them.

KhaozEngine is **not** a full engine. It owns exactly four concerns, each a separate NuGet package, and nothing game-specific:

| Package | What it gives you | Depends on |
|---|---|---|
| **KhaozEngine.Input** | A unified pointer (mouse+touch), edge detection, the `IsTapIn` press-origin invariant, per-frame region blocking, drag/scroll/pinch, keyboard + gamepad + menu-navigation, and a coordinate-transform seam — all behind a testable `IRawInput` seam. | MonoGame |
| **KhaozEngine.Screens** | A screen stack routed top-to-bottom with `receivesInput` / `PassUpdateThrough` / `AlwaysReceivesInput`, two consumption policies, and screen transitions. | KhaozEngine.Input |
| **KhaozEngine.UI** | A widget library (Button, Slider, Dropdown, ScrollablePanel, TextInput, Toggle, Tooltip, …), a `PrimitiveRenderer`, and a `TextInputHandler`. | KhaozEngine.Input |
| **KhaozEngine.Ecs** | A minimal `World` / `Entity` / `ISystem` ECS. Independent of the others. | MonoGame |

Target framework `net10.0`, consumable from the `net10.0-android` / `net10.0-ios` heads. Built against MonoGame.Framework.DesktopGL 3.8.

## Why it exists

The input + screen code was built clean inside Hardpoint, modelled on Nullwake's mature, shipping system, with one improvement: the raw hardware read sits behind an interface so the whole input + routing surface is **unit-testable without a device**. Once there was a second and third real consumer, it was extracted here as a Rule-of-Three extraction. The headline payoff: the **click-through fix** (a tap only registers when press-origin and release are in the same target, and overlays reserve their footprint so clicks never leak to the layer beneath) now lives in one place.

## The one rule that matters most

> **`MonoGameRawInput` is the only code in the entire stack that touches `Mouse`/`Keyboard`/`GamePad`/`TouchPanel`.** Everything above it reads an immutable `RawInputState` snapshot through the `IRawInput` seam. Games must not poll the MonoGame input statics directly — doing so re-introduces the untestable, click-through-leaking pattern this library exists to kill.

Full consumer contract: [`docs/USING-KHAOZENGINE.md`](docs/USING-KHAOZENGINE.md). Read it before wiring a game in.

## Quickstart (the canonical game-loop wiring)

```csharp
using KhaozEngine.Input;
using KhaozEngine.Screens;

// LoadContent: create once.
_rawInput = new MonoGameRawInput(Window);                 // the ONLY statics-toucher
_input    = new InputManager(isMobile: IsMobile);          // pass a coordinate transform if you scale
_screens  = new ScreenManager(_input) { ExitRequested = Exit };
_screens.GraphicsDevice = GraphicsDevice;
_screens.SpriteBatch    = _spriteBatch;
_screens.Add(new MyFirstScreen());

// Update: input first, then screens.
_input.Update(_rawInput.Read(), IsActive);                 // IsActive suppresses ghost taps on refocus
_screens.Update(gameTime);

// Draw: bottom-to-top.
_screens.Draw(gameTime, _spriteBatch);
```

A screen:

```csharp
public sealed class MyFirstScreen : GameScreen
{
    private static readonly Rectangle Button = new(300, 200, 200, 40);

    public override bool Update(GameTime gameTime, bool receivesInput)
    {
        if (!receivesInput) return false;
        if (Manager.Input.IsTapIn(Button))      // press-origin invariant = click-through-safe
            Manager.RequestExit();
        return true;                            // "I consumed input this frame"
    }

    public override void Draw(GameTime gameTime, SpriteBatch spriteBatch) { /* ... */ }
}
```

## Consuming the packages

Published to a private GitHub Packages feed on tagged releases, and packed to a local file-feed for day-to-day development.

```xml
<!-- nuget.config (additive) -->
<add key="khaozengine-local" value="/Users/antonio/KhaozEngine/local-feed" />
<!-- or the GitHub Packages feed: https://nuget.pkg.github.com/APKiwi/index.json -->
```
```xml
<PackageReference Include="KhaozEngine.Input"   Version="0.1.0" />
<PackageReference Include="KhaozEngine.Screens" Version="0.1.0" />
<PackageReference Include="KhaozEngine.UI"      Version="0.1.0" />
<PackageReference Include="KhaozEngine.Ecs"     Version="0.1.0" />
```

**Versioning is SemVer.** Each game pins a version and adopts fixes by bumping it — so you can keep one game on an old version while you migrate another. Don't fork the packages; if a game needs an API that isn't there, add it here and bump the version.

## Testability standard

Every input and routing path is covered by `KhaozEngine.Tests` (xUnit), headless, by constructing `RawInputState` snapshots frame-by-frame and feeding them to `InputManager.Update`. New behaviour added to the library ships with a headless test. This is the standard, not a nicety — it's the reason the raw read is behind `IRawInput`.

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
```

## Repo layout

```
KhaozEngine.Input/      KhaozEngine.Screens/      KhaozEngine.UI/      KhaozEngine.Ecs/
KhaozEngine.Tests/      docs/USING-KHAOZENGINE.md
Directory.Build.props (shared version)   nuget.config   .github/workflows/ci.yml
```

CI builds, tests, packs, and on a `v*` tag publishes to GitHub Packages.

## Consumers

| Game | Uses | Status |
|---|---|---|
| **Hardpoint** | Input, Screens, Ecs | Migrated (`0.1.0`). Only `GameplayScreen`/`PauseScreen`/`HardpointGame` are game-side. |
| **Nullwake** | Input, Screens, UI | Migrating. Source of the widgets, `VirtualResolution`, transitions, and the click-through fix. |
| **SpaceGame** | Input, Screens, UI | Migrating. Full input-model migration off its `InputState`. |
