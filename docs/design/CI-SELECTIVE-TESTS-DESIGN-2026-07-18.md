# CI Selective Tests Design (2026-07-18)

Status: complete, shipped in 13.0.3. Program issue: [#207](https://github.com/APKiwiOrg/KhaozEngine/issues/207)
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

`KhaozEngine.Tests` splits into nine per-area test projects plus a small rump plus shared-helper
projects (three as implemented: `TestSupport`, `TestSupport.Services`, and `TestSupport.Gpu`, the
last split out mid-implementation, see "As implemented" below). Folder assignments (recon verifies
each folder's dominant references and corrects mismatches before the move):

| New project | Monolith folders |
|---|---|
| `KhaozEngine.Foundation.Tests` | Primitives, App, Localization, Logging, Platform, Http, Content, Persistence, Diagnostics, Updates + root foundation/determinism files |
| `KhaozEngine.Simulation.Tests` | Ecs, Simulation + root ECS files (except the Netcode-coupled integration file, which goes to Server) |
| `KhaozEngine.Server.Tests` | Netcode, Replication, Sharding, WorldStore, NetWorld, ServerStatus, ServerAdminEndpoint, Identity, Social, Commerce, Benchmarks + `Simulation/FixedTickHostSimulatorIntegrationTests.cs` |
| `KhaozEngine.Render.Tests` | Gpu, Render2D, Render3D, Windowing, Snapshot, Terrain, Telegraphs, ParticlesRender3D, Imaging, Showcase (owns `Gpu/goldens/`) + `Dungeon/DungeonKitAssetTests.cs` (validates Showcase kit content) |
| `KhaozEngine.Gui.Tests` | Gui |
| `KhaozEngine.MapEditor.Tests` | MapEditor, MapEditTool, MapDoc |
| `KhaozEngine.Game.Tests` | Game, Locomotion, Navigation, Dungeon (minus the kit-asset file), Collision, Physics, Objectives, Progression + root collision files |
| `KhaozEngine.Audio.Tests` | Audio, Sfx (Sfx tests the `KhaozEngine.Sfx.Tool` exe, referenced directly) |
| `KhaozEngine.Particles.Tests` | Particles |
| `KhaozEngine.Tests` (rump) | ArchitectureTests and the genuinely cross-cutting remainder, kept deliberately small |
| `KhaozEngine.TestSupport` (new, no tests) | Planned as shared fixtures/builders/custom attributes (e.g. `GpuFact`, the `AllocSensitive` fixture) used by 2+ clusters, referencing nothing beyond `KhaozEngine.Primitives`. As implemented it ended dependency-free instead, see "As implemented" below |
| `KhaozEngine.TestSupport.Services` (new, no tests) | The package-coupled cross-cluster fakes (Updates, ServerStatus). References those two leaf packages, and is referenced ONLY by the test projects that need the fakes (Foundation, Gui, Game, Server) |

### Recon corrections (what moved relative to the first cut)

The mapping recon (835 files scanned, full tables in the working notes) corrected the priors:
Benchmarks benchmark Ecs+Replication+Sharding tick perf, so they live with Server, not Foundation.
Updates has zero package dependency on Gui (it was grouped by folder adjacency only), so it lives
with Foundation and its fakes go to `TestSupport.Services` (Gui and Game consume them
cross-cluster). One Simulation file pulls in Netcode and moves to Server. One Dungeon file
validates Showcase kit assets and moves to Render. `ParticlesRender3D` stays in Render (it is the
Particles-to-Render3D seam and needs both). Real test count is ~5,867: 240 `[GpuFact]` and 17
`[SqlServerFact]` sit on custom Fact subclasses on top of the 5,610 plain `[Fact]`/`[Theory]`.

The existing satellites (`Localization.Analyzers.Tests`, both `DecouplingTests`,
`Physics.HeadlessLoaderTests`, `tools/*.Tests`) are untouched.

## Decision 2: moves preserve namespaces

Files move with their declared namespaces unchanged (C# namespaces are independent of project
membership). Consequences, all deliberate:

- Every `FullyQualifiedName`-based filter survives untouched: `~DeterministicFp` in ci.yml,
  `~Golden` in cross-platform-gpu.yml.
- Assembly names DO change, so every `InternalsVisibleTo Include="KhaozEngine.Tests"` grant is
  retargeted to the new owning test project in the same commit as that cluster's move, or dropped
  where compilation proves it dead. The final sweep found 45 csprojs plus one source-level attribute
  carrying the old grant, well past the ~20 guessed here. Of the three `.Render3D` companion grants
  guessed dead in this paragraph, two were refuted: `Particles.Render3D` and `Terrain.Render3D`
  compile internal-facing types into their companion assemblies under base namespaces
  (`ParticleSceneExtensions`, `Scene3DChunkSink`), so wave 3 retargeted them to
  `KhaozEngine.Render.Tests` (`Terrain.Render3D` also picked up `KhaozEngine.Game.Tests` in the Game
  wave). The third, `Telegraphs.Render3D`, was genuinely dead: its one public type (`GroundTelegraphs`)
  needs no internals grant, so the stale grant was dropped, exactly as planned here.
- Every split project pins `<RootNamespace>KhaozEngine.Tests</RootNamespace>`. Two test files load
  embedded resources by their `KhaozEngine.Tests.*` manifest names (Localization coverage, Gui
  patch-notes parser), and manifest names derive from RootNamespace, not AssemblyName. Pinning it
  everywhere keeps resource names and future namespace inference consistent with the preserved
  namespaces.
- xUnit collection definitions are per-assembly. Three collections cross clusters (`AllocSensitive`
  spans five, `ClipboardSerial` and `AmbientLocalization` span Foundation+Gui): each landing
  assembly gets its own thin `[CollectionDefinition]`, with any shared fixture class in
  `TestSupport`. Cross-assembly serialization is not needed because `dotnet test` runs assemblies
  sequentially.

## Decision 3: minimal references per test project

Each new test csproj references ONLY the engine projects its tests use (recon computes the union per
cluster from using directives, compile errors at the green gates catch the misses). This is the whole
point of the split: selection precision comes from honest reference sets. The rump ended with ZERO
`ProjectReference`s instead of the "potentially everything" guessed here: `ArchitectureTests` parses
`*.csproj` XML directly rather than compiling against the projects it checks, so it needs no reference-
graph edge to do its job. That is the opposite of the prediction above: the rump now runs only when its
own files change, not on nearly every selective run, which under-selects it for a csproj-only change
elsewhere that its rules would have caught. Tracked as #212.

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

## As implemented

`KhaozEngine.TestSupport` ended dependency-free: zero `ProjectReference`s, not even the `Primitives`
ceiling planned in Decision 1. `GpuFactAttribute` was the reason a ceiling was planned at all, and it
needs `KhaozEngine.Gpu` (`GpuDeviceContext.CreateHeadless`), so it moved to a third helper project,
`KhaozEngine.TestSupport.Gpu`, discovered mid-implementation rather than planned. That project also
needs `<IsTestProject>false</IsTestProject>`: without it, solution-level `dotnet test` discovers the
xunit-referencing assembly as a test project, spins a testhost against a DLL with no test adapter, and
that testhost crashes, an artifact confirmed unrelated to the split and unchanged by it. No other
helper qualified for `TestSupport` either (the `AllocSensitive` collection markers are empty
`[CollectionDefinition]` classes with no shared state, so each landing assembly just carries its own
trivial copy rather than referencing one), so `TestSupport` itself holds only an anchor class.

Three sanctioned byte-identical duplications, chosen over a cross-test-project reference because each
fixture is small, self-contained, and shared by only 2 clusters, so a reference would cost more
selection precision than the duplication costs in upkeep: `DictionaryCatalog` (`IStringCatalog` test
double, `Foundation.Tests` + `Gui.Tests`), `FakeAppDataEnvironment` (`IAppDataEnvironment` test double,
`Foundation.Tests` + `MapEditor.Tests`), and `GltfTriangleFixtures` (headless SharpGLTF triangle-glb
writers, `Render.Tests` + `MapEditor.Tests`). Each copy carries an in-file comment naming its sibling.

Two known selection imprecisions surfaced after the split, both verified, neither actioned:

- **#211, over-selection.** `KhaozEngine.Render.Tests` references `KhaozEngine.Showcase` for one
  kit-asset test (`DungeonKitAssetTests.cs`), and `Showcase` references `KhaozEngine.Audio` for its
  demo, so an Audio-only change drags the heaviest test project (2,124 tests) into the affected set.
- **#212, under-selection.** The rump ended with zero `ProjectReference`s (see the Decision 3
  correction above), so a csproj-only change elsewhere that `ArchitectureTests` would flag does not
  mark the rump affected, and is caught only at the next full run, not the push that introduced it.
