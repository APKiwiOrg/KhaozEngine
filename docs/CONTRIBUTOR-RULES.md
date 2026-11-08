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
- A refusal a caller may need to tell apart carries structured members on its exception: a public reason
  enum and the identifiers its docs promise (an entry id, the offending index, a path), filled at every throw
  site. The type belongs to the refusal family that owns the check, a validator or policy, and derives from
  the type thrown before, as `WaterOpticsSurfaceException` derives from `ArgumentException`, so existing
  catches keep working. A refusal for malformed input data whose BCL type would be the sealed
  `InvalidDataException` derives from `MalformedDataException`, and code that handles malformed data catches
  the base. A new refusal in a family adds a reason member at the end of its enum. Public refusal enums are
  append-only from 23.5.0, so existing numeric values never move, because mods and tools built against one engine
  version must keep working. The message stays developer prose
  and is never a contract. A plain parameter check whose `ParamName` and `ActualValue` already name the
  problem stays a BCL exception. An adapter's status projection remains distinct from the lower refusal
  reason. Classifiers use typed reasons and producer context, never keywords inside user identifiers or
  exception prose. A rule that every implementation of a shared contract enforces, such as an
  `IGpu*` command or resource rule, is one family in the contract's package, and every backend throws the
  same reason for the same input. A collection identifier is a read-only snapshot the exception copies when
  it is set, so neither the caller's source nor a cast of the exposed list can change it. Use `ImmutableArray<T>`
  behind the existing `IReadOnlyList<T>` property. Materialize a fresh array before creating each snapshot,
  because `ImmutableArray.CreateRange` can reuse an immutable input's storage and change record equality
  after collection reassignment. A `ReadOnlyCollection<T>` over an array or list is not a snapshot,
  because its `ICollection.SyncRoot` exposes that storage. A change to a throw
  site's type converts every test that asserts the old exact type in the same change, GPU facts included,
  because they skip in a local run and fail only on the GPU legs. So do test projects outside the solution, such
  as those under `tools/`, because only their own workflow runs them. A wrapper that adds context, such as a
  source name, rethrows the inner refusal in its own family with its reason and identifiers and keeps the
  original as the inner exception. It never flattens a known reason into a generic one. When the wrapper and
  the refusal it wraps share one type, one copy method on that type carries the reason and every identifier,
  as `TileWorldException.WithSourceName` does, and a test that sets every public identifier fails by name
  when a new one is not copied. A compound guard selects its reason from the branch that failed, in the
  predicate's own order, and leaves the predicate and message alone. A throw on behalf of a string validator
  takes its reason from the validator's first typed finding, so it converts only once every finding the
  validator can return is typed, and it never stamps a generic reason. A reason names the rule the guard enforces,
  even when an inventory or plan labels it differently, and its name is true on every path that throws it, so a guard
  that runs before a response is classified never reuses a member that claims success. One member keeps one set of
  identifiers at every site, so an integer check and a floating check of the same idea are two members, and a path
  identifier uses one form everywhere, such as a rooted `$.` path. A name identifier is the name the guard reports,
  so a public member that delegates to a guarded member reports and documents the delegate's name. A carrier gets an
  inner-exception constructor only when some site has an inner to pass. A reason that maps to one fixed catalog
  finding code names that code in its docs rather than carrying it as an identifier. An identifier never carries a
  token, a secret, a connection string, key material or a raw response body, and the test that checks a secret is
  absent reads the identifiers and the decoded form of the secret as well as the message. A reason only test code can throw stays off the public
  enum, in a test-only family beside that code. A guard over a producer's or interface's property reads each value
  once, and its predicate, reason and identifiers use that value, because a faulty producer is the case the refusal
  has to describe. A limit identifier is the inclusive limit of the field it names, so a retry at that limit succeeds.
  A catch or status that reads a message is replaced by a reason check only once every thrower behind it is typed,
  keeping every status value and detail text. Code using `MapDocumentFinding.FromException` handles null, because
  consumer code can still throw a refusal with no reason. A custom carrier keeps every public legacy
  constructor for consumers, while a whole-assembly `RefusalConstructorFence` rejects engine calls to those
  constructors, including constructor chaining and provider base forwarding. Each fence has a deliberate
  forbidden-constructor positive control. Legacy chaining uses a nonpublic initialization constructor that
  accepts a nullable reason, never a public unspecified member. Copy completeness tests cover every public
  identifier and owned collection and name any property lost by the copy. An invariant claim must hold for every
  admitted input, including host triggers in the same transaction, host DDL after open and collection interface
  casts. An existing structured refusal keeps its carrier and failure dimensions even at a defensive guard
  believed unreachable. Do not downgrade it to a plain BCL exception. A reader of result streams that host triggers
  can write into checks each result set's shape before reading its columns and proves the stream complete before
  accepting a success outcome. Refusal outcomes keep their historical path. A generated identity comes back through
  an output parameter, never a result set read after DML, because a trigger's result set arrives first.
