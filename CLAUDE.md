# KhaozEngine

Shared, game-agnostic engine - a custom MonoGame-free 2D/3D render + windowing/input + Gui + ECS + netcode
stack (Hardpoint, Nullwake, SpaceGame all run on it). See README.md and docs/USING-KHAOZENGINE.md.

## Before starting ANY engine work (concurrent-dev rule)
This section is the engine's instance of the global "Branching, worktrees, and finishing work"
default (worktree per change; finish by merge to `main` + commit + push). It wins where it differs:
heavy parallel dev makes the worktree mandatory (with the trivial-change exception below), and a
finished release is a full publish (merge + push `main` + push the `vX.Y.Z` tag + pack to
`local-feed`), not a held local merge. There is a lot of parallel development on this engine.
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
- **Always update BOTH `CHANGELOG.md` AND `CHANGENOTES.md` on every version bump.** `CHANGELOG.md` gets the
  newest-first detailed entry (public API / behaviour change); `CHANGENOTES.md` gets a newest-first one-or-two
  sentence digest line (the high-level "history over time" view). Both go in the SAME commit as the
  `Directory.Build.props` version bump. Never bump the version (or tag a release) without both.
- Release ritual, in order: bump `<KhaozEngine5xVersion>` in `Directory.Build.props` → add the
  `CHANGELOG.md` entry → add the one-line `CHANGENOTES.md` entry → update the engine-version declarations the
  guard checks (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", and
  the `README.md` `<PackageReference>` example) → `dotnet pack -c Release -o ./local-feed` (cumulative within a
  release) → commit → `git tag vX.Y.Z` → push `main` + the tag (CI publishes to GitHub Packages on `v*`).
  `local-feed/` is a gitignored dev convenience; GitHub Packages (every published `v*`) is the durable store, so
  `local-feed` may be pruned up to the lowest version any consumer still pins (currently `7.3.0`) without losing
  anything recoverable.
- **Full doc sweep on EVERY feature / bug / change - not just the guard-checked declarations.**
  `check-doc-versions.sh` only verifies the 3 version strings; it does NOT catch package/feature docs drifting,
  so those silently rot (7.34.0 shipped with the `README.md` package table and this `CLAUDE.md` package map both
  missing the two new packages). After ANY change, sweep ALL docs that could reference what you touched and update
  every one. When a package is ADDED/REMOVED: the `README.md` package-catalog table + the repo-layout block, this
  `CLAUDE.md` package enumeration (the `<KhaozEngine5xVersion>` list above) + the umbrella descriptions,
  `docs/CONSUMERS.md` (the umbrella/package table), and `docs/USING-KHAOZENGINE.md` (a usage section for new public
  API). For a behaviour/bug change: `CHANGELOG.md` + `CHANGENOTES.md` AND any doc, README, or code comment that
  described the OLD behaviour. Mechanical check before committing: grep the new (or removed) type / package / flag
  name across `*.md` + `CLAUDE.md` and confirm every place that should mention it does (and no stale doc still
  describes what you removed).
- `scripts/check-doc-versions.sh` enforces those three declarations match the **5.x line**
  (`<KhaozEngine5xVersion>`, which is the engine); CI runs it on every push, so a forgotten bump fails the
  build. Consumer pins are exempt and may lag.
- **`docs/CONSUMERS.md` tracks which game pins which package version.** Update its version matrix
  whenever a consumer bumps a `KhaozEngine.*` `<PackageReference>`, and the engine-version line on
  every release. Refresh snippet is at the bottom of that file.
