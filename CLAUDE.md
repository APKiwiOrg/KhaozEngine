# KhaozEngine

Shared, game-agnostic input + screen-stack + UI + ECS for MonoGame games
(Hardpoint, Nullwake, SpaceGame). See README.md and docs/USING-KHAOZENGINE.md.

## Before starting ANY engine work (concurrent-dev rule)
There is a lot of parallel development on this engine. Before you touch anything:
1. Check for ongoing parallel work first: `git worktree list`, `git branch -a`,
   and `git fetch && git status` to see other branches/trees in flight.
2. If your change fits an existing branch/worktree, work there.
3. If it does not fit any of them, create a NEW worktree (do not start work
   loose on `main` or pile onto an unrelated branch). Isolate the change in its
   own tree so concurrent work does not collide.

This applies to every change with one exception below: code, tests, docs, and
version/release work.

- **How to create the tree:** prefer the native `EnterWorktree` tool, not
  `git worktree add`. The native tool is what the parallel-dev workflow expects.
- **Branch / tree naming:** `feature/<short-name>` for new features, `fix/<short-name>`
  for bug fixes, `<batchN>-promote` for game-code-into-engine promotion batches
  (e.g. `batch1-promote`). Keep the worktree directory name matching the branch.
- **Trivial-change exception:** a self-contained edit that ships no package
  (a doc typo, a comment, a CLAUDE.md/governance tweak, a one-line non-API fix
  with no version bump) may be made directly on a clean `main` without a worktree,
  as long as the parallel-work check in step 1 comes back clean. Anything that
  touches public API, tests, or triggers the release ritual still needs a tree.

## Rules
- `MonoGameRawInput` is the ONLY class that may touch Mouse/Keyboard/GamePad/TouchPanel
  statics. Everything else reads `RawInputState` via `IRawInput` - keeps input headless-testable.
- New behaviour ships with a headless test in `KhaozEngine.Tests` (build `RawInputState`
  frame-by-frame; `GameTime` is `new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt))`).
- Hit-test via `InputManager` bounds helpers (`IsTapIn`, etc.), never raw position + button.

## Build / test / release
- `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` - every new behaviour ships with a headless test.
- **Always update `CHANGELOG.md` on every version bump.** Add a newest-first entry describing the
  public API / behaviour change in the SAME commit as the `Directory.Build.props` `<Version>` bump.
  Never bump the version (or tag a release) without a matching changelog entry.
- Release ritual, in order: bump the version line in `Directory.Build.props` (`<KhaozEngine5xVersion>` for
  an engine/5.x release — the normal case; `<Version>` only for a legacy 4.x-MonoGame release) → add the
  `CHANGELOG.md` entry → update the engine-version declarations the guard checks (`docs/CONSUMERS.md` "Engine
  current version", `docs/ROADMAP.md` "Current released version", and the `README.md` `<PackageReference>`
  example) → `dotnet pack -c Release -o ./local-feed` (cumulative; don't `rm` old versions, consumers
  pin) → commit → `git tag vX.Y.Z` → push `main` + the tag (CI publishes to GitHub Packages on `v*`).
- `scripts/check-doc-versions.sh` enforces those three declarations match the **5.x line**
  (`<KhaozEngine5xVersion>`, which is the engine); CI runs it on every push, so a forgotten bump fails the
  build. The legacy 4.x `<Version>` and consumer pins are exempt and may lag.
- **`docs/CONSUMERS.md` tracks which game pins which package version.** Update its version matrix
  whenever a consumer bumps a `KhaozEngine.*` `<PackageReference>`, and the engine-version line on
  every release. Refresh snippet is at the bottom of that file.
- SemVer: additive = minor, fixes = patch, breaking = major.
- **Two shared version lines (5.x = the engine; 4.x = legacy MonoGame).** `Directory.Build.props` carries two
  shared versions. `<KhaozEngine5xVersion>` governs the **5.x line, which IS the engine**: the custom-stack
  (MonoGame-free) packages (`KhaozEngine.Gpu`, `Windowing`, `Render2D`, `Render3D`, `Gui`, `Audio`, `Particles`,
  `Effects`, `Game`) **and**, as of **`5.46.0`** (audit P1#9), the graduated MonoGame-free foundation packages (`Ecs`/
  `Serialization`/`Content`/`Diagnostics`/`App`/`Localization`/`Persistence`/`Pooling`/`Platform`/`Updates`/
  `Collision`/`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib`). All of those set
  `<Version>$(KhaozEngine5xVersion)</Version>` in their csproj. The 5.x line dropped the `-experimental` suffix
  at `5.31.0`; the tag is plain `vX.Y.Z` (releases up to `5.30.0-experimental` carried the suffix). `<Version>`
  governs the **legacy 4.x line**, which now carries **ONLY** the genuinely-MonoGame packages
  (`UI`/`Graphics`/`Screens`/`Sprites`/`Input`/`Time`), consumed by the still-4.x SpaceGame; it is
  frozen-ish (bump only when a MonoGame package itself needs a release) and gets deleted with MonoGame once
  SpaceGame migrates. Each line bumps as a unit: bump `<KhaozEngine5xVersion>` to release ALL 5.x packages
  together (repack to `local-feed`, single tag `vX.Y.Z`) — this is the normal release; bump `<Version>` only for
  a legacy 4.x release. The two lines move independently. `check-doc-versions.sh` enforces the **5.x line**
  (`<KhaozEngine5xVersion>`); the 4.x `<Version>` and consumer pins are exempt and may lag. NOTE: early Render3D
  releases (`5.0.0-experimental`, `5.1.0-experimental`) predate the shared 5.x line and were per-package; from
  `5.2.0-experimental` on, the 5.x line is shared. See `docs/ROADMAP.md` ("The post-MonoGame pivot").
- **Commit subjects:** conventional-commit style `area(scope): summary`, e.g.
  `audio(4.3.1): MacOsMusicBackend loads built .ogg` or `docs(consumers): ...`.
  On a release/version-bump commit, use the new version as the scope (`audio(4.3.1):`).
- **One version bump per batch, not per item.** When a worktree promotes several
  related items, commit each item individually but do the single `Directory.Build.props`
  bump + `CHANGELOG.md` entry + `dotnet pack` ONCE at the end of the batch, then do
  per-consumer adopt PRs. Never bump the version per-item within a batch.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- net10.0, MonoGame.Framework.DesktopGL 3.8, xUnit.