- A test that reaches a numerical guard, such as a singular matrix, a collapsed cosine or a zero length, never
  depends on floating-point cancellation landing on a threshold or on subnormal inputs. CI runs on x64 and
  arm64, whose fused multiply-add, SIMD and denormal modes differ, so the same input can pass one check on one
  leg and an earlier one on another. Use exact normal float32 values with the arithmetic written out in a
  comment, or a narrow value-taking fault seam beside the guard.
- A test that needs private state uses an internal `<Thing>ForTests` seam beside the type, never reflection.
  A seam returns the narrowest thing the test reads: a value, a count, an identity predicate or token, a
  snapshot copy or a genuine read-only view. It never returns a live mutable collection behind an
  `IReadOnly*` type, because a cast would let a test change engine state, and it returns a whole owner only
  when the test needs that exact object, documented as borrowed (never dispose, never mutate). A test that
  injects a fault calls a narrow internal method named for that fault and taking values. Seams never change
  behaviour.
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
- A new allocation assertion measures through `AllocationRegion` or `AllocationMeasurement` from
  `test-shared`. Its project imports `test-shared/AllocationMeasurement.props`, which also runs the
  assembly under `SerializedReportingTestFramework`, and the test joins a collection whose definition
  sets `DisableParallelization = true`. The region refuses to start in a collection that runs beside
  others. See [`test-shared/README.md`](../test-shared/README.md).
- ULP-sensitive locomotion tests sweep start offsets across a tread or contact boundary. A single
  start phase can hide penetration or stalls. Skip lists derived on arm64 are not portable to x64,
  where SIMD width can change the last bits of a sweep result. Derive expectations from the body
  model and check them across the start phases.
- Geometric stop bounds retain at least half the contact skin before the analytic contact. Requiring
  contact minus the full skin pins backend sweep precision. Any escape from a progress assertion for
  a `Held` start or stall must assert its specific cause, such as the support query's refusal status.
  A zero-progress tick alone does not justify the escape.
- Each concurrently running movement test class owns its `GroundMoveContext`, including flat-world
  fixtures. The context is single threaded and retains mutable step state and cached bindings, so
  separate test classes must not share a static context.
- A GPU test class that captures more than a few images shares one lazily created `Scene3D` through a
  class fixture. It also carries a parity test that ages the shared scene through several
  configurations and compares it with a fresh snapshot scene. `OceanFocusScene` is the reference.
