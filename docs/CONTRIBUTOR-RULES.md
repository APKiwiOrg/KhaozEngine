# KhaozEngine contributor rules

This living reference holds the detailed repository rules linked from root `AGENTS.md`. The root keeps
the short instructions that must load in every session. Read the relevant section here before work in
that area.

## Worktrees and concurrent development

Engine development is heavily parallel. Use an isolated worktree for code, tests, release work and
substantial documentation. Before starting, inspect `git worktree list`, branches, fetch state and the
target branch. Join an existing matching worktree or create a new one from current `origin/main` after
fetching. If local `main` contains a required unpushed commit, branch from that exact commit.

Use `feature/<short-name>` for features, `fix/<short-name>` for fixes and `<batchN>-promote` for game
promotion batches. Keep the directory and branch names aligned. A self-contained documentation typo,
comment, governance edit or one-line non-API fix may use a clean `main` only when the concurrency check
shows no conflicting work.

In a shared worktree, stage explicit paths and commit with an explicit pathspec. A bare `git commit`
can absorb another contributor's staged files. Never use the shared stash for worktree coordination.

## Engine code and test contracts

- `AppWindow` in `KhaozEngine.Windowing` is the only class that touches Silk.NET or GLFW input
  statics. All other code reads the immutable `InputState` snapshot through `InputManager` and
  `Pointer`. Hit testing uses their bounds helpers.
- New behavior gets a headless test in the matching `KhaozEngine.<Area>.Tests` project. Use the rump
  `KhaozEngine.Tests` project only for genuinely cross-cutting contracts. Declared namespaces remain
  `KhaozEngine.Tests.*` and new test projects set `<IsPackable>false</IsPackable>`.
- A test project references only the engine projects it uses. Push CI selects affected tests from the
  reference graph, so a broad reference silently turns targeted validation into a larger and less
  useful slice.
- A test that writes process-global state joins a collection whose definition sets
  `DisableParallelization = true`. The collection name alone only serializes members of that named
  collection against one another. Existing patterns include `gui-theme-global`, `ClipboardSerial`,
  `LoggingSerial`, `AmbientLocalization`, `AllocSensitive` and `NativeDeviceLifecycle`.
- A GPU test class that captures more than a few images shares one lazily created `Scene3D` through a
  class fixture. It also carries a parity test that ages the shared scene through several
  configurations and compares it with a fresh snapshot scene. `OceanFocusScene` is the reference.
- CI runs Release. Local `dotnet test` defaults to Debug. Tests around `Debug.Assert`,
  `[Conditional("DEBUG")]` or `#if DEBUG` need a Release run and a configuration-independent
  observable.
- Player-facing text resolves through `StringId` and the localization catalog. Prefer `LocalizedText`
  at GUI sinks. Developer-only output and non-localizable tokens use the explicit raw escape hatch.

### File-size ratchet

KESIZE001 and KESIZE002 are a design pressure, not a formatting target. Put new behavior in its own
type. Do not split a cohesive file at an arbitrary line. A baseline increase or exemption is the
owner's decision and requires approval. Ratcheting down is free through
`scripts/check-file-size.sh --update`.

An `exempt <path>` entry is for a file whose size grows only when fixed-form data grows. It is not for
a screen, frame loop or test fixture that gains lines with each feature. Inspect history when the
classification is unclear. The detailed rationale lives in
[`design/FILESIZE-ANALYZER-DESIGN-2026-07-20.md`](design/FILESIZE-ANALYZER-DESIGN-2026-07-20.md).

## Dependencies and package structure

The authoritative package and umbrella catalog is the root [`README.md`](../README.md). The full
dependency edges are its `Depends on` column and each umbrella csproj. Do not maintain another package
list here.

The render stack starts at `Primitives`, then `Gpu`, `Windowing`, `Render2D` or `Render3D`, then `Gui`
and game packages. GPU-free Foundation packages sit beside it. The server stack starts at
`Simulation`, then `Netcode`, `Replication`, `Sharding` and `WorldStore`, then `NetWorld`. `Ecs`
depends on `Simulation`. Optional physics and persistence backends stay explicit references. The
umbrella packages are code-free dependency groups.

Wrap third-party libraries behind dependency-free engine seams with opt-in backends. Read
[`DEPENDENCY-SEAMS.md`](DEPENDENCY-SEAMS.md) before adding or changing a dependency edge. The package
id is `KhaozEngine.Sharding`, not `KhaozEngine.World`, because a World package shadows the ECS type.

## Build and CI

Run from the repository root:

```sh
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
```

Warnings are errors in every configuration. Fix warnings at the source. Do not add a blanket
suppression or disable `TreatWarningsAsErrors`. The standing XML documentation suppression is the
narrow exception.

