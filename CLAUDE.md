# KhaozEngine

Shared, game-agnostic engine - a custom MonoGame-free 2D/3D render + windowing/input + Gui + ECS + netcode
stack (Hardpoint, Nullwake, SpaceGame, Ruinborne all run on it). See README.md and docs/USING-KHAOZENGINE.md.

## Before starting ANY engine work (concurrent-dev rule)
This section is the engine's instance of the global "Branching, worktrees, and finishing work"
default (worktree per change; finish by merge to `main` + commit + push). It wins where it differs:
heavy parallel dev makes the worktree mandatory (with the trivial-change exception below), and a
finished release is a full publish (merge + push `main` + push the `vX.Y.Z` tag + pack to
`local-feed`). **The publish (push + tag) is routinely HELD and BATCHED, and confirmed with the
user before pushing:** CI publishes every package to GitHub Packages on each `v*` tag, so related
work is committed + packed locally across several version bumps and pushed together, not
per-feature (the merge to `main` happens as work finishes; only the push/tag is batched). There
is a lot of parallel development on this engine.
Before you touch anything:
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
  **But this repo holds/batches pushes, so local `main` is routinely ahead of
  `origin/main`, and `EnterWorktree` branches from `origin/<default-branch>` by
  default - a tree it makes then silently lacks your unpushed commits.** If the
  change builds on unpushed local work (e.g. the next layer atop a just-merged but
  unpushed one), create the tree from local HEAD instead:
  `git worktree add .claude/worktrees/<name> -b worktree-<name> main`, then
  `EnterWorktree` with its `path` to switch in.
- **Branch / tree naming:** `feature/<short-name>` for new features, `fix/<short-name>`
  for bug fixes, `<batchN>-promote` for game-code-into-engine promotion batches
  (e.g. `batch1-promote`). Keep the worktree directory name matching the branch.
- **Trivial-change exception:** a self-contained edit that ships no package
  (a doc typo, a comment, a CLAUDE.md/governance tweak, a one-line non-API fix
  with no version bump) may be made directly on a clean `main` without a worktree,
  as long as the parallel-work check in step 1 comes back clean. Anything that
  touches public API, tests, or triggers the release ritual still needs a tree.

## Rules
- `AppWindow` (KhaozEngine.Windowing) is the ONLY class that touches the Silk.NET/GLFW input
  statics. Everything else reads the immutable `InputState` snapshot (handed in via `Frame.Input`)
  through `InputManager`/`Pointer` - keeps input headless-testable. (There is no `MonoGameRawInput`
  or `IRawInput` any more; the engine is MonoGame-free.)
- New behaviour ships with a headless test in `KhaozEngine.Tests` (construct an `InputState`
  frame-by-frame and feed `InputManager.Update(input, viewport?)`; `dt` is a plain `float` in seconds,
  no `GameTime`).
- Hit-test via `InputManager`/`Pointer` bounds helpers (`IsTapIn`, etc.), never raw position + button.

## Build / test / release
- `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` - every new behaviour ships with a headless test.
- **Always add a `CHANGELOG.md` entry on every version bump.** Newest-first, detailed (public API / behaviour
  change), with a tight one-line summary as the entry's first sentence so the file doubles as the high-level
  "history over time" view. It goes in the SAME commit as the `Directory.Build.props` version bump. Never bump
  the version (or tag a release) without it. (There is no separate `CHANGENOTES.md` - it was folded into
  `CHANGELOG.md`; one file is the single source of truth.)
- **Before merging back / releasing, re-check for concurrent work and integrate it FIRST.** Parallel dev is
  heavy here and local `main` is routinely ahead of `origin/main`, so always assume `main` moved under you. `git
  fetch`; if `main` advanced past your tree's base, merge `main` INTO your tree first, resolve every conflict and
  re-run the build + tests on the merged result THERE, so the merge back is clean (never resolve a pile of
  conflicts on `main`). The shared `<KhaozEngineVersion>` line collides constantly: a concurrent chat may have
  already bumped it and tagged that `vX.Y.Z`, so re-read the current version on the up-to-date `main` and take the
  next FREE version for your bump + tag (and rebase your `CHANGELOG.md` entry onto it).
- Release ritual, in order: bump `<KhaozEngineVersion>` in `Directory.Build.props` → add the
  `CHANGELOG.md` entry → update the engine-version declarations the
  guard checks (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", and
  the `README.md` `<PackageReference>` example) → `dotnet pack -c Release -o ./local-feed` (cumulative within a
  release) → commit → `git tag vX.Y.Z` → push `main` + the tag (CI publishes to GitHub Packages on `v*`).
  `local-feed/` is a gitignored dev convenience; GitHub Packages (every published `v*`) is the durable store, so
  `local-feed` may be pruned up to the lowest version any consumer still pins (see `docs/CONSUMERS.md`; do not prune
  below it) without losing anything recoverable.
