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
- Release ritual, in order: bump `<Version>` in `Directory.Build.props` → add the `CHANGELOG.md`
  entry → update the engine-version declarations the guard checks (`docs/CONSUMERS.md` "Engine current
  version", `docs/ROADMAP.md` "Current released version", and the `README.md` `<PackageReference>`
  example) → `dotnet pack -c Release -o ./local-feed` (cumulative; don't `rm` old versions, consumers
  pin) → commit → `git tag vX.Y.Z` → push `main` + the tag (CI publishes to GitHub Packages on `v*`).
- `scripts/check-doc-versions.sh` enforces those three declarations match `Directory.Build.props`; CI
  runs it on every push, so a forgotten bump fails the build (consumer pins are exempt and may lag).
- **`docs/CONSUMERS.md` tracks which game pins which package version.** Update its version matrix
  whenever a consumer bumps a `KhaozEngine.*` `<PackageReference>`, and the engine-version line on
  every release. Refresh snippet is at the bottom of that file.
- SemVer: additive = minor, fixes = patch, breaking = major.
- **Commit subjects:** conventional-commit style `area(scope): summary`, e.g.
  `audio(4.3.1): MacOsMusicBackend loads built .ogg` or `docs(consumers): ...`.
  On a release/version-bump commit, use the new version as the scope (`audio(4.3.1):`).
- **One version bump per batch, not per item.** When a worktree promotes several
  related items, commit each item individually but do the single `Directory.Build.props`
  bump + `CHANGELOG.md` entry + `dotnet pack` ONCE at the end of the batch, then do
  per-consumer adopt PRs. Never bump the version per-item within a batch.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- net10.0, MonoGame.Framework.DesktopGL 3.8, xUnit.
