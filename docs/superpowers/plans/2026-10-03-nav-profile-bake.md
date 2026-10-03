# Baked Ground Navigation Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist physics-checked `GroundNavigation` profile sets in a versioned binary file that loads without physics and refuses stale inputs, remove per-proof allocation from `BuildProfile`, and add a route-free `DirectMoveToRange` approach driver.

**Architecture:** Movement owns a little-endian `KENB` container with a canonical identity block and a checksummed payload holding one capture's columns and each profile's grids, traversal layers and links. Navigation gains one public factory that rebuilds a surface grid from a stored blocked mask. The allocation fix reuses Bepu overlap scratch, one footprint predicate and one dry context. The direct driver shares extracted range helpers with `MoveToRange` and latches a typed `Blocked` status from tick-counted progress windows.

**Tech Stack:** C# on .NET 10, xUnit, BepuPhysics in tests, `BinaryPrimitives`, `IncrementalHash` SHA-256.

**Spec:** `docs/design/NAV-PROFILE-BAKE-DESIGN-2026-10-03.md`. Read it whole before Task 1. Sections are cited as S1 to S11 and decisions as D1 to D9.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/grimhollow-nav-bake`, branch `feature/grimhollow-nav-bake`, from engine main `8dfe93941`. Reconcile with current `origin/main` before the final verification.
- Another live session lands backlog work on engine main. Never touch its worktrees or the main checkout.
- No project reference changes. Movement references exactly Locomotion, Navigation and Physics. Hashing and encoding use only base library types.
- Little-endian through `BinaryPrimitives`. Floats stored and compared as IEEE 754 bits. Magic `KENB`, format version 1, flags 0, identity block at most 1 MiB.
- Labels and names: 1 to 64 characters from `a-z`, `0-9`, `.`, `_`, `-`, `/`. At least one source. 1 to 256 profiles. `MaxSurfacesPerColumn` at most 255 for a bake.
- Identity excludes exactly `WalkSpeed`, `RunSpeed` and `AirMomentum` from `MoveTuning`, and retains the other 27 fields.
- No analytic fallback and no fresh-build fallback inside `Load`. Content problems return a status, stream I/O errors propagate.
- Direct driver thresholds are required options with no engine defaults. Grimhollow R13 values (15 ticks and 0.1 m, 45 ticks and 0.1 m) appear only in tests.
- Additive minor change. Ruling O2.26: the staged patch `20.18.1` becomes `20.19.0` at integration with its entry folded in, and the delta reliability work rides the same `20.19.0`. Root performs it. No worker bump, tag, pack or push of main.
- One building worker at a time. Focused tests per task, one full Release verification at the finish. No local repetition, stress runs or client launches.
- Zero warnings. No `.filesize-baseline` growth. Put each new concern in its own file. Test namespaces under `KhaozEngine.Tests.*`. Allocation tests join the assembly's `AllocSensitive` collection.
- No em dashes, en dashes or prose semicolons in Markdown or comments. Record every departure, its reason and its cost in this plan's Outcome.

Before any restore in this worktree:

```sh
mkdir -p /Users/antonio/KhaozEngine/.worktrees/grimhollow-nav-bake/local-feed
```

## Ownership Map

| Area | Files | Task |
| --- | --- | --- |
| Bepu query scratch | `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.Queries.cs` | 1 |
| Proof allocation | `KhaozEngine.Movement/NavAreaFootprint.cs`, `PhysicsNavBake.Profiles.cs`, `GroundMoveContext.cs`, `GroundTraversalProbe.cs` | 1 |
| Grid factory | `KhaozEngine.Navigation/NavGrid.cs` (made partial), new `NavGrid.BlockedSurfaces.cs` | 2 |
| Bake identity | new `KhaozEngine.Movement/NavBakeSources.cs`, `NavBakeProfile.cs`, `NavBakeExpectation.cs`, `NavBakeLoadStatus.cs` (enum only), `NavBakeIdentity.cs`, `NavBakeIdentity.Tuning.cs`, `NavBakeBinary.cs` | 3 |
| Bake format | `KhaozEngine.Movement/NavBakeBinary.cs` (created in Task 3), new `NavBakeLoadResult.cs`, `GroundNavigationBake.cs`, `GroundNavigationBake.Writer.cs`, `GroundNavigationBake.Reader.cs`, internal accessors in `GroundNavigation.cs` and `NavAreaFootprint.cs` | 4 |
| Bridge proof | new `KhaozEngine.TileWorld.Physics.Tests/TileWorldNavigationBakeTests.cs`, helper visibility in `TileWorldMovementNavigationTests.cs` | 5 |
| Direct driver | new `KhaozEngine.Movement/RangeApproachCore.cs`, `DirectApproachOptions.cs`, `DirectMoveToRange.cs`, `DirectMoveToRange.Progress.cs`, modified `MoveToRange.cs`, `MoveToRange.Approach.cs`, `RangeMoveStatus.cs` | 6 |
| Server acceptance | `KhaozEngine.Server.Tests/NetWorld/PlayerRangeMovementTestRig.cs`, new `PlayerDirectApproachAcceptanceTests.cs` | 6 |
| Living docs and release | `KhaozEngine.Movement/README.md`, `KhaozEngine.Navigation/README.md`, `docs/USING-KHAOZENGINE.md`, root `README.md` Movement summary, `CHANGELOG.md`, `docs/INDEX.md`, the design status line | 7 |

Tasks 1, 2 and 6 are independent. Task 4 needs 1, 2 and 3, and follows Task 1 because both edit `NavAreaFootprint.cs`. Task 5 needs 4. Task 7 runs last. Root owns integration, version selection, push, pack and tag.

## Review Focus

1. A bake written on ARM64 and loaded on x64 must load when its authored inputs are equal. Task 3 `IdentityBytesMatchTheGoldenFingerprint` pins identity bytes to contain no architecture-dependent data.
2. A caller that reuses or mutates its `NavBakeSources` after `Create` or `Load` must not change the stored identity. Task 4 `SourcesAreSnapshotAtCreateAndLoad`.
3. A file cut short by an interrupted copy must come back as a status, never an exception. Task 4 `EveryTruncationIsCorruptOrNotABake`.
4. A stream that returns short reads, such as a decompressing or network stream, must load the same bake. Task 4 `LoadHandlesOneByteReads`.
5. A client that expects only the profiles it uses must be told which profile is missing or extra, not just that loading failed. Task 4 `SubsetExpectationIsRefusedNamingTheMissingProfile`.

---

### Task 1: Remove per-proof allocation

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.Queries.cs:71-133` and `:146-152`
- Modify: `KhaozEngine.Movement/NavAreaFootprint.cs`, `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs:80`, `KhaozEngine.Movement/GroundMoveContext.cs`, `KhaozEngine.Movement/GroundTraversalProbe.cs:28-30`
- Create: `KhaozEngine.Game.Tests/AllocAssert.cs` (copy of `KhaozEngine.TileWorld.Tests/AllocAssert.cs`, namespace `KhaozEngine.Tests`)
- Create: `KhaozEngine.Movement.Tests/AllocAssert.cs` and `AllocSensitiveCollection.cs` (copies of the TileWorld.Tests pair)
- Test: `KhaozEngine.Game.Tests/Physics/PenetrationAllocationTests.cs`, `KhaozEngine.Movement.Tests/ProfileAllocationTests.cs`

