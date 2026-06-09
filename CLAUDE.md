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
- `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` — every new behaviour ships with a headless test.
- **Always update `CHANGELOG.md` on every version bump.** Add a newest-first entry describing the
  public API / behaviour change in the SAME commit as the `Directory.Build.props` `<Version>` bump.
  Never bump the version (or tag a release) without a matching changelog entry.
- Release ritual, in order: bump `<Version>` in `Directory.Build.props` → add the `CHANGELOG.md`
  entry → update the engine-version line in `docs/CONSUMERS.md` → `dotnet pack -c Release -o ./local-feed`
  (cumulative; don't `rm` old versions, consumers pin) → commit → `git tag vX.Y.Z` → push `main` + the
  tag (CI publishes to GitHub Packages on `v*`).
- **`docs/CONSUMERS.md` tracks which game pins which package version.** Update its version matrix
  whenever a consumer bumps a `KhaozEngine.*` `<PackageReference>`, and the engine-version line on
  every release. Refresh snippet is at the bottom of that file.
- SemVer: additive = minor, fixes = patch, breaking = major.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- net10.0, MonoGame.Framework.DesktopGL 3.8, xUnit.
