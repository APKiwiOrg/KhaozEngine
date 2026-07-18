# CI Selective Tests Design (2026-07-18)

Status: in progress on `feature/ci-selective-tests`. Program issue: [#207](https://github.com/APKiwiOrg/KhaozEngine/issues/207)
(which also records the rejected alternatives: per-area workflow files, consumer smoke leg).

## Problem

`ci.yml` has grown from ~2 min (its own header comment) to ~4.5-5 min per push. Measured steps on run
29624878784: restore 22s, build 108s, test 78s, determinism 27s, pack 22s. The suite is 5,610 tests in
the `KhaozEngine.Tests` monolith (plus four small satellite test projects), and the engine keeps
growing. Every push pays the full cost regardless of what changed, and the monolith gives the build
graph no information to select with.

Goals:

1. Push and PR cost (wall clock and billed minutes) proportional to what the diff actually affects.
2. Test layout aligned to the package dependency layering, so "affected" is meaningful and stays
   meaningful as the engine grows (the split is the durable asset, the CI wiring rides on it).
3. Full validation unchanged where it gates publishing: tag runs and the weekly hosted sweep still run
   everything.

Non-goals: changing `cross-platform-gpu.yml` legs or tiers (only its paths update), tiering the
self-hosted Metal leg (watch item in #207), any consumer smoke leg (declined in #207), per-area
workflow files (rejected in #207, hand path gates rot: the 10.18.1 miss).

## Decision 1: cluster set

`KhaozEngine.Tests` splits into nine per-area test projects plus a small rump plus a shared-helper
project. Folder assignments (recon verifies each folder's dominant references and corrects
mismatches before the move):

| New project | Monolith folders |
|---|---|
| `KhaozEngine.Foundation.Tests` | Primitives, App, Localization, Logging, Platform, Http, Content, Persistence, Diagnostics, Benchmarks + root foundation/determinism files |
| `KhaozEngine.Simulation.Tests` | Ecs, Simulation + root ECS files |
| `KhaozEngine.Server.Tests` | Netcode, Replication, Sharding, WorldStore, NetWorld, ServerStatus, ServerAdminEndpoint, Identity, Social, Commerce |
| `KhaozEngine.Render.Tests` | Gpu, Render2D, Render3D, Windowing, Snapshot, Terrain, Telegraphs, ParticlesRender3D, Imaging, Showcase (owns `Gpu/goldens/`) |
| `KhaozEngine.Gui.Tests` | Gui, Updates |
| `KhaozEngine.MapEditor.Tests` | MapEditor, MapEditTool, MapDoc |
| `KhaozEngine.Game.Tests` | Game, Locomotion, Navigation, Dungeon, Collision, Physics, Objectives, Progression + root collision files |
| `KhaozEngine.Audio.Tests` | Audio, Sfx |
| `KhaozEngine.Particles.Tests` | Particles |
| `KhaozEngine.Tests` (rump) | ArchitectureTests and the genuinely cross-cutting remainder, kept deliberately small |
| `KhaozEngine.TestSupport` (new, no tests) | Shared fixtures, builders, custom attributes (e.g. `GpuFact`) used by 2+ clusters |

The existing satellites (`Localization.Analyzers.Tests`, both `DecouplingTests`,
`Physics.HeadlessLoaderTests`, `tools/*.Tests`) are untouched.

## Decision 2: moves preserve namespaces

Files move with their declared namespaces unchanged (C# namespaces are independent of project
membership). Consequences, all deliberate:

- Every `FullyQualifiedName`-based filter survives untouched: `~DeterministicFp` in ci.yml,
  `~Golden` in cross-platform-gpu.yml.
- Assembly names DO change, so every `InternalsVisibleTo Include="KhaozEngine.Tests"` grant (~20
  csprojs) is retargeted to the new owning test project in the same commit as that cluster's move.
- xUnit collection definitions are per-assembly. Any `[Collection]` used from folders that land in
  different clusters gets a duplicate `[CollectionDefinition]` per cluster (recon inventories these).

## Decision 3: minimal references per test project

Each new test csproj references ONLY the engine projects its tests use (recon computes the union per
cluster from using directives, compile errors at the green gates catch the misses). This is the whole
point of the split: selection precision comes from honest reference sets. The rump may reference
whatever `ArchitectureTests` needs (potentially everything) and therefore runs on nearly every
selective run. That is acceptable because it stays small and fast.

`KhaozEngine.TestSupport` is `IsPackable=false`, ships nothing, and is exempt from the README catalog
and `check-doc-versions.sh` (the guard checks packable projects only, verified during
implementation). Not to be confused with `KhaozEngine.Localization.TestKit`, which is a shipped
package and stays one.

## Decision 4: selection mechanism is dotnet-affected

`dotnet-affected` 6.2.0 spiked green against this repo on net10: it discovered the full project set,
walked the graph, and correctly amplified a `Directory.Build.props` change to everything (the 13.0.1
release commit). It lands as a committed local tool manifest (`.config/dotnet-tools.json`).

The affected universe it reports is intersected with the test projects that are members of
`KhaozEngine.slnx` (7 today, ~16 after the split), which is exactly what root `dotnet test` runs. The
tool discovers by directory scan, the slnx is the authority, the intersection guards any future
divergence.

## Decision 5: ci.yml event split

Tag pushes and `workflow_dispatch` keep the current full path unchanged: restore, build, full test,
determinism double-pass, pack, publish. Push (main) and `pull_request` take a selective path,
encapsulated in `scripts/ci-selective-test.sh` so it is runnable and testable locally:

1. Diff base: `github.event.before` for pushes, merge-base against the base branch for PRs. A zero or
   unknown before-sha falls back to FULL.
2. Force-FULL file classes (belt and braces over the tool's own handling): `.github/workflows/**`,
   `scripts/**`, `Directory.Build.props`, `nuget.config`, `*.slnx`, `.config/dotnet-tools.json`.
3. Otherwise run `dotnet affected`, intersect with slnx test projects.
4. Empty set (docs-only push): doc guard only, no restore/build/test/pack. The job goes green in
   under a minute, and this is the common case the change exists for (docs, governance, changelog
   pushes).
5. Non-empty: restore + build the affected test projects (their references pull in exactly the needed
   slice), then `dotnet test --no-build` each, with the existing `Category!=LiveSocket` filter. The
   determinism step runs only when `KhaozEngine.Foundation.Tests` (owner of the `DeterministicFp`
   tests) is affected, scoped to that project instead of the whole solution.
6. No pack on selective runs. Pack requires a full build, and its regression window is bounded by the
   next tag run, which in this repo is hours. Tag runs still pack and publish exactly as today.

Known and accepted: release pushes carry the version bump in `Directory.Build.props`, so they run
FULL by rule 2. Correct (a shared-props change genuinely affects everything), and the tag run that
follows within minutes is full anyway. The selective win is the non-bump push.

## Decision 6: cross-platform-gpu.yml touches paths only

Path gates `KhaozEngine.Tests/Gpu/**` and `KhaozEngine.Tests/Imaging/**` become
`KhaozEngine.Render.Tests/**`. Goldens move to `KhaozEngine.Render.Tests/Gpu/goldens/` (with any
path literals in test code, recon inventories them). Legs, tiers, filters, and the weekly schedule
are unchanged. Full-suite legs run root `dotnet test`, which after the split means all test
assemblies sequentially, so the Vulkan one-device-at-a-time serialization semantics are preserved.

## Execution order

One commit per stage, full `dotnet build` + root `dotnet test` green after each:

1. `KhaozEngine.TestSupport` + `KhaozEngine.Foundation.Tests` extracted (proves the pattern).
2. Remaining clusters, roughly bottom-up (Simulation, Server, Render, Gui, MapEditor, Game, Audio,
   Particles). IVT retargets ride each cluster's commit. The gpu workflow path update rides the
   Render commit.
3. Selection: tool manifest, `scripts/ci-selective-test.sh`, ci.yml rewiring.
4. Docs sweep (AGENTS.md test-location rule, README repo layout, workflow headers), CHANGELOG,
   single version bump (patch, no package API changes).

## Risks

- Full-run test time rises slightly (per-assembly runner overhead, ~16 assemblies). Accepted,
  measured at the green gates. Knob if it ever hurts: `dotnet test` per-project parallelism.
- Latent test-ordering assumptions surface when collections split across assemblies. The green gates
  catch the loud ones, the weekly full hosted legs the rest.
- `dotnet-affected` abandonment or breakage: the script isolates it, the fallback is a ~150-line
  engine-owned tool on `Microsoft.Build.Graph` with the same contract.
- A future test project added without minimal references quietly degrades selection (everything
  affects it). The rump is the sanctioned place for that. AGENTS.md carries the rule.

## Verification

Each stage gates on a local full suite. After merge and release: the main push run must take the
selective path (expected FULL, the bump is in the diff), the tag run must run full and publish, and
the next natural docs-only push demonstrates the fast path. All three watched to green before the
issue closes.