- **Full doc sweep on EVERY feature / bug / change - not just the guard-checked declarations.**
  `check-doc-versions.sh` only verifies the 3 version strings; it does NOT catch package/feature docs drifting.
  Each artifact has ONE canonical source - edit that one, the rest point at it:
  - **Package + umbrella catalog -> `README.md`** (the package table + "Umbrella metapackages" table, plus the
    repo-layout block). When a package is ADDED/REMOVED or its summary/deps change, edit the README table.
    `docs/CONSUMERS.md` and this `CLAUDE.md` only POINT at the README catalog - do not re-enumerate packages in
    either. (7.34.0 shipped with the README package table missing two new packages - one source means one place
    to forget.)
  - **Per-package API -> that package's own `<Package>/README.md`.** When public API is ADDED/CHANGED WITHIN an
    existing package, update the package's own README - the `PackageReadmeFile` that ships *inside the nupkg* and is
    read standalone on NuGet.org, so it rots independently of the master catalog (the `NetWorld` README still
    described pre-8.0.0 `WorldColliders`/`WorldSurfaces` ctor params two releases later; 8.2.0's telemetry types
    were missing from the `Diagnostics`/`Gui`/`Netcode` READMEs). These stay self-contained; keep them correct.
  - **Usage -> `docs/USING-KHAOZENGINE.md`** (a section for new public API); **seams/edges ->
    `docs/DEPENDENCY-SEAMS.md`** whenever a dependency edge or a seam member changed.
  - **`CHANGELOG.md`** + the version bump as before; for a behaviour/bug change also fix any doc, README, or code
    comment that described the OLD behaviour.
  Mechanical check before committing: grep the new (or removed) type / package / flag name across **ALL `*.md`
  recursively** (root, `docs/`, AND every per-package `<Package>/README.md`) + `CLAUDE.md`, and confirm every place
  that should mention it does (and no stale doc still describes what you removed).
- `scripts/check-doc-versions.sh` enforces those three declarations match the **engine version line**
  (`<KhaozEngineVersion>`); CI runs it on every push, so a forgotten bump fails the
  build. Consumer pins are exempt and may lag.
- **`docs/CONSUMERS.md` tracks which game pins which package version.** Update its version matrix
  whenever a consumer bumps a `KhaozEngine.*` `<PackageReference>`, and the engine-version line on
  every release. Refresh snippet is at the bottom of that file.
- SemVer: additive = minor, fixes = patch, breaking = major.
- **One shared version line - the engine is entirely MonoGame-free.** `Directory.Build.props` carries a single
  `<KhaozEngineVersion>` governing the WHOLE engine; every packable project sets
  `<Version>$(KhaozEngineVersion)</Version>` in its csproj, so one bump releases all packages together (repack to
  `local-feed`, single tag `vX.Y.Z`). `check-doc-versions.sh` enforces this line. **Per-version history lives in
  `CHANGELOG.md`, and the package + umbrella catalog (every package, what it gives you, and its deps) is the
  table in `README.md` - not here.** When a package is added/removed or a summary/dep changes, edit the README
  table (the single source) plus that package's own `<Package>/README.md`; this file and `docs/CONSUMERS.md` only
  point at the README catalog. The only package facts kept here are the engine-dev orientation below:
  - **Dependency layering** (full per-package deps are the README "Depends on" column): `Primitives` is the
    zero-dependency leaf at the bottom; the render/runtime stack layers `Gpu` -> `Windowing` ->
    `Render2D`/`Render3D` -> `Gui`/`Game`/`Game.Render3D`; the GPU-free `Foundation` packages (Ecs, Serialization,
    Content, Diagnostics, App, Locomotion, Persistence, Platform, Updates, Collision,
    Physics, Terrain, Determinism) sit beside it (9.0.0 folded Pooling into Primitives, Localization into App,
    and Effects into Particles); the server/netcode stack layers `Simulation` (a zero-dependency
    leaf) -> `Netcode`/`Replication`/`Sharding`/`WorldStore` -> `NetWorld`. `Ecs` depends on `Simulation` (acyclic).
    Opt-in, in NO umbrella, added explicitly: `Physics.Bepu`, `WorldStore.Sqlite`/`.SqlServer`, `Server.Admin`.
    The four umbrellas (`Foundation`, `Game2D`, `Game3D`, `Server`) are code-free dependency groups.
  - **Gotchas / history:** the package id is `KhaozEngine.Sharding`, NOT `KhaozEngine.World` (a `World` leaf would
    shadow the ECS `World` type). The legacy 4.x MonoGame line + its six packages
    (`UI`/`Graphics`/`Screens`/`Sprites`/`Input`/`Time`) were DELETED - there is no 4.x line; all consumers are on
    the 7.x/8.x line (pins in `docs/CONSUMERS.md`). The line dropped `-experimental` at `5.31.0`; the foundation
    graduated onto it at `5.46.0`. See `CHANGELOG.md` (top) for the MonoGame-free pivot history.
- **Commit subjects:** conventional-commit style `area(scope): summary`, e.g.
  `audio(4.3.1): MacOsMusicBackend loads built .ogg` or `docs(consumers): ...`.
  On a release/version-bump commit, use the new version as the scope (`audio(4.3.1):`).
- **One version bump per batch, not per item.** When a worktree promotes several
  related items, commit each item individually but do the single `Directory.Build.props`
  bump + `CHANGELOG.md` entry + `dotnet pack` ONCE at the end of the batch, then do
  per-consumer adopt PRs. Never bump the version per-item within a batch.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- net10.0, MonoGame-free: Silk.NET (windowing + input, GLFW natives bundled per-RID), Veldrid behind
  `KhaozEngine.Gpu` (GPU), Silk.NET.OpenAL (audio), xUnit (tests).