- The test-health ratchet enforces per-file KETEST001 exception-message assertions and KETEST002
  non-public reflection counts in `.testhealth-baseline`. The baseline holds exemptions only, so any new hit
  fails the build. Growth requires owner approval. A legitimate
  wording or structural-reflection contract uses an owner-approved `exempt <rule> <path>` entry with a
  `#` reason above it, never a suppression. Ratchet down with
  `build-slot --heavy --label testhealth-update -- sh scripts/check-test-health.sh --update`.
  KETEST003 is a report-only review aid, outside the baseline. Only 6 of 20 sampled findings in this
  repository were real because loops over local tables and test-owned case helpers look possibly
  empty to a syntactic check. See the [analyzer README](../KhaozEngine.CodeHealth.Analyzers/README.md#test-health-ratchet)
  for rule boundaries, baseline grammar, script modes and consumer adoption. Analyzer tests use
  `KhaozEngine.Tests.CodeHealth`.
- `[Trait("Category", "Diagnostic")]` marks on-demand probes. Default CI excludes their timing and
  cost workloads, including 19 renderer methods with method-level traits. Run them through the
  `cross-platform-gpu` dispatch input `renderTestFilter` set to `Category=Diagnostic`, or narrow it
  with `Category=Diagnostic&FullyQualifiedName~TemporalResolveCostPerfGpuTests`. Existing GPU
  capability gates and software-adapter refusals still apply. Software runs prove correctness, not
  hardware performance. Paid runs retain their separate authorization contract.
- Four renderer checks remain in default CI without Diagnostic traits:
  `ShadowRerecordBenchGpuTests.Compacted_refit_preserves_the_atlas_from_drawing_every_caster`,
  `ShadowRerecordBenchGpuTests.The_hold_releases_on_a_cadence_the_threshold_predicts`,
  `ShadowCascadeCullPerfGpuTests.A_small_caster_fixture_draws_fewer_instances_with_culling` and
  `OceanFftPingPongPerfGpuTests.The_fft_ocean_costs_no_steady_state_gpu_drain`. Atlas equality stays
  byte-exact and the hold cadence retains its 400-frame observation horizon. Cascade draw counts
  and FFT stall counters use small fixed frame bounds, described in
  [`RENDERER-PERFORMANCE.md`](RENDERER-PERFORMANCE.md#default-correctness-and-on-demand-probes).
- CI runs Release. Local `dotnet test` defaults to Debug. Tests around `Debug.Assert`,
  `[Conditional("DEBUG")]` or `#if DEBUG` need a Release run and a configuration-independent
  observable.
- Player-facing text resolves through `StringId` and the localization catalog. Prefer `LocalizedText`
  at GUI sinks. Developer-only output and non-localizable tokens use the explicit raw escape hatch.
- Every engine table records when each row was created, and every table whose rows change after insert
  also records when each row last changed. The store sets both in the statement that writes the row,
  from the clock it stamps its other times with, never through a column default. A column that already
  holds the insert time is the creation time. A migration gives a legacy row an exact time it can prove
  or NULL, never a guess. `journal_snapshot`, replaced whole by compaction, is the one exception. See
  [`design/ROW-TIMESTAMPS-DESIGN-2026-09-28.md`](design/ROW-TIMESTAMPS-DESIGN-2026-09-28.md).

### File-size ratchet

KESIZE001 and KESIZE002 are a design pressure, not a formatting target. Put new behavior in its own
type. Do not split a cohesive file at an arbitrary line. A baseline increase or exemption is the
owner's decision and requires approval. Ratcheting down is free through
`scripts/check-file-size.sh --update`.

An `exempt <path>` entry is for a file whose size grows only when fixed-form data grows. It is not for
a screen, frame loop or test fixture that gains lines with each feature. Inspect history when the
classification is unclear. The detailed rationale lives in
[`design/FILESIZE-ANALYZER-DESIGN-2026-07-20.md`](design/FILESIZE-ANALYZER-DESIGN-2026-07-20.md).

## Writing tests

Every test guards something distinct: a contract, a rule, an owner ruling or a past regression. A correct
change should break few tests, and each failure should point at a real defect. These fifteen rules are the
fleet standard. Every game repo carries the same text as game-template's managed `docs/WRITING-TESTS.md`, so
keep the two in step and change the shared wording in both.

1. **Decide the test is needed, then find the existing one.** Before writing a test, name what it guards (a
   contract, a rule, an owner ruling or a past regression) and search for a test that already fails when that
   breaks. If one does, add nothing, or strengthen that test. If a similar test covers part of the behaviour,
   favour it: extend it, usually with a theory row, or combine the two into one theory. Add a new test only
   for behaviour no test covers. Each theory row covers a distinct branch or boundary, so ten rows that take
   the same path are one row. Sibling facts that differ only in their inputs are one theory. A test that
   guards nothing new costs CI time on every run and breaks on correct refactors without catching anything.
2. **Test the rule, not the value.** Do not copy a tunable constant, palette entry, default or published
   catalog number into an assertion. Such a test fails only when someone retunes on purpose. Assert the
   property the value must keep: a bound, an ordering, a monotonic step, a conservation.
3. **One guard per owner ruling.** An approved look or tuned value gets one test named for its ruling, not one
   assertion per value. A ruling with several behavioural obligations keeps one test per obligation.
4. **Assert the contract, not incidental detail.** Exception message wording, call or allocation counts on
   fakes, log text, internal ordering, private members reached by reflection, exact frame indices and exact
   intermediate values are not contracts unless a document or ruling makes them one. They break on correct
   refactors and catch nothing a behavioural assertion would miss. Assert the exception type and a structured
   member (`ParamName`, an error code, a status) instead, and reach behaviour through the public API or an
   `InternalsVisibleTo` seam. Removing one incidental check never adds another, so do not require an inner
   exception type, wrapper or library namespace unless a document promises it.
5. **Validate every shipped item through one validator.** An invariant over assets, clips, meshes, shaders,
   goldens or catalog rows is written once as a theory with one case per discovered item, plus a fact that the
   discovered set equals the shipped set from an independent source, with an explicit policy for any item it
   exempts. It never loops over items inside one fact, because the first failure hides the rest, and it never
   checks a representative sample, because a defect in one item is invisible in another.
6. **Use the shared fixtures.** Fakes, host boot, waits, content reads and comparison helpers live in shared
   fixtures. Extend one instead of writing a local copy, because copies drift and each needs its own fixes.
7. **Name the regression.** A test written for a defect names its issue number in a comment.
8. **Exact values where they are the contract.** Wire and persisted formats, determinism hashes, public
   constants, documented tuning values and golden images are compared exactly. There the exact value is the
   behaviour, and a looser check lets a format break ship. Compare a documented limit to its literal, never to
   the production constant that produced it, which moves with the code under test.
9. **Absence checks and documented diagnostic values stay.** `Assert.DoesNotContain` or `DoesNotMatch` on a
   message pins what it must never say (a token, a secret, a retired claim), which is a disclosure contract. A
   diagnostic value the README, consumer docs or a member's XML docs promise (an entry id, the offending index,
   a count, a path) is the contract, and only the prose around it is a wording pin. Check those docs before
   removing any message assertion.
10. **A test must be able to fail.** Assert the expected count or non-emptiness before asserting inside a loop
    or `Assert.All`, because an empty result passes silently. Never assert a fake's configured answer, a
    constant the test itself defined, or a golden generated in the same run it is compared against. When an
    assertion goes, keep the call it made if it is the only exercise of a production path, and assert a
    property of its result. A no-write assertion needs a fixture that would leave a write if the rollback were
    missing, such as a side table a trigger fills before it discards the row.
11. **Never delete or weaken a test without evidence.** A deletion names, in its commit message, the surviving
    test that fails for the same defect, or shows why the deleted test could not fail. Removing an
    over-specific line keeps or strengthens the behavioural assertion beside it. A failing test is fixed or its
    defect is filed, never loosened until it passes.
12. **Timing never gates default CI.** A test that measures and reports instead of asserting carries
    `[Trait("Category", "Diagnostic")]`, and default CI excludes that category. Wall-clock bounds flake on
    shared runners and say nothing about correctness.
13. **No sleeps, retries or bigger budgets for flakes.** A test that fails intermittently gets an issue when it
    is first seen and a deterministic fix: a completion signal, a held clock or a serial collection. A sleep or
    retry hides the race and slows every run. Hold every clock the code under test reads, because one left on wall
    time can still decide the outcome, and check a time budget one tick short of its limit as well as at it.
14. **Tooling tests live with their tooling.** Tests of measurement harnesses, evidence pipelines and research
    tools sit in their own project outside the main test project, with their own CI trigger, so product changes
    do not pay for them.
15. **A refusal test breaks one rule.** A test that expects a refusal feeds an input that breaks only the rule
    under test, beside a passing control that fixes that one defect. An input that breaks two rules still
    passes when the wrong check refuses it, because the exception type cannot tell the refusals apart. When no
    input can break one rule alone, assert the structured member that names the rule. A rule with a limit gets
    its control at the limit, such as a route of exactly the cap or the last plane the world has, so a check
    tightened by one fails. The control follows the refusal's no-write assertions, so they still prove the
    refusal wrote nothing.
    On a write path, compare content, such as saved bytes or a digest, rather than an object reference or a count.
    A budget control runs the real operation at the exact limit and one below it, not an isolated guard helper. A
    tiled save commits at its manifest rename, so a refused save leaves the manifest and loaded content unchanged,
    and a content-addressed file it wrote first is unreferenced until the next save sweeps it.

In this repository:

- KETEST001 and KETEST002 enforce rules 4 and 9 as build errors against `.testhealth-baseline`, and
  KETEST003 lists rule 10's possibly empty loops in report mode only (see the "Test-health ratchet" bullet
  above and `KhaozEngine.CodeHealth.Analyzers/README.md`). KETEST001 cannot tell a documented diagnostic
  value from prose, so a rule 9 value moves onto a structured member of the thrown exception (the refusal
  bullet under "Engine code and test contracts") and the test asserts that member. It is never a deleted
  assertion and never an exemption.