Ordinary pushes and pull requests run `scripts/ci-selective-test.sh`. Tag pushes and manual runs use
the full restore, Release build, test, determinism, pack and publish path. Convention, instruction
budget and doc-version checks run before both paths. See
[`design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md`](design/CI-SELECTIVE-TESTS-DESIGN-2026-07-18.md).

The engine's GPU matrix uses hosted `metal-native`, `direct3d11-native` and `vulkan-native` legs.
Goldens are backend-family artifacts. A new or changed golden must be baked by the relevant CI leg.
Plain `dotnet test` skips GPU facts. A local GPU proof sets `KE_GPU_TESTS=1` and confirms zero skipped
render tests. [`CROSS-PLATFORM.md`](CROSS-PLATFORM.md) owns the full matrix contract.

If a sandboxed build fails only because it cannot read the user gitconfig, create `.buildhome` in the
worktree and run with `HOME="$PWD/.buildhome"` plus the real NuGet cache through `NUGET_PACKAGES`.
Never commit `.buildhome`.

## Version, package and release rules

`Directory.Build.props` contains the one `<KhaozEngineVersion>` for every package. SemVer is additive
minor, fix patch and breaking major. Tooling, documentation and repository governance do not bump the
engine version.

For a package-bearing change:

1. Fetch and inspect current `main`, the version and tags. A concurrent change may have taken the
   planned number.
2. Ride an existing unreleased version when one is staged. Otherwise choose the next free version.
3. Add or extend the newest `CHANGELOG.md` entry in the same commit as the version change.
4. Update every version declaration guarded by `scripts/check-doc-versions.sh`.
5. Run the full documentation sweep described below.
6. Build and test in Release.
7. Run `scripts/pack-local-feed.sh`. Never write released bytes with a bare pack command.
8. Commit, reconcile with current `main`, verify again, merge and push `main` through the owning
   orchestrator.

One related batch gets one version bump. Item commits can remain separate, followed by one release
commit. Commit subjects use `area(scope): summary`. A release commit uses the new version as scope.

Packing is cumulative and occurs on every package-bearing finish. `scripts/pack-local-feed.sh` refuses
to overwrite a released version unless the tree is exactly that clean tagged commit. The pack standard
lives in `scripts/pack-standard.sh` and is covered by `scripts/tests/pack-local-feed.test.sh`. Run
`scripts/check-local-feed.sh` before a consumer vendors packages.

A release tag is separate and user-started. Create it with `scripts/tag-release.sh`, which reads the
version and creates the canonical annotated message. Do not hand-create a tag. The only automatic tag
exception is when a consumer is explicitly pinned and waiting on this engine change. In that case the
dependency is already blocked, so the engine release completes without another prompt.

`local-feed/` is a gitignored development convenience. GitHub Packages is the durable release store.
Prune the local feed only above the lowest version still pinned by a consumer.

## Documentation governance

Each fact has one canonical source:

- Package inventory, umbrella membership and package summaries: root `README.md`.
- Consumer use and public API examples: `docs/USING-KHAOZENGINE.md`.
- Dependency edges and backend seams: `docs/DEPENDENCY-SEAMS.md`.
- Per-package quick reference: that package's own `README.md`, shipped inside its nupkg.
- Release history: root `CHANGELOG.md`.
- Design rationale: `docs/design/` with a row in `docs/INDEX.md`.
- Open work: GitHub Issues.

Every feature, bug fix and API change gets a full doc sweep. Search the added or removed type,
package, flag and behavior across every Markdown file, package README and `AGENTS.md`. Correct stale
descriptions, not only version declarations. `scripts/check-doc-versions.sh` verifies package catalog
coverage, package readmes, example versions and the newest changelog heading. It cannot verify prose
accuracy.

A completed design document remains as history, but it is not a reference surface. Move shipped API
and use instructions into the living docs. Any deferral left when a program completes becomes an
issue. Root `docs/` contains living references only.

## Issues and handoffs

The backlog and roadmap are GitHub Issues. `docs/TODO.md` and `docs/ROADMAP.md` are retired. Search
prior art first with `scripts/ledger.sh search <identifier>`, including closed issues. File a
`kind/backlog` issue for actionable work and `kind/roadmap` when the work needs its own design spec.

Every issue carries one confidence label:

- `confidence/verified`: checked against code or a reproduction.
- `confidence/authored`: a deliberate product or architecture choice.
- `confidence/lead`: surfaced but not yet verified.
- `confidence/refuted`: declined with the reason recorded before closure.

Cross-repository handoffs use `needs/upstream` and a full GitHub URL. Consumer fit failures are a
primary API-gap signal. File linked issues in both repositories, label the engine issue `parity`, add
it to the organization board and include code-cited fit evidence. Board membership is manual.

The session ledger is informational. Keep authenticated `gh` access available so rate limits cannot
turn an incomplete mirror into a false zero.