**Interfaces:**
- Produces: `internal Func<Vector3, bool> NavAreaFootprint.AcceptsPredicate { get; }`, created once in the constructor.
- Produces: `internal GroundMoveContext GroundMoveContext.DryContext { get; }`. Returns `this` when `Medium` is null. Otherwise lazily creates and keeps one context from the six-argument constructor with the same `GroundHeight`, `GroundNormal`, `Physics`, `ClampXz`, `MovementQueries` and a null medium.
- Produces: `BepuPhysicsWorld` private `List<CollidableReference> _overlapScratch`, cleared per penetration call. `OverlapCollector` takes the list in its constructor.

- [ ] **Step 1: Write the allocation and behavior tests.** All classes carry `[Collection("AllocSensitive")]`.

```csharp
// PenetrationAllocationTests
[Fact] public void WarmedPenetrationAllocatesNothingPerCall()
// Floor box and wall box, capsule overlapping both. Three warm calls, then:
AllocAssert.NoPerCallAllocation("ComputePenetration", () => { for (int i = 0; i < 100; i++) world.ComputePenetration(capsule, pose, out _); });

[Fact] public void WarmedViewPenetrationAllocatesNothingPerCall()   // same through CreateQueryViewExcludingStatics(floor)

[Fact] public void ConsecutiveQueriesDoNotLeakCandidates()
// Query into the wall, then in open air (false, mtv default), then into a second static.
// The third result equals a fresh single-static world's result for the same capsule and pose.

// ProfileAllocationTests
[Fact] public void WarmedMediumEdgeProbeAllocatesNothingPerCall()
// GroundTraversalProbeTests.FlatWorld(), context with a medium delegate returning dry, Func<Vector3,bool> accepts = _ => true created once.
// Warm three TryEdge calls (0,0,0) to (0.25,0,0), then NoPerCallAllocation over 50 calls.

[Fact] public void DryContextIsCachedAndCarriesQuerySelection()
Assert.Same(context.DryContext, context.DryContext);
Assert.Null(context.DryContext.Medium);
Assert.Same(context.MovementQueries, context.DryContext.MovementQueries);
Assert.Same(noMedium, noMedium.DryContext);

[Fact] public void BuildProfileStaysWithinOneKibPerColumn()
// Flat box half extents (9, 0.1, 9). Options bounds [-8, 8) on X and Z, cell 0.25, so 4,096 columns.
// Warm with one BuildProfile on the 3 by 1 PhysicsNavProfileTests-sized fixture, then measure one BuildProfile.
Assert.True(allocated <= 4L * 1024 * 1024, $"BuildProfile allocated {allocated} bytes for 4096 columns");
```

- [ ] **Step 2: Run RED.**

Run: `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~PenetrationAllocationTests"`
Expected: both allocation facts FAIL with a nonzero byte count. `ConsecutiveQueriesDoNotLeakCandidates` passes.

Run: `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~ProfileAllocationTests"`
Expected: compile FAIL with CS1061 for `DryContext`. Comment out that one fact, rerun, and record the probe and `BuildProfile` byte counts in Outcome as the baseline. Restore the fact.

- [ ] **Step 3: Implement the three changes** listed in Interfaces. `PhysicsNavBake.Prove` passes `footprint.AcceptsPredicate`. `TryEdge` uses `context.DryContext`. No change to candidate order, exclusions, batching or the deepest-contact rule.

- [ ] **Step 4: Run GREEN** with both commands above plus the adjacent suites.

Run: `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.Physics"`
Run: `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release`
Expected: all pass with nonzero counts. If `BuildProfileStaysWithinOneKibPerColumn` still fails, record the measured bytes and the top allocation sites from one `dotnet-trace` or `dotnet-counters` capture in Outcome, file an issue with `scripts/ledger.sh`, and stop for root. The same applies when either zero-allocation fact still fails. Do not edit `CharacterMovement`.

- [ ] **Step 5: Commit.** `perf(movement): stop allocating per ground traversal proof`

---

### Task 2: Rebuild a surface grid from a stored blocked mask

**Files:**
- Modify: `KhaozEngine.Navigation/NavGrid.cs:19` to `public sealed partial class NavGrid`
- Create: `KhaozEngine.Navigation/NavGrid.BlockedSurfaces.cs`
- Test: `KhaozEngine.Game.Tests/Navigation/NavGridBlockedSurfacesTests.cs`

**Interfaces:**
- Produces: `public static NavGrid FromBlockedSurfaces(int width, int height, float cellSize, float originX, float originZ, ReadOnlySpan<bool> blocked, ReadOnlySpan<float> heights, float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity, float yawRadians = 0f)`

- [ ] **Step 1: Write the tests.**

```csharp
[Fact] public void RebuildsEveryCellOfASurfaceGrid()
// FromSurfaces 7 by 5, cell 0.25, origin (-1, 2), yaw 0.3, band [0, 4], with a step-blocked standable cell,
// a low-headroom cell and a non-standable cell. blocked[i] = ClearanceAt == 0, heights[i] = SurfaceHeightAt ?? 0f.
// For every cell: ClearanceAt equal, SurfaceHeightAt equal as bits or both null.
// CellSize, OriginX, OriginZ, YawRadians, YMin, YMax, HasSurfaceHeights equal.

[Theory] public void RejectsMismatchedSpansAndNonFiniteOpenHeights(string fault)
// "blocked-short" and "heights-short" throw ArgumentException. NaN or infinity on an open cell throws
// ArgumentOutOfRangeException. NaN on a blocked cell is accepted. Width, height, cell size and yaw follow FromSurfaces.

[Fact] public void CopiesCallerSpans()          // mutate both arrays after the call, grid unchanged
[Fact] public void AllBlockedGridHasNoSurfaces() // every SurfaceHeightAt null, HasSurfaceHeights true
```

