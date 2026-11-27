# CI Selective Tests Design

The per-area test split shipped in 13.0.3. Program issue: [#207](https://github.com/APKiwiOrg/KhaozEngine/issues/207)
(which also records the rejected alternatives: per-area workflow files, consumer smoke leg).

## Historical baseline

Before the per-area split, a typical push through `ci.yml` took about 4.5 to 5 minutes.
[Run 29624878784](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/29624878784) recorded 22 seconds
for restore, 108 for build, 78 for tests, 27 for determinism and 22 for pack. The monolithic
`KhaozEngine.Tests` project held 5,610 tests, alongside four small satellite projects.
Every push ran the whole suite. Splitting the test assemblies gave CI dependency boundaries
it could use to select the affected tests.

Goals:

1. Push and PR cost (wall clock and billed minutes) proportional to what the diff actually affects.
2. Test layout aligned to the package dependency layering, so "affected" is meaningful and stays
   meaningful as the engine grows (the split is the durable asset, the CI wiring rides on it).
3. Full validation unchanged where it gates publishing: tag runs and the weekly hosted sweep still run
   everything. Since Decision 7 a tag run proves that by citing the successful `workflow_dispatch` run of
   `ci` at its exact commit, and repeats everything itself only when no such run exists.

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
| `KhaozEngine.Render.Tests` | Gpu, Render2D, Render3D, Windowing, Snapshot, Terrain, Telegraphs, ParticlesRender3D, Imaging (owns `Gpu/goldens/`) + `Dungeon/DungeonKitAssetTests.cs` (validates the dungeon kit against a direct copy of the Showcase asset dir, no Showcase project reference, #211). The `Showcase/` folder tests moved out to their own project, see below |
| `KhaozEngine.Gui.Tests` | Gui |
| `KhaozEngine.MapEditor.Tests` | MapEditor, MapEditTool, MapDoc |
| `KhaozEngine.Game.Tests` | Game, Locomotion, Navigation, Dungeon (minus the kit-asset file), Collision, Physics, Objectives, Progression + root collision files |
| `KhaozEngine.Audio.Tests` | Audio, Sfx (Sfx tests the `KhaozEngine.Sfx.Tool` exe, referenced directly) |
| `KhaozEngine.Particles.Tests` | Particles |
| `KhaozEngine.Showcase.Tests` (added by #211) | `Showcase/` (the `ShowcaseStrings` catalog test and the `ShowcaseMenu` widget test). Split out of Render.Tests because it is the ONLY test project that references the `KhaozEngine.Showcase` demo app, which transitively pulls in Audio. Keeping this project tiny is what stops an Audio- or Showcase-adjacent change from dragging the heavy Render.Tests into the affected set |
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
  `TestSupport`. This paragraph originally claimed cross-assembly serialization is unnecessary
  because `dotnet test` runs assemblies sequentially. REFUTED post-ship: on net10, root
  `dotnet test` runs the 16 test assemblies in PARALLEL (proven by interleaved per-assembly output
  in run 29634904849). No collection correctness depended on the wrong claim, but two absolute-time
  consequences did surface, see "As implemented".

## Decision 3: minimal references per test project

Each new test csproj references ONLY the engine projects its tests use (recon computes the union per
cluster from using directives, compile errors at the green gates catch the misses). This is the whole
point of the split: selection precision comes from honest reference sets. The rump ended with ZERO
`ProjectReference`s instead of the "potentially everything" guessed here: `ArchitectureTests` parses
`*.csproj` XML directly rather than compiling against the projects it checks, so it needs no reference-
graph edge to do its job. That is the opposite of the prediction above: the rump ran only when its own
files change, not on nearly every selective run, which under-selected it for a csproj-only change
elsewhere that its rules would have caught. Fixed under #212 by a targeted rule in
Product CI: whenever the diff touches any `*.csproj`, the rump is included in the full
run (see Decision 4). It parses XML, so running it is cheap.

`KhaozEngine.TestSupport` is `IsPackable=false`, ships nothing, and is exempt from the README catalog
and `check-doc-versions.sh` (the guard checks packable projects only, verified during
implementation). Not to be confused with `KhaozEngine.Localization.TestKit`, which is a shipped
package and stays one.

## Decision 4: project dependency scope

Product CI runs on main pushes, pull requests targeting main, the nightly schedule at 03:17 UTC and
manual dispatch. A newer run cancels its pull request's previous run. Pushes and other events each
have their own concurrency group. The pull request checks out GitHub's test merge commit.

Scope compares a pull request with its base and a main push with the last passing run of this workflow
on an ancestor. It considers both paths of a rename. The rules in `.github/ci-scope.txt` follow the
transitive `ProjectReference` directories and preserve path case. A project graph change, linked
source, shared build input, missing or malformed rules, or an unknown path requests a full run.
Project-file changes also run the architecture tests that inspect the solution and project XML (#212).
The build helper checks the segment inventory and rules against the actual graph, including
conditional references. A valid rule set with a stale project mapping fails the check. Files known
to be prose may skip compilation and still run whole-tree convention guards.

Native correctness uses the same scope rules on pull requests and main pushes. The dependency
closures of canonical-solution test projects with device-gated tests are split between two owners.
The native core workflow owns `gpu-core`, because every device fact in `KhaozEngine.Gpu.Core.Tests`
needs the native library that only that workflow builds, so those facts skip on every GPU leg. GPU
selects the rest. Each device test project has exactly one owner. Foliage selects `map-doc` and
`render`, terrain selects `tile-world-physics`, and Windows console selects its two probe compile
roots. The native core workflow selects `gpu-core` and the pack and smoke compile roots. Every full
scope selects all five workflows. Shared assets, the GPU build scripts, the Windows console smoke
script, the C++ sources under `native/` and the golden scene and grid the native core tests read
from `KhaozEngine.Render.Tests` are full overrides, since no project closure reaches them.

The Product CI tooling fixture derives the GPU project roster and checks each workflow's actual
segment inputs, the foliage build list and the projects the native core workflow builds, packs
and runs. The build helper audits closure rows against the project
graph, so an added reference with a stale row fails validation. Pull requests compare with
their own base. On a main push, Scope reads workflow history with a read-only token to find the
last passing ancestor. For GPU, only a passing push or schedule can be the comparison base,
since a dispatch can test a narrower scope. The Scope summary explains a skip before the test
jobs get runners. Weekly full and manual paths run independently of Product CI.

## Decision 5: product and release validation

`product-ci.yml` builds one Release solution on hosted Linux x64. The solution combines selected
test and compile-only roots. A full run includes every canonical solution root and every tracked
project outside it, found by `scripts/outside-solution-projects.py` (#1847). Two selected segments
that share a root compile it once. The temporary test solution uses only canonical test roots, keeps
solution-level parallelism and writes one TRX per assembly. The Release build refuses Debug
outputs. The default filter excludes LiveSocket and Diagnostic. Foundation and FloatingPoint
determinism rerun under tiering 0 and 1 when their assemblies are selected.

Live catalog, account and journal/world-store/commerce tests use the same build and one disposable
SQL Server. Each suite has its own database environment, with separate journal and commerce
databases for the server suite. Each selected filter must execute every fact. The water segment
checks the same frozen differential corpus in both tiering modes on Linux x64 and macOS ARM64,
with one two-root Release build on ARM64. Linux also checks census output isolation against its
already built ordinary physics DLL, a separate census build and a private test package. The
package verifies that the normal DLL stays unchanged and free of census code.

The Linux `Test: tooling` stage runs the script and hook fixtures on full runs and changes that
reach their inputs. Package wrappers and their shared policy scripts force a full run because the
census proof exercises their refusal to pack a census build.

The last job, `CI passed`, covers Scope, Linux and ARM64 and writes the run summary. A passing full
run records its tested tree. A main push or nightly can reuse a trusted, unexpired full pass for
the same tree. A recent conflicting full failure or inconclusive lookup blocks reuse. A reuse or
partial run creates no new full-pass marker. Manual Product CI dispatch always runs the full suite.

`ci.yml` retains the pre-tag dispatch and tag paths. Its release gate uses an exact-commit dispatch
as described in Decision 7. The product tree marker has no role in releasing packages.

## Decision 6: cross-platform-gpu.yml touches paths only

Path gates `KhaozEngine.Tests/Gpu/**` and `KhaozEngine.Tests/Imaging/**` become
`KhaozEngine.Render.Tests/**`. Goldens move to `KhaozEngine.Render.Tests/Gpu/goldens/` (with any
path literals in test code, recon inventories them). Legs, tiers, filters, and the weekly schedule
are unchanged. Full-suite legs run root `dotnet test`, which this doc originally called sequential
per assembly. It is parallel on net10 (see the Decision 2 correction), which has two consequences
found on the 13.0.3 release itself: the dungeon seed-sweep's absolute 60s guard tripped on the
Metal leg under cross-assembly contention (fixed by raising it to 180s, #217 closed), and the
Vulkan one-device-at-a-time serialization is now per-assembly only, so Render.Tests and
MapEditor.Tests device work can overlap on the full path. The first full dispatch over the split
(run 29636574022) passed all three legs, so the overlap is tolerated so far, tracked as lead #218.

## Decision 7: a tag run reuses the hosted pre-tag gate

Added 2026-10-17. The release ritual dispatches `ci` on `main` at the release commit as the hosted
pre-tag gate, then tags that commit. The tag run used to repeat the whole gate (restore, build, full
test, determinism double-pass, the three SQL Server jobs) before pack and publish, at the same commit,
minutes later, with nothing new to learn.

A `prior-gate` job now runs first on tags and dispatches. It calls `scripts/ci-prior-gate.sh
<github.sha>`, which asks the Actions API for `workflow_dispatch` runs of `.github/workflows/ci.yml` at
that exact commit. A run qualifies only when its `head_sha` equals the tag's commit, its event is
`workflow_dispatch`, its status is `completed` with conclusion `success` for the whole run, its `path` is
`.github/workflows/ci.yml` and both `repository` and `head_repository` are this repository. The newest
qualifying run wins. Two green dispatches at one commit ran the same workflow file over the same tree.

On reuse the tag's `build-test-pack` restores, builds, packs and publishes, and skips the full test and
the determinism pass. The three SQL Server jobs skip too. The reused dispatch always runs them, and a
whole-run `success` means each one executed every selected fact, since each fails when any fact
skips. They never gated publish, so on a tag they were evidence only, and that evidence already sits on
the reused run. The only thing a repeat could add is drift in the `2022-latest` service image between
the dispatch and the tag, which tests the environment, not the engine. The `prior-gate` job logs the
reused run's URL, adds a notice annotation and a step summary line, and each skipping job logs the same
URL, so the release report cites it.

The lookup fails closed. A gh or API error, a response that is not a run list, a `total_count` above the
runs returned (a pagination gap), or any returned run outside the query filters means no reuse, and the
tag runs the full sequence as before. The downstream jobs run under `!cancelled()`, so a failed or
skipped `prior-gate` also leaves them on the full path. Several successful dispatches at one commit are
not treated as ambiguous, because they are equivalent proofs. The job holds `contents: read` for its
checkout and `actions: read` for the query, and nothing else, so the read grant is never next to the
publish token.

Reuse cannot chain. Only `workflow_dispatch` runs qualify, and a dispatch never reuses: on a dispatch the
lookup runs with `--report-only`, which always outputs `reuse=false`. That keeps the real query exercised
on every gate run without changing what the gate runs. A wiring bug that makes the script exit 2 fails
`prior-gate` on the dispatch, which blocks the release before any tag exists.

The reuse rule rests on one invariant inside this file: a `workflow_dispatch` of `ci` runs the full
sequence. Matching on `head_sha` means the reused run executed this same `ci.yml`, so the invariant is
checked by reading the file at the tag's commit. An edit that makes dispatches skip work must revisit
this decision. The cost is one short serial job before `build-test-pack` on tags and dispatches. Covered by `scripts/tests/ci-prior-gate.test.sh`
with a stubbed `gh` (match, no match, failed, cancelled and in-progress runs, other event, commit,
workflow or repository, API error, pagination gap, malformed responses, report-only and usage errors).

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

Two known selection imprecisions surfaced after the split, both verified, both now fixed:

- **#211, over-selection (fixed).** `KhaozEngine.Render.Tests` referenced `KhaozEngine.Showcase`, and
  `Showcase` references `KhaozEngine.Audio` for its demo, so an Audio-only change dragged the heaviest
  test project (2,124 tests) into the affected set. The premise that the reference was solely for the
  `DungeonKitAssetTests.cs` kit-asset test was wrong: `Showcase/ShowcaseStringsTests.cs` (internal
  `ShowcaseStrings` via the IVT grant, plus public `ShowcaseApp`) and `Showcase/ShowcaseMenuTests.cs`
  (public `ShowcaseMenu`) also compiled against Showcase. Fix: the two `Showcase/` code tests moved to a
  new tiny `KhaozEngine.Showcase.Tests` project (the only test project that references Showcase now, with
  the IVT grant retargeted to it), and `DungeonKitAssetTests.cs` stayed in Render.Tests but now copies
  the `assets/dungeon/**` kit straight into its own output instead of getting it transitively from the
  Showcase reference. Render.Tests no longer references Showcase, so an Audio change now flags
  {Audio.Tests, Showcase.Tests}, not the heavy Render.Tests.
- **#212, under-selection (fixed).** The architecture tests inspect project definitions independently
  of their project-reference graph. Product CI handles this by selecting full coverage on project-file
  changes.

Release and local test setup use `scripts/build-release-tests.sh`. The explicit outside-solution
`tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj` target owns its offline verifier
process prerequisite. Other solution files acquire that prerequisite only when their resolved roots
include this checkout's actual tooling project. Product CI includes the verifier as a compile-only
root without calling that setup script. `tooling-validation.yml` owns test execution for the renderer
tooling suite. Its `select` job is bounded at five minutes, and its Scope step selects suites
independently from the changed paths. The automatic push and pull request filters are exactly the
selector's inputs, including sources linked by `RendererPerfShared.props`, and the selector fixture
fails when a filter misses an input or lists a path no suite reads. A base commit that is missing
from the checkout selects all four suites. General scripts and hooks select Product CI's tooling
stage. Changes to the tooling workflow itself select all four suites. Manual dispatch and the
reusable prerequisite validate the requested suite. A new commit on a pull request supersedes that
pull request's older run. Main pushes, manual dispatches, reusable calls and reruns use run-specific
groups to preserve each result and protect the renderer prerequisite for paid runs. A rerun stays
out of the pull request group because it keeps its old commit and would cancel the current one.

The same selector routes Items benchmark inputs to the explicit outside-solution
`tools/Items.Benchmark.Tests/Items.Benchmark.Tests.csproj` suite on hosted Ubuntu. Dispatch selects
`items` independently of renderer. The nine retained Items structural and resident-memory instrument
methods stay in Server.Tests. Ordinary engine builds need no Items acceptance or verifier prerequisite.

Navigation capture research has the same outside-solution boundary in
`tools/NavCapture.Research.Tests/NavCapture.Research.Tests.csproj`. The selector's independent `nav`
route runs its three synthetic fixtures on hosted `ubuntu-26.04` when that project, Movement's
AssemblyInfo, any `KhaozEngine.Movement/PhysicsNav*.cs` source, shared build/package/baseline inputs
or the tooling workflow change. The PhysicsNav sources are watched because the snapshot restore
reflects over `PhysicsNavBake`'s private constructor and reads the capture types. Manual dispatch
also accepts `nav`. All three named Release facts must pass with zero skips before a separately
approved capture or restoration campaign. The seven SupportQueryCapture methods and both enabled
and disabled theory rows stay in Movement.Tests and default engine CI. Other production Movement
edits retain their ordinary engine tests without automatically selecting the research suite.

The `coverage` route builds `Coverage.Verify.Tests` once on hosted Ubuntu. The project reference
builds `Coverage.Verify` and makes its executable assembly available to the synthetic CLI process
tests. The workflow checks the inventory and verifies the TRX contains passing process tests and
zero skips.
