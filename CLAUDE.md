# KhaozEngine

Shared, game-agnostic input + screen-stack + UI + ECS for MonoGame games
(Hardpoint, Nullwake, SpaceGame). See README.md and docs/USING-KHAOZENGINE.md.

## Rules
- `MonoGameRawInput` is the ONLY class that may touch Mouse/Keyboard/GamePad/TouchPanel
  statics. Everything else reads `RawInputState` via `IRawInput` — keeps input headless-testable.
- New behaviour ships with a headless test in `KhaozEngine.Tests` (build `RawInputState`
  frame-by-frame; `GameTime` is `new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt))`).
- Hit-test via `InputManager` bounds helpers (`IsTapIn`, etc.), never raw position + button.

## Build / test / release
- `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- `dotnet pack -c Release -o ./local-feed` — cumulative; don't `rm` old versions (consumers pin).
- Version is shared in `Directory.Build.props`; tag `v*` to publish to GitHub Packages via CI.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- net10.0, MonoGame.Framework.DesktopGL 3.8, xUnit.