- [ ] **Step 2: Run RED.** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~NavGridBlockedSurfacesTests"`. Expected: compile FAIL, `FromBlockedSurfaces` missing.
- [ ] **Step 3: Implement** with the private constructor and `ClearanceTransform.Compute(blocked.ToArray(), width, height)`.
- [ ] **Step 4: Run GREEN** with the same filter, then `--filter "FullyQualifiedName~KhaozEngine.Tests.Navigation"`. Expected: all pass.
- [ ] **Step 5: Commit.** `feat(navigation): rebuild surface grids from a stored blocked mask`

---

### Task 3: Canonical bake identity

**Files:**
- Create: `KhaozEngine.Movement/NavBakeSources.cs`, `NavBakeProfile.cs`, `NavBakeExpectation.cs`, `NavBakeLoadStatus.cs` (the enum only), `NavBakeIdentity.cs`, `NavBakeIdentity.Tuning.cs`, `NavBakeBinary.cs`
- Test: `KhaozEngine.Movement.Tests/NavBakeIdentityTests.cs`

**Interfaces:**
- Produces, public, exactly as design S5: `NavBakeSources` (`Add`, both `AddHashOf`, `Labels`), `NavBakeProfile`, `NavBakeExpectation`, `NavBakeLoadStatus`. `NavBakeLoadResult` references `GroundNavigationBake`, so it belongs to Task 4.
- Produces, internal:
  - `static string NavBakeIdentity.CurrentEngineVersion { get; }`, from the Movement assembly's `AssemblyInformationalVersionAttribute` through `NormalizeEngineVersion`.
  - `static string NavBakeIdentity.NormalizeEngineVersion(string informational)`, which removes any `+` suffix.
  - `static byte[] NavBakeIdentity.Encode(NavBakeExpectation expected, string engineVersion)`, the S4 identity block. Invalid names, labels, digests, counts or duplicates throw `ArgumentException`.
  - `static (NavBakeLoadStatus Status, string Detail) NavBakeIdentity.Compare(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> expected)`. Equal bytes return `Loaded` with an empty detail. Otherwise it decodes both and names the first difference in the order engine, options, sources, profiles. It never throws. Stored bytes that are truncated, trailing, unsorted, duplicated, outside the label set, or that carry an overlapping filter return `Corrupt`. Filters are checked on raw `uint32` values before a `NavAreaFilter` is constructed.
  - `static MoveTuning NavBakeIdentity.ReadTuning(ref NavBakeReader reader)` and the matching writer. Reading restores `WalkSpeed = 1f`, `RunSpeed = 1f` and `AirMomentum = false`.
  - `NavBakeSources` keeps `(string Label, byte[] Digest)` entries, and `internal IReadOnlyList<(string Label, byte[] Digest)> Snapshot()` returns an ordinal-sorted copy.
  - `NavBakeBinary.cs`: `ref struct NavBakeReader` and `sealed class NavBakeWriter` over `ArrayBufferWriter<byte>`, with `int32`, `uint32`, `uint16`, `uint8`, float bits, byte spans and short ASCII strings. Every read returns `false` when bytes run out.

- [ ] **Step 1: Write the tests.**

```csharp
[Fact] public void IdentityIsCanonicalAcrossInsertionOrder()
// Two sources and two profiles added in opposite orders encode to equal bytes.

[Fact] public void EveryRetainedTuningFieldChangesTheIdentity()
// Reflect MoveTuning's primary constructor parameters. Floats change by MathF.BitIncrement, bools flip.
// The 27 retained fields each change the bytes. WalkSpeed, RunSpeed and AirMomentum leave them equal.
// Failure text: a new MoveTuning field needs encoding and a format version bump.
Assert.Equal(30, all.Count);
Assert.Equal(27, retained.Count);

[Fact] public void EveryOptionFieldChangesTheIdentity()
// Reflect PhysicsNavBakeOptions' primary constructor parameters.
Assert.Equal(13, fields.Count);   // each field changed alone changes the bytes

[Fact] public void EngineVersionStripsBuildMetadata()
Assert.Equal("20.19.0", NavBakeIdentity.NormalizeEngineVersion("20.19.0+8dfe93941"));
// Encode with "20.19.0" and "20.19.1" differ.

[Theory] public void LabelsNamesAndDigestsAreValidated(string fault)
// "", "A", 65 characters, "a b", a duplicate label or name, a 31-byte digest: ArgumentException.
[Fact] public void SetBoundsAreValidated()                     // zero sources, zero or 257 profiles: ArgumentException

[Fact] public void IdentityBytesMatchTheGoldenFingerprint()
// Fixed options, sources ("world", 32 bytes of 0x11), engine version "0.0.0-golden" and one profile "player"
// with a literal tuning written out in the test: new MoveTuning(WalkSpeed: 2f, RunSpeed: 5f, CapsuleHalfHeight: 0.75f,
// MaxSlopeRadians: 0.8f, CapsuleRadius: 0.3f) with every other argument at its declared default, and default areas.
// SHA-256 hex of Encode equals a constant recorded at first GREEN. The constant changes only with a format version bump.

[Theory] public void CompareNamesTheFirstDifference(string change, NavBakeLoadStatus status, string detailFragment)
// engine -> EngineChanged "0.0.0-golden"; CellSize -> OptionsChanged "CellSize"; digest of "world" -> SourcesChanged "world";
// extra label "catalog" -> SourcesChanged "catalog"; missing profile -> ProfilesChanged "player";
// Gravity -> ProfilesChanged containing "player" and "Gravity".

[Theory] public void NonCanonicalStoredIdentityIsCorruptAndNeverThrows(string fault)
// Hand-edited golden bytes: unsorted labels, duplicate profile name, label byte 'A', overlapping filter bits,
// one trailing byte, cut one byte short. Compare returns Corrupt for each.
```

- [ ] **Step 2: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~NavBakeIdentityTests"`. Expected: compile FAIL on the missing types.
- [ ] **Step 3: Implement** the interfaces above in S4 field order. Use `IncrementalHash.CreateHash(HashAlgorithmName.SHA256)` for `AddHashOf`.
- [ ] **Step 4: Run GREEN** with the same filter. Record the golden constant and its inputs in Outcome. Expected: all pass.
- [ ] **Step 5: Commit.** `feat(movement): canonical identity for baked navigation sets`

---

### Task 4: Bake format, create, write and load

