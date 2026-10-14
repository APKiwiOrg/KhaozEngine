# Parallel contact navigation bake implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Contact navigation profiles build on several threads with byte-identical output, through concurrent physics readers and a worker-based bake.

**Architecture:** `BepuPhysicsWorld` gets a writer-preferring reader and writer gate with per-operation scratch loans and a separate concurrent-read capability. A contact `BuildProfile` above degree 1 builds per-worker bindings over one build snapshot, runs admission, edges and Stair proofs in row bands in the serial order, and selects the serial failure.

**Tech Stack:** C# .NET 10, xUnit, BepuPhysics 2.4.0.

**Spec:** `docs/design/PARALLEL-CONTACT-NAVIGATION-BAKE-2026-10-14.md` (revision 3, rulings PB-1 to PB-6). Read the ruling a task names before its code.

## Global Constraints

- Bake bytes, legacy and contact, are identical for any degree of parallelism and identical to the pre-change baselines.
- Never widen a tolerance. Every existing physics, lease, support, movement and navigation test passes unchanged.
- A known proper fix is implemented rather than a workaround. A defect outside the task is reported, not fixed in passing.
- No release, tag, game pin, Grimhollow or world edits. No load or repeat runs. Concurrency tests are bounded and event coordinated, with timeouts and full thread cleanup.
- Every build and test runs through `build-slot` (`--heavy` for the full suite). Format check: `dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore`, judged on touched files only (ten unchanged files fail on main, #1387).
- `TreatWarningsAsErrors` stays on. Zero warnings.
- No em dashes, en dashes or prose semicolons in docs, comments or messages.
- Version: v20.39.0 is released. This work cuts 20.40.0, bumped once in Task 2 together with its new `CHANGELOG.md` entry. Later tasks append to that entry. Never edit a released entry.
- Commit subjects use `area(scope): summary`.

## Review Focus

- A read path that still touches shared state under concurrent readers (scratch, collectors, caches, diagnostics). Task 2 pins each query kind under simultaneous readers.
- A deadlock between nested reads and a queued writer. Task 2 pins it with event-coordinated tests.
- Any bake byte that differs by degree, or differs from today. Task 1 pins the baselines and Task 3 compares every degree against them.
- A parallel build that reports a different exception than the serial build. Task 3 pins two-failure orders across phases.
- Native memory left behind by reader pools. Task 2 accounts for every scratch.

---

### Task 1: Pre-change baselines

**Files:**
- Create: `KhaozEngine.Movement.Tests/ContactNavigationBaselineTests.cs`
- Modify (if a fixture helper is the natural owner): `KhaozEngine.Movement.Tests/Fixtures/ContactNavigationFixture.cs`

**Interfaces:**
- Produces: `ContactNavigationBaselineTests` with one pinned normalized full-file SHA-256 per fixture. Task 3 reuses the same builders at every degree.

- [ ] **Step 1: Write baseline tests on the unchanged code.** For each fixture, build through the current public serial API, write the file, normalize the engine identity the way `NavBakeIdentityTests.AllLegacyFullFileGolden` does, and pin the SHA-256. Cover: the low-lip bank, deck over ground, the WadeOnly pool, the rebased origin fixture, a mixed legacy and contact bake, the contact Stair fixture extended to have both accepted and refused candidates, and a new fixture with multiple raw layers, a non-binary cell size, a nonzero origin and at least 9 rows (so later band splits cross edges and links). Each baseline also asserts the graph's raw and resting height bits, layer metadata, open and exit masks, candidate order and accepted link bits match a second fresh build.
- [ ] **Step 2: Run and pin.** `build-slot --label pb-1 -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~ContactNavigationBaselineTests"`. Expected: PASS on unchanged production code. No production file changes in this task.
- [ ] **Step 3: Commit.** `test(navigation): pin contact bake baselines before parallel work`

### Task 2: Concurrent physics readers (PB-1)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryLease.cs`, `BepuPhysicsWorld.cs`, `BepuPhysicsWorld.Queries.cs`, `BepuPhysicsWorld.QueryView.cs`, `BepuPhysicsWorld.SupportFootprint.cs`, `BepuPhysicsWorld.SupportNeighborhood.cs`, `BepuPhysicsWorld.CapsuleContacts.cs`, `BepuPhysicsWorld.CertifiedSweeps.cs`, `InstalledSupportGeometryCache.cs`, `CapsuleMeshContacts.cs` and every other read entry the audit finds
- Create: `KhaozEngine.Physics/IPhysicsConcurrentReadSource.cs`, `KhaozEngine.Physics.Bepu/QueryScratch.cs` (and a pool owner if separate), `docs/verification/2026-10-14-bepu-concurrent-read-audit.md`
- Test: `KhaozEngine.Game.Tests/Physics/ConcurrentReadGateTests.cs`, `ConcurrentReadScratchTests.cs`, `ConcurrentQueryParityTests.cs` (new)

**Interfaces:**
- Produces: `public interface IPhysicsConcurrentReadSource` (marker with the PB-1 contract in its XML documentation), implemented by `BepuPhysicsWorld` and its `QueryView`. `IPhysicsQueryLeaseSource` is unchanged.

- [ ] **Step 1: Write the failing tests.** Two threads hold read leases at once and both query. A queued writer plus a nested query inside an active reader completes. A mutation from a ground or diagnostic callback throws before the generation changes. Wrong-thread disposal is rejected without waiting. A refused acquisition releases everything. A nested second lease on one thread still throws. An explicit lease inside a writer interval throws. Writer-interval reads release scratch at writer exit. A footprint query from a diagnostic observer during an outer footprint query, with different requests and outputs, leaves both correct. Simultaneous readers produce results identical to serial for rays, mesh and compound sweeps, penetration and contact smoothing. Two readers missing one cold installed geometry key construct it once, and mutation, removal and handle reuse invalidate it. Every constructed `QueryScratch` is disposed once with its pool cleared after success, a query exception, a failed acquisition and owner disposal. An unchanged old `IPhysicsQueryLeaseSource` implementer still compiles and works.
- [ ] **Step 2: Run and see them fail.** `build-slot --label pb-2 -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~ConcurrentRead|FullyQualifiedName~ConcurrentQuery"`. Expected: FAIL.
- [ ] **Step 3: Implement PB-1** exactly: gate, scratch loans per operation, logical reset on return, exclusive drain at disposal, installed geometry publication, capability. Write the audit document for the pinned Bepu read paths.
- [ ] **Step 4: Bump the version** to 20.40.0 in `Directory.Build.props`, every declaration `scripts/check-doc-versions.sh` guards, and a new `## 20.40.0` `CHANGELOG.md` entry, in the same commit as the production change.
- [ ] **Step 5: Run.** The new tests, then `KhaozEngine.Game.Tests` filtered to `Physics|Locomotion`, then `KhaozEngine.Movement.Tests`, then `KhaozEngine.Server.Tests` filtered to `Explicit|Physics`. Expected: PASS with no existing test changed.
- [ ] **Step 6: Commit.** `feat(physics): allow concurrent read leases on Bepu worlds`

### Task 3: Contact bake workers (PB-2 to PB-5)

**Files:**
- Modify: `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`, `ContactNavigationProof.cs`, `ContactNavigationEnvelope.cs`, `GroundMoveContext.Contact.cs` (`NavigationFrame`), `GroundNavigationBake.cs`
- Create: `KhaozEngine.Movement/NavBakeExecution.cs`, `KhaozEngine.Movement/ContactNavigationWorkers.cs` (worker set, snapshot, schedule, failure cutoff, shutdown), a scoped diagnostics helper beside the diagnostics owners
- Test: `KhaozEngine.Movement.Tests/ParallelContactBakeTests.cs`, `ParallelContactBakeFailureTests.cs`, `NavBakeExecutionTests.cs` (new), plus Task 1's baseline builders

**Interfaces:**
- Consumes: Task 2 `IPhysicsConcurrentReadSource`, Task 1 baseline builders.
- Produces: `public sealed record NavBakeExecution(int MaxDegreeOfParallelism)`, `PhysicsNavBake.BuildProfile(in MoveTuning, NavAreaFilter, GroundProfileOptions?, NavBakeExecution?)`, `GroundNavigationBake.Create(PhysicsNavBake, NavBakeSources, IReadOnlyList<NavBakeProfile>, NavBakeExecution?)`.

- [ ] **Step 1: Write the failing tests.**
  - Every Task 1 fixture at degrees 1, 2, 3 and 8 matches its baseline hash and graph comparisons.
  - A degree above the row count matches.
  - A water generation change between two workers' first acquisitions, and one after a band completes, refuse the whole build.
  - Two-failure fixtures across layers, raw surfaces and candidate order (the later one failing first in wall time) throw the serial API's exception. Also: a provider exception, a held sibling lease, a setup failure after partial worker construction, and a cleanup failure attached under `Data["CleanupFailures"]`. Each publishes nothing and leaves the caller's world and view usable.
  - A diagnostic-enabled degree 8 build matches degree 1 with merged counts and restored prior thread state.
  - Execution validation: degree 0 (also with an invalid existing argument), null, the existing `null` and `default` option call forms (compile coverage), an all-legacy degree 8 build on an undeclared backend (serial), a mixed build, and a borrowed wrapper view without the capability over a Bepu owner at degree 2 (`NotSupportedException`).
  - Fault providers are schedule-independent.
- [ ] **Step 2: Run and see them fail.** `build-slot --label pb-3 -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~ParallelContactBake|FullyQualifiedName~NavBakeExecution"`. Expected: FAIL.
- [ ] **Step 3: Implement PB-2 to PB-5** exactly, including the serial order definition, the failure cutoff, ordered shutdown, the frame snapshot, validation order and per-worker diagnostics. Degree 1 runs the current serial path unchanged.
- [ ] **Step 4: Run.** The new tests, the Task 1 baselines, all of `KhaozEngine.Movement.Tests`, `KhaozEngine.TileWorld.Physics.Tests`, and the Game height call-site guard. Expected: PASS.
- [ ] **Step 5: Commit.** `feat(navigation): build contact profiles on parallel workers`

### Task 4: Provider audit, cost record, docs and verification

**Files:**
- Modify: provider XML docs in TileWorld (ground samplers, `TileMediumSampler`, the TileWorld registration) as audited, `KhaozEngine.Movement/README.md`, `KhaozEngine.Physics/README.md` or the nearest physics doc, `docs/INDEX.md`, the spec status line, `CHANGELOG.md` (20.40.0)
- Create: `docs/superpowers/plans/proofs/2026-10-14-parallel-contact-bake-cost.md`

- [ ] **Step 1: Provider audit.** Read the TileWorld providers the contact bake calls and confirm they are deterministic and safe for concurrent reads under PB-2. Name each endorsed method in its docs. Any unsafe state is a finding to report, not a silent fix outside scope.
- [ ] **Step 2: Cost record.** One cold and one warm local measurement through `build-slot --heavy` at degree 1 and at the machine's core count, with the PB-5 metrics, the speedup and an updated Hollowmere extrapolation. No loop or repeat run.
- [ ] **Step 3: Docs.** Execution setting, the provider contract, the capability, and the spec and index status (implemented in staged 20.40.0).
- [ ] **Step 4: Full verification once.** `build-slot --heavy --label pb-4 -- dotnet build KhaozEngine.slnx -c Release`, then `build-slot --heavy --label pb-4 -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, the format check, and the five repository guards (`check-dashes`, `check-prose`, `check-file-size`, `check-agent-instructions`, `check-doc-versions`). Expected: zero warnings, zero failures, format failures only in the #1387 files.
- [ ] **Step 5: Commit.** `docs(navigation): record parallel contact bake cost and status`