- Rule 12's category is excluded by every default filter (`scripts/ci-selective-test.sh`, `ci.yml`,
  `cross-platform-gpu.yml`). Run it on demand with the cross-platform-gpu `renderTestFilter` input set to
  `Category=Diagnostic`. Issue #491 is the precedent: a wall-clock bound that flaked on a loaded shared runner.
- Rule 14's reference is `tools/Renderer.Perf.Tooling.Tests`, outside `KhaozEngine.slnx`. It runs on the free
  `tooling-validation.yml` workflow when its own inputs change, never in the engine full suite. `tools/Items.Benchmark.Tests` and
  `tools/NavCapture.Research.Tests` follow the same pattern.
- Rule 6's shared helpers include `test-shared` (allocation measurement, serialized reporting) and each area
  test project's fixtures. Process-global state still needs a `DisableParallelization` collection (see
  above).

## Dependencies and package structure

The authoritative package and umbrella catalog is [`PACKAGES.md`](PACKAGES.md), one row per package
linking that package's own README. Its `Depends on` column is curated, so the exact edges are each
csproj's `ProjectReference` set and each umbrella csproj. A new package adds its row there, in the area
it belongs to. Do not maintain another package list here or in the root README.

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
sh scripts/build-release-tests.sh KhaozEngine.slnx
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket&Category!=Diagnostic"
```

The shared Release setup builds the selected engine target without renderer tooling prerequisites.
Renderer measurement implementation and tests live in the outside-solution
[`Renderer.Perf.Tooling.Tests`](../tools/Renderer.Perf.Tooling.Tests/README.md) project. No engine project
references it. Build it explicitly with
`sh scripts/build-release-tests.sh tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj`
before its `dotnet test -c Release --no-build` run. This target, and foreign or reduced solutions whose
resolved roots include this exact project, also build the offline verifier required by process tests.
Canonical project selection uses actual file identity, never a basename match. Ordinary engine CI and
native engine shards do not build or discover the tooling suite. A csproj-only edit still selects the
architecture rump when dotnet-affected reports no affected roots. `tooling-validation.yml` owns its free
three-OS validation and the prerequisite gate for separately authorized renderer campaigns.

The outside-solution [`Items.Benchmark.Tests`](../tools/Items.Benchmark.Tests/README.md) project owns
the process-memory-sensitive Items quick acceptance fact. Its only direct reference is Benchmarks.
Five Items structural methods and four resident-memory instrument methods remain in Server.Tests.
The free tooling workflow selects suite `items` on hosted Ubuntu and requires the named Release fact
to pass with zero skips in its own process. Require a passing run at the exact benchmark code commit
before publishing new Items evidence, recording its run URL and code SHA beside that evidence.
This validation does not launch or authorize a full benchmark campaign.

The outside-solution [`NavCapture.Research.Tests`](../tools/NavCapture.Research.Tests/README.md)
project owns three synthetic navigation capture and snapshot restoration fixtures. The free tooling
workflow selects suite `nav` on hosted Ubuntu and requires all three named Release facts to pass
with zero skips before a separately approved research campaign. The seven SupportQueryCapture methods
remain in Movement.Tests and default engine CI. Fixture validation authorizes no capture campaign.

The ratchet report build is a separate maintenance inventory. `check-test-health.sh` sequentially
builds the engine solution, then every tracked project with an xunit package reference that the solution
does not list, before lowering any allowance. It finds those projects itself because the analyzer reaches
every project under `Directory.Build.props`. A hand-kept list once missed
`KhaozEngine.MapDoc.OracleHarness.Tests`, which then failed its own build with no allowance. Linked files
retain their canonical physical repository path and findings use the maximum per-source count across
assemblies. This build inventory adds no tooling test execution to engine CI.

Renderer workflow contract tests parse checkout text through `RendererPerfWorkflowText`, which accepts
LF and CRLF before extracting jobs or embedded commands. Exact-byte inputs keep their LF rules in
`.gitattributes`, including renderer control shaders and authorized manifests. A verbatim or raw C#
literal takes its file's checkout line endings, so a test file whose multiline literals are compared
byte for byte with an LF-pinned source is pinned too and joins the `RendererPerfIdentityCheckoutTests`
inventory described in [`RENDERER-PERFORMANCE.md`](RENDERER-PERFORMANCE.md). Fixture directories use
`RendererPerfFixtureDirectory` to clear read-only regular files such as Git objects and remove directory
links nonrecursively. Cleanup never follows reparse points or changes their targets' attributes.
Windows selector fixtures resolve the console Bash from the same Git installation's `--exec-path`,
avoiding the System32 WSL launcher. Entrypoint fixtures store canonical host paths before changing the
working directory, so exact identity assertions retain their meaning with Git-style separators.

Warnings are errors in every configuration. Fix warnings at the source. Do not add a blanket
suppression or disable `TreatWarningsAsErrors`. The standing XML documentation suppression is the
narrow exception.

The default CI filter is `Category!=LiveSocket&Category!=Diagnostic`. LiveSocket tests need a live
network peer, and Diagnostic tests report on demand rather than gate. A `.testhealth-baseline` change
forces a FULL selective CI run.

Ordinary pushes and pull requests run `scripts/ci-selective-test.sh`. Manual runs use the full
restore, Release build, test, determinism and pack path plus the three SQL Server jobs, and never
publish. A tag push reuses the hosted pre-tag gate when `scripts/ci-prior-gate.sh` finds a successful
manual run of `ci` at the tag's exact commit. It then restores, builds, packs and publishes, skips the
full test, the determinism pass and the SQL Server jobs, and cites the reused run's URL. Without such a
run, or on any lookup doubt, the tag runs the whole manual path and publishes. Only tag pushes publish.
Convention, instruction budget and doc-version checks run before both paths. See
[`design/CI-SELECTIVE-TESTS.md`](design/CI-SELECTIVE-TESTS.md).

The whole-tree convention checks are `scripts/check-dashes.sh`, `scripts/check-prose.sh`,
`scripts/check-file-size.sh`, `scripts/check-agent-instructions.sh`, `scripts/check-scratch.sh` and
`scripts/check-home-paths.sh`, each run with `--tree`. The pre-commit hook runs the staged checks.
The home-path guard checks added staged lines by default and all tracked text with `--tree`, including
escaped Windows paths and memory project identifiers. Use repo-relative paths, or `~/` for paths
outside the repo. Generic `runner`, `Shared` and angle-bracket placeholder names pass. Exceptional
synthetic fixtures are listed by pathspec in `.home-paths-ignore`, one per line with blank lines and
`#` comments ignored. Keep exceptions narrow. Staged checks read the exception list from the index
so an unstaged exception cannot hide a path that the commit would ship. Run the guard's tests with
`sh scripts/tests/check-home-paths.test.sh`.