**Files:**
- Create: `KhaozEngine.Movement/NavBakeLoadResult.cs`, `GroundNavigationBake.cs`, `GroundNavigationBake.Writer.cs`, `GroundNavigationBake.Reader.cs`
- Modify: `KhaozEngine.Movement/NavBakeBinary.cs` (adds what the payload needs)
- Modify: `KhaozEngine.Movement/GroundNavigation.cs` (internal `NavAreaFootprint Footprint { get; }`), `KhaozEngine.Movement/NavAreaFootprint.cs` (internal `Columns`, `Options`, `Areas`, on top of Task 1's change)
- Test: `KhaozEngine.Movement.Tests/GroundNavigationBakeRoundTripTests.cs`, `GroundNavigationBakeRefusalTests.cs`, `BakeEquivalence.cs` (assertion helper)

**Interfaces:**
- Consumes: Task 1 `NavAreaFootprint`. Task 2 `NavGrid.FromBlockedSurfaces`. Task 3 identity types and `NavBakeBinary`. The existing internal constructors `PhysicsNavColumns(options, width, height, starts, surfaces)`, `NavAreaFootprint(columns, options, tuning, areas)` and `GroundNavigation(graph, tuning, footprint)`, `PhysicsNavBake.Columns`, `Options` and `Origin`, and public `NavLayerLinks.GenerateGrounded(grids, stepHeight)`.
- Produces, public, exactly as design S5: `NavBakeLoadResult`, `GroundNavigationBake.Create`, `Load(Stream, NavBakeExpectation)`, `WriteTo`, `Fingerprint`, `ProfileNames` (identity order), `GetProfile`.
- Produces, internal: `static NavBakeLoadResult GroundNavigationBake.Load(Stream source, NavBakeExpectation expected, string engineVersion)`, used by the public overload and by tests.
- Format: design S4 payload. Accepted nodes are the open cells, so there is no accepted-node bitset. Candidate links are regenerated with `GenerateGrounded`, and `Create` throws `InvalidOperationException` if the fresh `Space.Links` differ from the regenerated list. Accepted links are a bitset over the regenerated candidates.
- Load order:
  1. `Encode(expected, engineVersion)` first. Its `ArgumentException` reaches the caller.
  2. Read the 52-byte header. A wrong magic is `NotABake`. A version other than 1 or nonzero flags is `UnsupportedFormat`. `L` over 1 MiB is `Corrupt`.
  3. Read `L` bytes and `Compare`, returning its status unless it is `Loaded`.
  4. Bound `P` by the design S4 formula and `Array.MaxLength`. When `source.CanSeek`, require `P == Length - Position`. Only then allocate a plain `byte[P]`.
  5. Read with a loop that tolerates short reads, require end of stream, verify SHA-256.
  6. Decode inside one `try` that catches `ArgumentException` and returns `Corrupt`. Enforce the design S4 value invariants before constructing grids, layers and graphs.

- [ ] **Step 1: Write the round-trip tests.** Fixtures reuse `GroundTraversalProbeTests.FlatWorld`, `StepWorld` and the `PhysicsNavProfileTests` geometry: thin wall, one metre door with profiles "small" (radius 0.2) and "wide" (radius 0.6), step, deck over water with tags (`feet.Y < 1f ? 0x02u : 0x01u`) and filters "dry" `(0, 0x02)` and "wet" `(0x02, 0)`, a world rebased by (100, 5, -40) before capture, and "stair-open" and "stair-fence". The two stair fixtures copy the inline geometry of `PhysicsNavProfileTests.CandidateStairLinksRequireTheSamePhysicalProof` (`PhysicsNavProfileTests.cs:211-221`): its two decks, the optional 0.1 m fence, bounds `MinX = -1f, MaxX = 1f` and the `tiny` tuning.

```csharp
[Theory, MemberData(nameof(Fixtures))] public void LoadedProfilesMatchTheFreshBuild(string fixture)
// Create, WriteTo a MemoryStream, Load. For every profile name: BakeEquivalence.AssertEquivalent(fresh, loaded),
// which checks every S6 item and runs AllowsSegment plus both FindPath overloads over all cell-centre pairs of the
// fixture, or a fixed stride sample when there are over 400 pairs.

[Fact] public void StairSeamAcceptanceRoundTrips()
// stair-open: the seam link is in the loaded graph's Links. stair-fence: it is in Space.Links and not in graph Links.
[Fact] public void TwoCreatesWriteIdenticalBytes()
[Fact] public void RewritingALoadedBakeReproducesItsBytes()
[Fact] public void ProfilesShareOneColumnSnapshotAfterLoad()   // Assert.Same on Footprint.Columns of "dry" and "wet"
[Fact] public void LoadedProfileDrivesMoveToRangeLikeTheFreshOne()
// Same body, target and live context, 60 ticks each through MoveToRange and NpcGroundMovement: equal steering and states.
[Fact] public void CreateRefusesADisposedCaptureAndAnOversizedSurfaceCap()   // ObjectDisposedException, ArgumentOutOfRangeException at 256
[Fact] public void SourcesAreSnapshotAtCreateAndLoad()
[Fact] public void GetProfileRejectsUnknownNames()             // KeyNotFoundException
```

- [ ] **Step 2: Write the refusal tests.** A helper `Reseal(byte[] file)` recomputes the payload SHA-256 after a deliberate payload edit so the decoder, not the checksum, is exercised.

```csharp
[Fact] public void NonBakeBytesAreNotABake()                   // empty, "KENC" plus header, 64 random bytes
[Theory] public void UnknownVersionOrFlagsAreUnsupported(ushort version, ushort flags)   // (2, 0) and (1, 1)
[Theory] public void StaleInputsAreRefusedWithTheFirstDifference(string change, NavBakeLoadStatus status, string detailFragment)
// Uses the internal engineVersion overload for EngineChanged. Bake is null for every refusal.
[Fact] public void InvalidExpectationThrows()                  // empty sources: ArgumentException from Load, before any read
[Fact] public void StaleRefusalDoesNotReadThePayload()        // counting stream: bytes read <= 52 + L
[Fact] public void EveryTruncationIsCorruptOrNotABake()        // prefixes 0 to 60, then every 97th length to the end
[Fact] public void TrailingBytesAreCorrupt()
[Fact] public void SeekableLengthMismatchIsCorruptBeforeAllocation()   // P edited one larger, MemoryStream: Corrupt, under 64 KiB allocated
[Fact] public void FlippedPayloadByteIsCorrupt()               // without Reseal
[Fact] public void HostileCountsAreCorruptWithoutLargeAllocation()
// Resealed surface count int.MaxValue and layer count int.MaxValue: Corrupt, and Load allocates under 1 MiB beyond the file.
[Theory] public void ValueInvariantsAreEnforced(string fault)
// Resealed: a per-cell count above MaxSurfacesPerColumn, descending column heights, NaN column height, NaN headroom,
// headroom -1, NaN open layer height, NaN YMin. Each returns Corrupt.
[Fact] public void UnusedBitsetBitsAreCorrupt()                // resealed high bit in a blocked bitset and in the stair-open accepted-link bitset
[Fact] public void ExitToABlockedCellIsCorrupt()               // resealed
[Fact] public void LoadHandlesOneByteReads()                   // stream wrapper returning at most one byte per Read
[Fact] public void SubsetExpectationIsRefusedNamingTheMissingProfile()   // expect only "small" against a small plus wide bake
```

- [ ] **Step 3: Run RED.** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~GroundNavigationBake"`. Expected: compile FAIL, `GroundNavigationBake` missing.
- [ ] **Step 4: Implement** the writer and reader. The writer encodes into an `ArrayBufferWriter<byte>`, hashes the payload, then writes header, identity and payload.
- [ ] **Step 5: Measure once.** On the deck fixture, record file size, `Load` elapsed and `Load` allocated bytes in Outcome from a temporary `ITestOutputHelper` line. Remove the line before commit.
- [ ] **Step 6: Run GREEN** with the Step 3 filter, then the whole Movement.Tests project. Expected: all pass with nonzero counts.
- [ ] **Step 7: Commit.** `feat(movement): bake and load ground navigation profile sets`

---

### Task 5: TileWorld bridge proof

**Files:**
- Modify: `KhaozEngine.TileWorld.Physics.Tests/TileWorldMovementNavigationTests.cs:247-281`, changing `BuildDisposed`'s inputs, `Bounds`, `Drawn` and the nested world fixture from private to internal. No behavior change.
- Create: `KhaozEngine.TileWorld.Physics.Tests/TileWorldNavigationBakeTests.cs`

**Interfaces:**
- Consumes: Task 4 public API only.

- [ ] **Step 1: Write the tests.** Sources use `new NavBakeSources().Add("colliders", colliders.Hash)`. The fixture has only quarter-turn surfaces, so the hash is exact here.

```csharp
[Fact] public void BakedTileWorldLoadsEquivalentToAFreshBuild()
// The deck and bed water fixture plus the one metre door, profiles "player" (radius 0.3) and "wide".
// Physics world disposed before Load. Rewrite of the loaded bake equals the file bytes.
// Every route and AllowsSegment answer from the existing bridge facts matches between fresh and loaded profiles.

[Fact] public void ColliderEditRefusesTheBakeNamingItsSource()
// Raise one tile corner, rebuild colliders. Load returns SourcesChanged with "colliders" in Detail and a null bake.
```

- [ ] **Step 2: Run RED then GREEN.** `dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~TileWorldNavigationBakeTests|FullyQualifiedName~TileWorldMovementNavigationTests"`. Expected RED: both new facts fail only if Task 4 behavior is wrong, since the API exists. A first GREEN with nonzero counts is acceptable evidence for a pure acceptance task. Record it as such in Outcome.
- [ ] **Step 3: Observe once.** With a temporary test, bake a 48 m by 48 m drawn flat world at 0.25 m (36,864 columns). Record `Capture` and `BuildProfile` elapsed and allocated bytes, file size, `Load` elapsed and allocated bytes in Outcome. Delete the temporary test before commit. This is one observation, not a benchmark.
- [ ] **Step 4: Commit.** `test(movement): prove baked tile world navigation`

---

### Task 6: Route-free approach driver (O2.21)

**Files:**
- Create: `KhaozEngine.Movement/RangeApproachCore.cs`, `DirectApproachOptions.cs`, `DirectMoveToRange.cs`, `DirectMoveToRange.Progress.cs`
- Modify: `KhaozEngine.Movement/MoveToRange.cs`, `MoveToRange.Approach.cs` (call the extracted core, behavior unchanged), `RangeMoveStatus.cs` (append `Blocked` after `Suspended`)
- Modify: `KhaozEngine.Server.Tests/NetWorld/PlayerRangeMovementTestRig.cs` (add `SteerDirect`)
- Test: `KhaozEngine.Movement.Tests/DirectMoveToRangeTests.cs`, `KhaozEngine.Server.Tests/NetWorld/PlayerDirectApproachAcceptanceTests.cs`

**Interfaces:**
- Produces, public, exactly as design S9: `DirectApproachOptions`, `DirectMoveToRange`, `RangeMoveStatus.Blocked`.
- Produces, internal `static class RangeApproachCore`: `TravelBound`, `BoundedDirection`, `ClosestHorizontal`, `Body`, `Feet` and `ValidateBody`, moved unchanged from `MoveToRange`, plus `StopAtRange(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range, bool run, float dt, GroundMoveContext context, Vector2 command, in MoveState predicted, StepAdmission admits)` and `internal delegate bool StepAdmission(in MoveState from, in MoveState to, in MoveTuning tuning)`. The tuning parameter carries `CapsuleHalfHeight`, which `MoveToRange.AllowsStep` needs for feet (`MoveToRange.Approach.cs:43-45`). `MoveToRange` caches its admission delegate once in its constructor.
- Produces, internal in `DirectMoveToRange.Progress.cs`: a ring buffer of `(Vector2 FeetXz, float Distance)` holding `max(StallWindowTicks, ApproachWindowTicks) + 1` samples, allocated in the constructor. Methods `Record`, `ClearAll`, `ClearApproach`, `StallBreached(float travelMetres, int windowTicks)` and `ApproachBreached(float gainMetres, int windowTicks)`. A window of `N` ticks is `N` intervals between `N + 1` samples and is first eligible on the `(N + 1)`th counted tick. The approach window keeps its own start so clearing it leaves the stall history intact.
- Server rig: `public RangeSteering SteerDirect(DirectMoveToRange driver, in ReachTarget target, float range, bool targetMoves, bool run = false)` using `Client.LocalPredictedState.Move`, `Tuning`, `TickSeconds` and `Context`.
- Tick order is design S9 steps 1 to 8, including the zero travel bound guard of ruling O2.25. The direct driver's step admission is grounded, not swimming and finite position, with no segment guard. The internal reset key is the same `GoalShape` fields `MoveToRange` uses.

- [ ] **Step 1: Record base counts, then extract `RangeApproachCore`.**
Run on unchanged source: `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~MoveToRange|FullyQualifiedName~NpcGroundMovement|FullyQualifiedName~PlayerPathMovement|FullyQualifiedName~NpcRangeNavigation"` and record the passed count in Outcome. Extract the helpers and point `MoveToRange` at them, with no new test file yet. Rerun the same filter. Expected: all pass with the recorded count. Commit `refactor(movement): share range approach helpers`.

- [ ] **Step 2: Write the Movement tests.** Options `new DirectApproachOptions(15, 0.1f, 45, 0.1f)`, `dt = 1f / 30f`, tuning `MoveToRangeTests.Tuning`. Window facts feed scripted `MoveState` sequences to `Tick` over flat analytic ground, `new GroundMoveContext((_, _) => 0f)`, without stepping the world between ticks, as Grimhollow's `DirectWalkUpTests` do. Exactly one physics wall fact and one ledge fact use Bepu.

```csharp
// Scripted window facts on flat ground
[Fact] public void StallLatchesOnTheSixteenthCountedTick()
// Positions advance 0.005 m per tick toward a far point target. Ticks 1 to 15 are Following, tick 16 is Blocked with zero
// direction, and later ticks stay Blocked until Reset.
[Fact] public void ApproachLatchesOnTheFortySixthCountedTick()
// Positions move 0.05 m per tick on a circle around a static target (stall never trips, distance constant).
// Ticks 1 to 45 are Following, tick 46 is Blocked. The same script with targetMoves true is never Blocked within 120 ticks.
[Fact] public void SuspendedTicksCountTowardNeitherWindow()
// 15 counted stalled ticks, 10 airborne ticks (Suspended), then counted tick 16: Blocked only on that tick.
[Fact] public void RootedBodyHoldsFollowingAndIsNeverBlocked()
// SpeedScale 0 at a fixed position for 60 ticks: Following with zero direction every tick. Then SpeedScale 1 at the same
// position: Blocked first appears on the sixteenth counted tick after release, proving the rooted ticks were not counted.
[Fact] public void InRangeClearsWindowsAndLatch()
// Latch Blocked, script one in-range state (InRange), then 15 stalled ticks are Following and tick 16 is Blocked.
[Fact] public void TargetMovesToggleClearsOnlyTheApproachWindow()
[Fact] public void ShapeRangeAndGeometryChangesReset()

// Physics integration
[Fact] public void WallStallLatchesBlockedThroughThePhysicsCore()
// Bepu wall across the line to the target, Tick then NpcGroundMovement.Step. The first Blocked arrives on the first tick
// whose sample is under 0.1 m from the sample 15 counted ticks earlier.
[Fact] public void LedgePreflightRefusalCountsAsRequestedTravel()
// 2 m drop ahead: Following with zero direction, Blocked on the sixteenth counted tick, body never airborne.

// Approach behavior and contract
[Fact] public void OpenGroundReachesRangeWithoutAPlanner()
// Box target 6 m away, range 1.5, flat analytic ground. Loop Tick then NpcGroundMovement.Step for at most 300 ticks until
// InRange. Final ReachGeometry.Within holds and Distance >= range - TravelBound for one tick.
[Fact] public void FinalStepShrinksToTheRing()          // body 0.05 m outside range: magnitude < 1, Within after the step
[Theory] public void OptionsRequirePositiveFiniteThresholds(int stallTicks, float travel, int approachTicks, float gain)
// zero or negative ticks, zero, negative, NaN or infinite metres: ArgumentOutOfRangeException.
[Fact] public void BlockedRequestsIdleFromBothAdapters()
// PlayerPathMovement.Command with Blocked and a nonzero direction yields a zero move with ScaleSpeedByAxis true.
// NpcGroundMovement.Step with Blocked equals NpcGroundMovement.Hold.
```

- [ ] **Step 3: Write the Server acceptance tests.**

```csharp
[Fact] public void DirectWalkUpReachesRangeThroughTheRealCommandPath()
// Open ground box target, rig.SteerDirect, PlayerPathMovement.Command, Submit, Serve, Ingest per tick.
// Authority ends Within range and outside range - 0.1, every submitted frame is precise.
[Fact] public void DirectWalkUpBlockedByAWallSubmitsIdle()
// new PlayerRangeMovementTestRig(wallAndBox: true): Blocked latches, later submitted commands have zero move.
```

- [ ] **Step 4: Run RED.**
Run: `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~DirectMoveToRangeTests"`
Run: `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~PlayerDirectApproachAcceptanceTests"`
Expected: compile FAIL, `DirectMoveToRange` and `RangeMoveStatus.Blocked` missing.

- [ ] **Step 5: Implement** `DirectApproachOptions`, the progress ring and `DirectMoveToRange`.
- [ ] **Step 6: Run GREEN** with both Step 4 commands, the Step 1 filter, and `--filter "FullyQualifiedName~PlayerRangeMovement"` on Server.Tests. Expected: all pass with nonzero counts, and the Step 1 filter keeps its recorded count.
- [ ] **Step 7: Record the reference divergences** listed in design S9 in the Movement README draft for Task 7 and in Outcome.
- [ ] **Step 8: Commit.** `feat(movement): approach reach targets without a planner`

---

### Task 7: Living documentation, release note and full verification

**Files:**
- Modify: `KhaozEngine.Movement/README.md` (new "Baked profile sets" section after "Capsule checked ground profiles", new "Route-free approach" section after "Range steering and movement drivers", `Blocked` in the status list, the allocation targets and measured results)
- Modify: `KhaozEngine.Navigation/README.md` (`FromBlockedSurfaces` beside the surface grid factories)
- Modify: `docs/USING-KHAOZENGINE.md` (Movement section: bake in a content pipeline, load at startup, branch on status, direct approach)
- Modify: root `README.md` Movement package summary if it lists Movement capabilities
- Modify: `CHANGELOG.md` (extend the entry for the version root names)
- Modify: `docs/INDEX.md` row status and the design's status line, after verification

- [ ] **Step 1: Confirm the version with root** before editing `CHANGELOG.md`. Per ruling O2.26 root turns the staged `20.18.1` into `20.19.0` with its entry folded in, and the delta reliability work rides it. Add the release note below to that entry. When the bump lands on this branch, update every declaration `scripts/check-doc-versions.sh` guards in the same commit.

Release note draft:

```markdown
- `GroundNavigationBake` persists physics-checked `GroundNavigation` profile sets. `Create` builds named profiles from one
  `PhysicsNavBake` capture, `WriteTo` writes a little-endian `KENB` file, and `Load` returns them without physics in
  milliseconds. A canonical identity covers the engine version, capture options, area filters, every `MoveTuning`
  field except unit pace and air momentum, and caller-labelled SHA-256 source digests. A stale or damaged bake returns
  a typed `NavBakeLoadStatus` with the first difference named, never a silent fallback. Lifts round 2's navigation persistence non-goal for this scope.
- `NavGrid.FromBlockedSurfaces` rebuilds a surface grid from a stored blocked mask and open-cell heights.
- `BuildProfile` no longer allocates per proof. Penetration queries reuse their overlap scratch, the footprint predicate and
  the dry probe context are built once, and a warmed penetration query and edge proof allocate nothing.
- `DirectMoveToRange` approaches a reach target without a planner, with `MoveToRange`'s exact reach, stop ring and suspension
  rules. Caller-supplied tick windows latch the new `RangeMoveStatus.Blocked` when travel or reach progress stalls.
```

- [ ] **Step 2: Sweep.** Run `git grep -n -w -e GroundNavigation -e BuildProfile -e RangeMoveStatus -e "navigation persistence" -- '*.md'` and correct every stale description. Then reread `KhaozEngine.Movement/README.md` end to end.
- [ ] **Step 3: Reconcile.** `git fetch origin && git merge origin/main`. Resolve conflicts on this branch and keep both changelog sections.
- [ ] **Step 4: Full verification, once.**

```sh
cd /Users/antonio/KhaozEngine/.worktrees/grimhollow-nav-bake
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: build exit 0 with zero warnings, test exit 0 with zero failures and nonzero totals per assembly, every guard exit 0. Record exit codes, totals and elapsed times in Outcome.

- [ ] **Step 5: Update status** in the design status line and `docs/INDEX.md` to implemented and verified, with no release claimed.
- [ ] **Step 6: Commit.** `docs(movement): document baked navigation and direct approach`. The worker stops at its verified commit. Root merges, pushes, packs and selects the release.

## Outcome

Pending execution. Record per task: commit, focused counts, RED evidence, the Task 1 baseline and final byte counts, the Task 3 golden constant, the Task 4 and Task 5 observations, every departure with reason and cost, and the final verification.

### Task 1: allocation fix

Done. A1, A2 and A3 pass. The three planned changes left a `CharacterMovement` residual, so the task stopped twice at the
escape hatch. Root rulings N2 and its extension authorized one capsule cache each in `CharacterMovement.cs` and
`CharacterMovement.Collision.cs`.

| Target | Baseline | Three planned changes | Plus step capsule cache | Plus slide probe cache | Target |
| --- | ---: | ---: | ---: | ---: | ---: |
| A1 warmed `ComputePenetration`, world and view | 72 B per call | 0 B | 0 B | 0 B | 0 B |
| A2 warmed medium `TryEdge` | 6,848 B per call | 624 B per call | 312 B per call | 0 B | 0 B |
| A3 `BuildProfile`, 4,096 columns | 240,738,584 B, 58,774 B per column | 22,098,368 B, 5,395 B per column | 11,213,504 B, 2,738 B per column | 328,640 B, 80 B per column | 4,194,304 B, 1,024 B per column |

The final A3 value was the same on three consecutive warmed calls.

RED evidence. `PenetrationAllocationTests` failed both allocation facts at 7,200 bytes on both passes and passed
`ConsecutiveQueriesDoNotLeakCandidates`. `ProfileAllocationTests` failed to compile with CS1061 for `DryContext` only.
With that fact disabled the baselines above were measured, then the fact was restored.

Residual sites. In-process `GCAllocationTick` captures attributed about 99 percent of the remaining probe and profile
bytes to `KhaozEngine.Physics.CapsuleShape`, a sealed immutable 24-byte class. `CharacterMovement.StepCore` built one
per step through `CapsuleFor(t)`, and `SlideSubstep` built a skin-inflated probe per substep
(`CharacterMovement.Collision.cs:155`). Each site now reuses a `[ThreadStatic]` single-entry cache keyed by exact radius
and length bits, kept separate so the two never evict each other. Public `CapsuleFor` still returns a new instance.
No consumer stores a capsule or compares one by reference, so reuse is bit-identical. The remaining A3 bytes are the
result grids (`Single[]`, `Int32[]`, `Byte[]`).

Departures. The A3 budget reads `GC.GetAllocatedBytesForCurrentThread` around one call with one retry, mirroring
`AllocAssert`, because `AllocAssert` only asserts zero. The `DryContext` fact also asserts that ground, normal, physics
and clamp are carried. The `_dryContext` field lives in `GroundMoveContext.cs`, not the coordinates partial, to stay
inside the brief's file list. `CharacterMovement` edits came from rulings N2 and its extension, at a cost of two
thread-static fields. No issue was filed, by dispatch instruction.

Verification. Movement.Tests 354 passed of 354. Game.Tests `KhaozEngine.Tests.Physics` 203 passed of 203.
Game.Tests `KhaozEngine.Tests.Locomotion` 996 passed, 1 skipped of 997. Zero build warnings. File size guard exit 0.

### Task 3: canonical bake identity

Done. RED was a compile failure on the missing types (CS0246 and CS0103, 12 errors). GREEN is 41 of 41 in
`NavBakeIdentityTests` with zero warnings.

Golden fingerprint, the SHA-256 of the identity block, is
`a0a927cec3bf88a763041c829767efc6be1415e4e75070cc378948cfd43666e3` over 228 bytes. Inputs: engine version
`0.0.0-golden`, options `MinX -8`, `MinZ -8`, `MaxX 8`, `MaxZ 8`, `CellSize 0.25`, `ProbeHeight 10`, `ProbeRange 20`,
`MaxSlopeRadians 0.8`, `MaxCells 4096`, `MaxLayerCells 8192` with the other three at their declared defaults, one
source `world` with 32 bytes of `0x11`, and one profile `player` with default areas and
`new MoveTuning(WalkSpeed: 2f, RunSpeed: 5f, CapsuleHalfHeight: 0.75f, MaxSlopeRadians: 0.8f, CapsuleRadius: 0.3f)`.
The same digest was computed independently from the design S4 layout with Python `struct`, so the encoder matches the
written layout and holds no architecture-dependent data.

Departures.

- `ReadTuning` is `bool ReadTuning(ref NavBakeReader reader, out MoveTuning tuning)` rather than returning the tuning,
  because every reader call reports running out of bytes, and a bool byte other than 0 or 1 is also non-canonical.
- `NavBakeIdentity.TryDecode` is the non-throwing decode path for Task 4. It returns the decoded engine version, raw
  option bits, sources and profiles with raw area bits, and a developer reason on failure.
- `NavBakeIdentity.FormatVersion` (value 1) lives with the identity, since adding a `MoveTuning` or
  `PhysicsNavBakeOptions` field changes the identity layout and requires that bump. The golden test asserts it. Task 4's
  container reads it.
- `Encode` validates the engine version, names, labels, digests, counts and duplicates. It does not validate the
  capture options, which stay the concern of `Create` and the Task 4 reader.
- Tests beyond the brief: a 64-character and full-alphabet label and name are accepted, `AddHashOf` over a span and a
  stream equals `SHA256.HashData`, `Snapshot` copies digests, every retained tuning field round trips with distinct
  values, and each corrupt case asserts its own reason so it cannot pass on an unrelated failure.

### Task 6: route-free approach driver

Done. Commits `refactor(movement): share range approach helpers` and
`feat(movement): approach reach targets without a planner` on `feature/grimhollow-nav-driver` from `a4ba1bba7`.

Step 1 base count. The Step 1 filter passed 97 of 97 on unchanged source and 97 of 97 after the extraction. After the
driver it passes 121 of 121, which is the same 97 plus the 24 `DirectMoveToRangeTests` cases that
`FullyQualifiedName~MoveToRange` also matches.

RED evidence. Both Step 4 commands failed to compile with CS0246 for `DirectMoveToRange` and `DirectApproachOptions`.
The compiler stopped at those types before binding `RangeMoveStatus.Blocked`.

GREEN. `DirectMoveToRangeTests` 24 passed of 24. `PlayerDirectApproachAcceptanceTests` 2 passed of 2. Server.Tests
`FullyQualifiedName~PlayerRangeMovement` 7 passed of 7. Zero build warnings. File size and dash guards exit 0.

`RangeMoveStatus` consumers. Only `PlayerPathMovement.Command` and `NpcGroundMovement.Step` read the status, and both
test for `Following` alone. `BlockedRequestsIdleFromBothAdapters` proves `Blocked` is idle through each. No switch over
the enum exists in engine source.

Departures.

- The reset key is compared on every validated tick, before the suspension check. `MoveToRange` compares it only on
  route ticks. Reason: a latched block must not survive a range or shape change made while the body is airborne or
  already blocked. Cost: none.
- Ruling N4 moved the reset key into one internal `RangeShapeKey` with `From(tuning, target, range)` in
  `RangeApproachCore.cs`. Both drivers use it, so a new field cannot drift between them. `MoveToRange.Goal.cs` changed
  only to call it, and the Step 1 filter proves `MoveToRange` behavior is unchanged.
- The direct admission delegate is one static field, since it reads no instance state. `MoveToRange` caches its own in
  its constructor as specified.
- The ledge fact walks the body to the brink with the driver, then calls `Reset` and asserts the 16 counted ticks
  from there. Reason: the exact refusal point depends on how the core rests a capsule on an edge. The body rests on
  the corner about 0.056 m below its standing height. The fact asserts it stays grounded and no lower than 2.75 m
  minus the capsule radius, since corner sag cannot exceed the radius.
- `ShapeRangeAndGeometryChangesReset` also asserts that translating the target keeps the latch, since the driver
  holds no target identity.
- `dotnet format --verify-no-changes` reports whitespace on lines 19, 20 and 58 of `PlayerRangeMovementTestRig.cs`.
  Those lines predate this task and engine CI does not run the format check, so they were left alone.

Ruling N3 follow-up, commit `fix(movement): bound direct approach windows`. `DirectApproachOptions` refuses a window
above 65,535 ticks, so the ring capacity cannot overflow. `WindowsUpToTheBoundBuildADriver` and three new refusal rows
cover the bound. `WarmedSteadyTicksAllocateNothing` measures recorded orbit ticks, a stop ring bisection and `Reset` with
`AllocAssert`, and the test class joined the `AllocSensitive` collection. `DirectMoveToRangeTests` 29 passed of 29, Step 1
filter 126 passed of 126. The allocation fact passed on first run, as acceptance evidence for an existing property.

Ruling N4 follow-up, commit `refactor(movement): share range shape keys`. The shared `RangeShapeKey` is described in the
departures above. Tests add capsule half height and a capsule target's radius and half height to the shape reset
cases, a committed grounded body to the suspension theory, `LatchedBlockSurvivesSuspendedTicks`, and `ParamName` checks
in the options theory. `DirectMoveToRangeTests` 31 passed of 31, Step 1 filter 128 passed of 128.

Movement README draft for Task 7, a "Route-free approach" section after "Range steering and movement drivers":

```markdown
## Route-free approach

`DirectMoveToRange` steers a body toward exact shape range without a planner. It shares `MoveToRange`'s exact reach,
travel bound, closest point and stop ring code, and returns the same `RangeSteering` for `PlayerPathMovement` and
`NpcGroundMovement`. `DirectApproachOptions` takes a stall window in ticks with a travel distance and an approach
window in ticks with a reach gain. There are no defaults. A window of N ticks spans N intervals between N + 1 counted
samples and is first eligible on the (N + 1)th counted tick. When either window shows too little progress the driver
latches `RangeMoveStatus.Blocked`, which both adapters treat as idle.

Differences from a typical game-side walk-up rule:

- Direction uses the closest horizontal point of a box rather than its centre. For capsules and points they are the
  same. A box approach reaches its near face sooner.
- The final fraction comes from live bisection against exact reach, not from a minimum fraction, so a short last step
  cannot stall or overshoot.
- A step that would leave the ground or start swimming is refused and counts toward the stall window, so a walk off a
  ledge or into deep water ends `Blocked` instead of falling or swimming.
- A zero travel bound, such as a rooted body, counts toward neither window, so a rooted body holds without ending its
  walk. Airborne and committed ticks return `Suspended` and also count toward neither window.
- `InRange` clears both windows, so a followed body that moves away starts fresh windows.
- Stall travel is net displacement across the window, not accumulated path length, so pacing in place is blocked.
- `Blocked` stays latched until `InRange` or `Reset`. End the walk on the first `Blocked`.
- A change of target kind, shape, yaw, range or capsule geometry resets the windows. A change of `targetMoves` clears
  only the approach window. The driver holds no target identity, so replacing the target with another of the same
  shape needs `Reset`.
- Pass `targetMoves` true for any body target, such as a creature or player, and false for static objects and points.
- Call `Tick` exactly once per simulation tick, since the windows count ticks.
```