- SemVer: additive = minor, fixes = patch, breaking = major.
- **One shared version line - the engine is now entirely MonoGame-free.** `Directory.Build.props` carries a
  single `<KhaozEngine5xVersion>` governing **the whole engine**: the zero-dependency `Primitives` leaf
  (`Color`/`DeterministicRng`/`XorRng`/`MathUtil`/`ViewportMath`/`Easing`, new at `6.0.0`), the BCL-only
  `Imaging` leaf (`PngWriter`, the dependency-free RGBA8 PNG encoder; `Render2D.Png` is a shim over it, new at
  `7.33.0`), the custom-stack
  packages (`KhaozEngine.Gpu`,
  `Windowing`, `Render2D`, `Render3D`, `Gui`, `Audio`, `Particles`, `Effects`, `Game`, `Game.Render3D`), the
  headless snapshot harness libraries (`Snapshot` = 2D `SnapshotRunner`/`SnapshotHost`; `Snapshot.Render3D` = the
  `Shot3D` extension; tooling, in NO umbrella, referenced directly by a game's snapshot tool, new at `7.33.0`), the
  attack-telegraph libraries (`Telegraphs` = `TelegraphStyle`/`TelegraphResolve`/`TelegraphRenderer2D`, in the
  `Game2D` umbrella; `Telegraphs.Render3D` = the `Scene3D` ground-plane `Ground*` extensions over the generic
  `Render3D.DrawGroundDecal` depth-sampling decal primitive, in the `Game3D` umbrella; new at `7.34.0`) and the
  MonoGame-free foundation (`Ecs`/`Serialization`/`Content`/`Diagnostics`/`App`/`Localization`/`Persistence`/
  `Pooling`/`Platform`/`Updates`/`Collision`/`Netcode`/`Netcode.Abstractions`/`Netcode.LiteNetLib`/`Simulation`/
  `Replication`/`WorldStore`/`Sharding`)
  plus the four
  code-free umbrella metapackages (`Foundation`, `Game2D`, `Game3D`, `Server`). (`Simulation` = the headless
  `FixedTickHost` fixed-tick accumulator, new at `7.35.0` with the MMO netcode stack's Phase 0; `Netcode` gained
  the `INetTransport` seam + `LoopbackTransport` and `Netcode.LiteNetLib` gained the UDP `INetTransport` bindings
  the same release. `7.36.0` added the MMO stack's Phases 1+2: `Netcode` gained the `NetServer`/`NetClient`
  session layer; new `Replication` = authoritative ECS replication (`NetId`/`ReplicationRegistry`/`SnapshotWriter`/
  `ClientReplicationView`/`ServerReplicator` full-state+delta+interpolation, `InterestGrid` AoI); new `WorldStore` =
  the `IWorldStore` async durable-state seam + `InMemoryWorldStore`. Both new packages are in the `Server`
  umbrella. Phase 3A added `Sharding` = the in-process world-cell-grid topology
  (`CellCoord`/`CellSim`/`ShardHost`; a cell = an ECS `World` + `FixedTickHost` + `ServerReplicator` +
  `InterestGrid`), depends on `Ecs`/`Simulation`/`Replication`, also in the `Server` umbrella; Phase 3B extended it
  with cross-cell border ghosting (`ICellLink`/`InProcessCellLink` inter-cell messaging seam, `CellMessage`, a
  `Ghost` read-only-mirror tag, `CellPositionAccessor`, and `ShardHost.SyncGhosts`/`OverlapMargin`); Phase 3C added
  authority handoff (a `Migrating` tag, `Migrate`/`MigrateAck` message kinds, kind-scoped `ICellLink.Drain`, and
  `ShardHost.ProcessHandoffs`/`OwnerCount`/`TryGetOwner`) - exactly-once cell-crossing transfer (no dup/loss);
  Phase 3D added client home-cell serving (`ShardHost.BindClient`/`UnbindClient`/`TryGetHomeCell`/
  `SnapshotForClient`, `CellSim.RebuildInterest`) - a client's whole AoI is served from the single cell owning its
  player (invariant overlap margin >= interest radius, enforced), re-binding seamlessly on a crossing. Note:
  the package id is `KhaozEngine.Sharding`, NOT `KhaozEngine.World` - a namespace whose leaf is literally `World`
  would shadow the ECS `World` type. `Sharding` (3A + 3B + 3C + 3D) is built and held UNPUBLISHED pending the
  Phase 3 batch release (policy B), so it carries no version attribution yet.) (`KhaozEngine.Content.Validator`
  is a build-time tool, `IsPackable=false`, shipped inside the `Content` package rather than versioned itself.)
  `KhaozEngine.Updates.Tool` (the `ke-updater` dotnet tool: manifest/genkey/sign/verify, shipped at `7.3.0`)
  and `KhaozEngine.Sfx.Tool` (the `ke-sfxbake` dotnet tool: manifest-driven bulk SFX generation + bake via the
  ElevenLabs API + ffmpeg/oggenc, shipped at `7.14.0`) are both `PackAsTool` and ride the same shared version line.
  All packable projects set `<Version>$(KhaozEngine5xVersion)</Version>` in their csproj. Bump it to release ALL
  packages together
  (repack to `local-feed`, single tag `vX.Y.Z`); `check-doc-versions.sh` enforces this line. The 5.x line
  dropped the `-experimental` suffix at `5.31.0` (the tag is plain `vX.Y.Z`); the foundation graduated onto it
  at `5.46.0`. **The legacy 4.x MonoGame `<Version>` line + its six packages (`UI`/`Graphics`/`Screens`/
  `Sprites`/`Input`/`Time`) were DELETED from the repo; there is no 4.x line any more.** All three consumers
  have finished porting onto the 7.x line and no longer reference 4.x (per-consumer pins live in
  `docs/CONSUMERS.md`). See `docs/ROADMAP.md` ("The post-MonoGame pivot").
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