Never repeat tests in a loop or add CPU load on the dev Mac, which also hosts the org's self-hosted runners.
Flake hunts and stress proofs run through `.github/workflows/stress-test.yml`, a caller of the org's shared
workflow in `APKiwiOrg/ci-workflows`, on a GitHub-hosted runner, and only with the owner's explicit
permission first. See that repository's README for its inputs.

The non-gating hosted CPU rebase diagnostic is scheduled against main and dispatched against any ref. Its
measurement, fixed 3x verdict and deterministic suite split are documented in
[`RENDERER-PERFORMANCE.md`](RENDERER-PERFORMANCE.md#hosted-physics-rebase-diagnostic).

The engine's GPU matrix uses hosted `metal-native`, `direct3d11-native` and `vulkan-native` legs.
Goldens are backend-family artifacts. A new or changed golden must be baked by the relevant CI leg.
Plain `dotnet test` skips GPU facts. A local GPU proof sets `KE_GPU_TESTS=1` and confirms zero skipped
render tests. [`CROSS-PLATFORM.md`](CROSS-PLATFORM.md) owns the full matrix contract.

`terrain-format.yml` is a separate headless portability gate on hosted `ubuntu-26.04`, `macos-26` and
`windows-2025`. It loads the committed Mac-baked KENB 4 fixture, reconstructs every accepted terrain
certificate and requires a byte-identical rewrite. Every main push and pull request runs the gate.
Manual dispatch supports a task branch. It never uses a self-hosted runner or requires a GPU.

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
6. Build and test in Release. Use an explicit private `KHAOZENGINE_FEED` for any pre-merge pack proof.
7. Commit, reconcile with current `main`, verify again, merge and push `main` through the owning
   orchestrator.
8. Run `scripts/pack-local-feed.sh` from `main` after `origin/main` contains that commit. Never write
   released bytes with a bare pack command.

One related batch gets one version bump. Item commits can remain separate, followed by one release
commit. Commit subjects use `area(scope): summary`. A release commit uses the new version as scope.

Packing is cumulative and occurs on every package-bearing finish. `scripts/pack-local-feed.sh` refuses
to overwrite a released version unless the tree is exactly that clean tagged commit. It also refuses
to write the shared feed until HEAD is an ancestor of current `origin/main` and the tree is clean. A
`KHAOZENGINE_FEED` that resolves outside the shared feed is the private path for a deliberate branch
build. Once those guards pass, the wrapper packs through a fresh sibling directory and promotes only the
guarded current version's generated `nupkg` and `snupkg` files, then removes same-version files the fresh
pack no longer generates. A repeated pack refreshes stale outputs while leaving every other version
untouched. The pack standard lives in
`scripts/pack-standard.sh` and is covered by
`scripts/tests/pack-local-feed.test.sh`. Run `scripts/check-local-feed.sh --version X.Y.Z` for the
version a consumer is about to vendor. A release checks its own version with `--current`. With no
scope the report walks every version in the feed.

The pack builds the current tree, worktree or not, and writes to the main checkout's `local-feed`,
which is the feed consumers read. `scripts/check-local-feed.sh` reads the same feed and flags staged
package commits that are not on current `origin/main`. Setting `KHAOZENGINE_FEED` moves both to another
directory. A relative value resolves against the root of the tree the script runs in. A linked
worktree still keeps its own empty `local-feed`, because `nuget.config` names it as a restore source.

A release tag is separate and user-started. Create it with `scripts/tag-release.sh`, which reads the
version and creates the canonical annotated message. Do not hand-create a tag. The only automatic tag
exception is when a consumer is explicitly pinned and waiting on this engine change. In that case the
dependency is already blocked, so the engine release completes without another prompt.

`local-feed` is a gitignored development convenience. GitHub Packages is the durable release store.
Prune the local feed only above the lowest version still pinned by a consumer.

A release holds the release lock from the moment it fixes its anchor until the tag is pushed or the
release stops. Run `scripts/release-lock.sh acquire vX.Y.Z` with `KE_RELEASE_LOCK_OWNER` set, and
`scripts/release-lock.sh release` at the end. Every other session checks
`scripts/release-lock.sh status` before merging into or pushing `main`, and waits while a lock is held.
The engine hooks refuse moving or pushing `main` for anyone but the holder. A lock older than six hours
is stale. The lock lives in the git common directory, so one lock covers every worktree on the machine.
The hooks resolve per worktree because `core.hooksPath` is the relative `.githooks`. A worktree
branched before the lock landed lacks the guard until it merges `main`, which is why the rule binds
every session in writing as well.

The pre-tag gate is hosted. Dispatch `ci` and `cross-platform-gpu` on `main` at the release commit and
wait for both. The `ci` dispatch runs the full restore, build, test, determinism pass, pack and SQL
Server jobs without publishing. The `cross-platform-gpu` dispatch with default inputs runs the full
suite on every backend. Tag that same commit. The tag run finds the successful `ci` dispatch through
`scripts/ci-prior-gate.sh`, skips the tests it already passed, then packs and publishes. The tag run's
`prior-gate` job logs the reused run's URL, which the release report cites as the test evidence beside
the tag run itself. A tag run that logs no reuse ran the full suite again, which is correct but means
the gate dispatch was missing, red or at another commit.
Locally run only the whole-tree guards and `scripts/pack-local-feed.sh`. Do not repeat the full suite
on the dev Mac. A push to `main` cancels an in-flight `ci` dispatch because both share the `ci`
concurrency group for `main`, and the release lock prevents that push. The `cross-platform-gpu`
concurrency group includes the event name, so only another dispatch on `main` cancels its dispatch.

A tracked flake does not block a release when all of these hold. Every failing test is named on an
open issue that tracks it as intermittent. Those tests and the code they exercise are unchanged since
the previous release tag. Every other test and leg of the gate passes. The release report records the
issue, the failing run and that comparison. Any other failure blocks the release, and a waiver never
covers a test that is new or whose code changed in the release.

## Documentation governance

Each fact has one canonical source:

- Package inventory, umbrella membership and one-line package summaries: `docs/PACKAGES.md`.
- What the engine is, for a newcomer: root `README.md`, which links out rather than restating package detail.
- Consumer use and public API examples: `docs/USING-KHAOZENGINE.md`.
- Dependency edges and backend seams: `docs/DEPENDENCY-SEAMS.md`.
- Per-package quick reference: that package's own `README.md`, shipped inside its nupkg.
- Release history: root `CHANGELOG.md`.
- Design rationale: `docs/design/` with a row in `docs/INDEX.md`.
- Open work: GitHub Issues.

Every feature, bug fix and API change gets a full doc sweep. Search the added or removed type,
package, flag and behavior across every Markdown file, package README and `AGENTS.md`. Correct stale
descriptions, not only version declarations. On macOS `git grep -E` does not honour `\b`, so a sweep
pattern using it matches nothing and reports clean. Use `git grep -w` or `git grep -P` there. `scripts/check-doc-versions.sh` verifies package catalog
coverage, package readmes, example versions and the newest changelog heading. It cannot verify prose
accuracy.

A completed design document keeps the rationale still in force. Rewrite superseded reasoning in
place and keep shipped API and use instructions in the living references. Any deferral left when a
program completes becomes an issue. Root `docs/` contains living references only. Long-running work
lives in the owner's program store, see [Programs](programs/README.md). Specs, plans and execution
ledgers stay in ignored local scratch.

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
