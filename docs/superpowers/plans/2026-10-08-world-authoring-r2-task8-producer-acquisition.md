# R2 Task 8 Producer Acquisition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete Task 8 with opt-in pinned producer sessions, one before-work acquisition budget, immutable scoped witnesses and verified scheme-2 whole identity.

**Architecture:** The two built-in sources open one finite acquisition session over metadata they already own. The session owns traversal and unique-key reservations, while the factory owns breadth-first record expansion and M4 bound preflight. Whole stored identity uses retained original manifest facts and verifies the pinned closure one payload at a time.

**Tech Stack:** C# on `net10.0`, System.Numerics, System.Text.Json, System.Security.Cryptography and xUnit 2.9.2. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-R2-SOURCE-ACQUISITION-2026-10-07.md`, owner-approved written contract OA18 at `86890fe22b340b483eaeed936dfb766b758ea53f`, original SHA256 `7d61c91e7b07963267f631b62b5b4055656ed21c72df0bcb7fed0953592ab800`. Also read canonical Task 8 and D5/M4 in `docs/superpowers/plans/2026-10-05-world-authoring-r2-authored-terrain-paint.md`, and its extracted `.superpowers/sdd/2026-10-05-world-authoring-r2-authored-terrain-paint/task-8-brief.md`.

## Global Constraints

- This replaces execution ordering and file ownership of canonical Task 8 only. Its fixture definitions, public consumer contracts, identity projections and original 22 prepared cases remain binding.
- The written producer contract is approved. Parent self-review and independent executable-plan review are complete. The owner approved this executable amendment on 2026-10-08 (OA19), at c62a17c70650835015abf3a41d0d2ff1b7155e88, original SHA256 b89a2bb41c7e9ab9a0bb2860ffa3472c1076ed9f37938acf97c9ff94c0ceffa9. Execute serially with the existing review and shared compute gates. Unchecked steps are future work, not execution evidence.
- Preserve `IMapSurfaceSource`, `MapPatchRead` and `MapPatchFindResult` signatures and positional constructors. Unsupported sources throw `MapDocumentException` containing `surface source does not support bounded scoped acquisition` before legacy callbacks.
- Keep `MaxCandidatePatches` one global unique-key allowance. Keep separate metadata and range allowances, each `1 + 4 * 256 * MaxPageReads`. Charge visits before cache checks, metadata before inspection, range bookkeeping before work and decode attempts before I/O.
- Session `PagesRead` is cumulative successful new directory/index decodes. Each operation result reports its own delta. Cache hits and captured work may exhaust budgets with zero decodes. FarAnchors remains five decoded pages and two additional anchor reads.
- `MapExactOverflowException` maps to `NotRepresentable`. Checked gross bound-count overflow maps to `CapacityExceeded`. Both publish no facts and dispose the session. Missing/corrupt/unloaded and unknown incidents never become empty or complete.
- Whole identity keeps `kemap/native-authored/2`. Scoped identity keeps `kemap/scoped/1`. Storage names, packing and incident bookkeeping remain outside semantic identity. A partial editing scoped digest is not a whole current editing hash.
- Witnesses remain sealed, factory-only and get-only, with detached `ReadOnlyCollection<T>` lists. Copy nested record lists, surface span tags, scope roles/space ids, asset digests and returned payloads. No producer is reread after publication.
- Only public synthetic inputs and finite headless conformance tests. No geometry/compiler work, full-world scoped scan, private oracle, stress, native adoption, release, version, main or feed work belongs to these substeps.
- Owner override OA20 removes slot/grant waits. Run authorized bounded commands directly and serially, one building worker in this lane at a time. Preserve actual exits and stop on unexpected failure. Workers stop at verified commits for controller review. No stress or repeated unchanged runs.

## Review Focus

1. A deleted indexed seed still consumes a key before a new resident seed can be read. Owner: 8A, `CapturedSession_DeletedSeedAndResidentSeedShareTheCandidateCap`.
2. A warm cache can exhaust traversal, metadata or range work while decoding zero pages. Owner: 8A, `StoredSession_AllPhasesShareVisitsBeforeCacheChecks`, `Session_MetadataAllowancePrecedesIndexedWork`, `Session_RangeAllowancePrecedesBookkeeping`.
3. A footprint needing two fresh bound keys can be refused before any bound work when an explicit known-empty key consumed a slot. Owner: 8B, `BoundPreflight_UsesKnownEmptyReservationsBeforeOneOverLimitRange`.
4. An incident-only remote record or unknown partial-window incidence must not disappear into a complete witness. Owners: 8A/8B, `Session_IncidentKnowledgeDistinguishesCompleteExistingAndNewPartialKeys`, `Acquire_IncidentOnlyAnchorIsIndependentOfEveryOtherPath`.
5. Complete unsaved edits are valid authored input, while stored corruption and legacy metadata must refuse before inappropriate payload work. Owner: 8C, `SchemeTwo_CompleteUnsavedEditsUseCurrentSemantics`, `StoredIdentity_LegacyMetadataRefusesBeforePayloadWork`, and the original stale-digest case.

---

## Dependency, ownership and execution conventions

Task 7 is complete at `e0e975cb4c2e0768b51d740fe14ff31e1ee08101`. Start from the reconciled task branch, read current `AGENTS.md`, and compare cited symbols with the current source before dispatch. Run 8A, review, 8B, review, 8C, review. Task 9 remains outside this amendment.

The complete source-acquisition amendment is normative. Review 1 and review 2 under the ignored SDD directory explain the reservation-provenance correction and the one-over-limit-range cap-three control. Their earlier approval descriptions are historical, not this plan's status.

Use canonical `wa_run`/`wa_test`, `MAPDOC`, `EDITOR` and unique log names from its Verification conventions. Each RED is one run, with the named missing type or assertion failure. Each GREEN needs exit 0, zero warnings/failures and the stated nonzero case count. An unrelated compiler/setup failure or empty filter is not RED evidence.

For every substep, record its exact owned `.cs` paths in a shell array `wa_t8_files`, including new files, before the following format gate:

```bash
wa_run t8X-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --include "${wa_t8_files[@]}" --verbosity diagnostic
```

Replace `X` with `a`, `b` or `c`. Require exit 0 and actual analyzer/document coverage for MapDoc, MapDoc.Tests and MapEditor.Tests when their files are owned by the substep. Retain any workspace warning and its coverage disposition. Explicit paths ensure new sources are covered. This command verifies all configured diagnostics and whitespace, not only IDE0005. The final whole-solution format gate remains required. After a failure, diagnose it, make the reviewed correction and run only the affected verification. Never suppress warnings or change ratchet baselines.

Every commit uses explicit owned paths, `git diff --cached --check`, then the stated subject. No whole-directory staging, stash, push or main integration by a worker. The controller owns current-main reconciliation and the canonical candidate/release gates later.

Keep newly linked `*Fixtures.cs` type names disjoint from MapEditor.Tests-owned types. Tests requiring MapDoc internals live in MapEditor.Tests, which already has friend access. Public tests live in MapDoc.Tests. Both use `KhaozEngine.Tests.*` namespaces. No project-reference change is needed.

## Frozen-case mapping

The four drafts remain unchanged under `.superpowers/sdd/2026-10-05-world-authoring-r2-authored-terrain-paint/task-8-preparation/`. Apply the corresponding file to its project only in the owning substep. Preserve every assertion, theory row and embedded control, including the partial editing root distinction and corrupt-anchor control.

| Prepared destination and methods | Cases | Owner |
| --- | ---: | --- |
| `KhaozEngine.MapDoc.Tests/Storage/ScopedAcquisitionTests.cs`: FarAnchors, missing/corrupt anchor, page/record/depth theory `(4,64,8)`, `(32,1,8)`, `(32,64,0)`, exact lattice ranges, mixed positive-area bounds, micro bound refusal, exact overflow, immutable payload/sweep | 10 | 8B |
| `KhaozEngine.MapEditor.Tests/MapDoc/ScopedAcquisitionWorkTests.cs`: MicroBound checked `244_140_625L`/zero visits, AcquiredBounds two present keys/gross `1L`/zero visits | 2 | 8B |
| `KhaozEngine.MapDoc.Tests/Identity/SchemeTwoIdentityTests.cs`: monolithic/tiled and builder/options controls, five height/unit/record/role/displayName rows, stale digest, partial view plus pinned-root/acquired-edit facts, factory-only scoped shape | 9 | 8C |
| `KhaozEngine.MapEditor.Tests/MapDoc/SchemeTwoRepackTests.cs`: equal tokens, nonempty different index-file sequences | 1 | 8C |
| Total original cases | 22 | |

Implement `AnchorFixtures`, `ScopeFixtures` and `AcquisitionBoundFixtures` from canonical Task 8 verbatim in meaning. Do not replace FarAnchors with the new isolated fixture, alter its coanchors, turn two additional reads into all patch reads, or equate five decodes with five visits.

### Task 8A: Opt-in producer sessions and actual before-work gates

**Files:**
- Create: `KhaozEngine.MapDoc/Storage/IMapSurfaceAcquisitionSource.cs`, `MapSurfaceCapacityException.cs`, `MapPageBudget.cs`, `MapSurfaceSessionState.cs`, `MapSurfaceAcquisitionRead.cs`, `MapSurfaceAcquisitionWork.cs`.
- Create: `KhaozEngine.MapDoc/Storage/MapStoredSurfaceSource.Acquisition.cs`, `MapDocumentSurfaceSource.Acquisition.cs`.
- Modify: `KhaozEngine.MapDoc/Storage/MapStoredSurfaceSource.cs`, `MapDocumentSurfaceSource.cs` (partial declarations and source-owned immutable surface lookup), `MapSurfaceQuery.cs` (extract budget/exception and add session path), `MapSurfacePageLookup.cs` (observations at existing budgeted node gate).
- Create public tests: `KhaozEngine.MapDoc.Tests/Storage/SurfaceAcquisitionSessionTests.cs`, `SessionConformanceFixtures.cs`.
- Create internal tests: `KhaozEngine.MapEditor.Tests/MapDoc/SurfaceAcquisitionBudgetTests.cs`.

**Interfaces:**
- Existing `Index.Covering(string surface, MapSlotRect slots, MapPageBudget? budget = null)`, `Directory(dir,budget)`, `Page(dir,page,budget)`, `Payload(entry)`, `MapSurfaceQuery.Subtract` and source capture/open remain the read seams.
- New public declarations in `KhaozEngine.MapDoc.Storage`, exactly as approved:

```csharp
public interface IMapSurfaceAcquisitionSource : IMapSurfaceSource
{
    IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope);
}
public interface IMapSurfaceAcquisitionSession : IDisposable
{
    string SnapshotId { get; }
    string RootSha256 { get; }
    int PagesRead { get; }
    IReadOnlyList<MapPatchKey> ReservedPatchKeys { get; }
    bool TryGetSurface(string surfaceId, out MapSurfaceRef? surface);
    MapPatchFindResult FindPatches();
    MapPatchRead ReadPatch(MapPatchKey key);
    bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records);
}
public sealed class MapSurfaceCapacityException : Exception
{
    public MapSurfaceCapacityException() { }
}
```

- Both sources implement the opt-in interface. Add internal `IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope, MapSurfaceAcquisitionWork? work)` overloads for observation only.
- Extract existing `MapPageBudget(int limit)` into its file, with optional `MapSurfaceAcquisitionWork? work = null`. Retain `Reads`, `BeforeMetadata()`, `BeforeVisit()`, `BeforeRange()`, `BeforeRead()` and actual private admission counters. They remain enforcement even when `work` is null.
- `MapSurfaceSessionState(MapSurfaceScope scope, string snapshotId, string rootSha256, MapSurfaceAcquisitionWork? work)` owns one copied scope/budget and a sorted key set. Internal `BeginFind()`, `RequireFound()`, `Reserve(MapPatchKey key)`, `SnapshotReservedKeys()` and `Dispose()` enforce order, lifetime and the cap before producer work.
- `MapSurfaceAcquisitionRead.Read(MapSurfaceStorageIndex index, MapPatchKey key, MapPageBudget budget, Func<MapDirectoryPageRef,MapPageBudget,MapPatchStatus> directory, Func<MapDirectoryPageRef,MapIndexPageRef,MapPageBudget,MapPatchStatus> page, Func<MapSurfaceIndexEntry,MapPatchRead> payload)` is an internal shared indexed key-read helper. Reserve in the session before calling it. Charge rectangle bookkeeping before constructing the key range, lookup nodes before bounds checks, and page visits before callbacks/cache checks.
- Add internal `MapSurfaceQuery.FindAcquisition(...)` with the same source/index/scope/three callback parameters as `Find`, followed by required `MapPageBudget budget, Action<MapPatchKey> reserve`. It returns operation decode deltas, reserves immediately before each candidate payload callback and exposes exact overflow directly. Legacy `Find` keeps independent-call behavior and its existing wrapper.
- `MapSurfaceAcquisitionWork` is internal observation only, with admitted `PageVisits`, `DecodeAttempts`, `MetadataChecks`, `RangeOperations`, and actual `SurfaceLookups`, `LookupNodeInspections`, `DirectoryCallbacks`, `IndexCallbacks`, `PayloadReads`, `Disposals` counts. Increment actual observations after the corresponding admission and immediately before actual work. Counters cannot admit, reset, refund or imitate backend work.

- [ ] **Step 1: Write the public session tests and finite synthetic fixtures.** Use `SurfaceStorageFixtures.FlatPatches`, `FlatPatch`, `RecordReferrer`, `SaveToTemp`, `LoadSlotWindow`, `DeletePayload` and `FlipPayloadByte`, with caller-owned directory cleanup.

`SessionConformanceFixtures` supplies `OnePatch()`, `DeletedSeedAndResidentSeed(string dir)`, `ManyExcludedRefs(int count)` and `ManyEmptyRefs(int count)` returning documents. `OnePatch()` is `FlatPatches(1)`. The two Many* fixtures begin with `FlatPatches(0)` and clear its default refs before adding exactly `count` refs, so no extra ground ref shifts the counters. All keep resolver `(1,2)`/AuthoredBindingsV2 and use native positive-row metre/centimetre refs unless specified below. No private paths or global state.

Three public Facts in `SurfaceAcquisitionSessionTests`:

```csharp
// Session_PinsMetadataAndRejectsOutOfOrderAndDisposedWork
// Open -> ids equal source, PagesRead=0, ReservedPatchKeys empty. ReadPatch before Find,
// incident query for an unserved key, second Find and work after Dispose each refuse.
// Capture first, then edit the original doc and save a newer manifest. Session ids stay fixed.
// Session_SurfaceLookupReturnsDetachedNestedMetadata
// One declared ref has IndoorSpan.DomainTags backed by ["captured"]. TryGetSurface succeeds,
// returns a read-only detached tag list, later caller edits leave it unchanged. Unknown id -> false/null.
// Session_IncidentKnowledgeDistinguishesCompleteExistingAndNewPartialKeys
// Complete OnePatch: Find then true/empty incidents. Start from RecordReferrer and copy only
// yard-far with Lattice ground(0,0), retaining its ground(300,0) anchor, before Save. Its
// actual footprint now intersects seed0. Load slot0 and preserve that remote incidence. Add new ground(1,0) to that
// partial document without saving: serve it, then false/null incidence, never true/empty.
```

In `DeletedSeedAndResidentSeed`, start with `FlatPatches(1)`, add `FlatPatch(new("ground",300,0),1000)` before saving, then load only slot 0 so slot 300 remains unread and the window is partial. Remove indexed A=`ground(0,0)`, add resident B=`ground(1,0)`, and request x `[0,68]`, z `[0,4]`. Both candidate rectangles overlap. The first pass explicitly attempts A as KnownEmpty, then the resident pass attempts B.

- [ ] **Step 2: Write real backend budget controls.** `SurfaceAcquisitionBudgetTests` has the following 10 cases. Use the internal observation overload, never replace the built-in gates with a scripted producer.

| Named test and rows | Fixed setup and assertions |
| --- | --- |
| `StoredSession_AllPhasesShareVisitsBeforeCacheChecks(bool warm)` with false/true | One selected seed and one excluded-role anchor on separate surfaces, plus a third bound key on a third surface. Each directory has one index. Optionally prewarm all three through independent legacy reads. Session cap 5: Find consumes 2 visits, anchor consumes 2, bound directory consumes fifth, bound index is refused before callback. Assert visits=5, callbacks=5, last index callback absent, cold decodes=5/warm decodes=0. The allowed-cap-6 control completes the same Find/anchor/bound sequence once. |
| `StoredSession_OperationDeltasAndNewBudgetsAreIndependent` | Same three-key fixture, cold cap 6: Find.PagesRead=2, anchor=2, bound=2, session.PagesRead=6. A second session on the same source succeeds at cap 6 with deltas 0/0/0, cumulative 0, visits=6. Neither session refreshes ids or clears caches. |
| `CapturedSession_DeletedSeedAndResidentSeedShareTheCandidateCap` | Above A/B fixture, cap 1. Find CapacityExceeded, all returned lists empty, reserved exactly `[A]`, payload callbacks exactly 1, B callbacks=0. Snapshot list is detached/read-only. Raising cap to 2 reserves `[A,B]`, Present exactly B, A remains explicitly known-empty. |
| `CapturedSession_BothPassesShareVisitsWithZeroDecodes` | Same partial A/B fixture, cap 3 visits: stored directory/index consume 2, resident directory consumes third, resident index is refused before callback. Find CapacityExceeded, visits=3, callbacks=3, decodes=0, no B payload callback. Cap 4 permits both passes and merge. |
| `Session_MetadataAllowancePrecedesIndexedWork` | MaxPageReads=1 gives 1,025 metadata units. `ManyExcludedRefs(1024)` has Ceiling refs, SupportFloor scope and a patch on its first ref. Find admits 1,024 role inspections, no visits/ranges. One TryGetSurface consumes unit 1,025 and one actual lookup. The next lookup and a new-key ReadPatch both refuse before their lookup/node inspection or payload callback. Assert MetadataChecks=1025, SurfaceLookups=1, LookupNodeInspections=0, PayloadReads=0. No `StatusOf` Directory scan is allowed. |
| `Session_RangeAllowancePrecedesBookkeeping` | `ManyEmptyRefs(1024)`, selected SupportFloor refs and no pages. Find seeds exactly 1,024 ranges, metadata=1,024. One absent-key read constructs its checked range after unit 1,025. The next absent-key read refuses before constructing a range. Assert RangeOperations=1025, no page/payload callbacks and zero decodes. |
| `Session_DecodeAttemptsPrecedeStorageIo` | Internal `MapPageBudget(1,work)` passed to the real stored `Directory` callback for two uncached directory refs from the three-key fixture. First actual decode succeeds, second throws capacity before filesystem I/O. Assert DecodeAttempts=1 and Reads=1, second directory still uncached. This isolates the decode gate from the separately tested visit gate. |
| `Session_NormalizesCoordinateOverflow(bool stored)` with false/true | One source ref uses cell unit `1/int.MaxValue`. Scope is a finite x near `float.MaxValue`, so exact range conversion fails. Stored and captured session Find throw `MapExactOverflowException`, with zero payload callbacks. In the same Fact body, corrupt a normal payload and assert Corrupt rather than exact overflow. |

- [ ] **Step 3: Run first RED.** `wa_test t8a-red "$MAPDOC" "FullyQualifiedName~SurfaceAcquisitionSessionTests"`. Expected compiler failure names the new producer/session interface or `OpenAcquisition`, not a fixture/setup defect. Then one internal RED with `FullyQualifiedName~SurfaceAcquisitionBudgetTests`, expected new observation/session APIs absent.

- [ ] **Step 4: Implement the session paths.** Build the id lookup once at source Open/Capture. Open copies request/identity metadata only, loads no pages and rebuilds no incidence. Stored reads reuse pinned index callbacks. Captured reads use budgeted indexed callbacks and resident dictionaries, never legacy `ReadPatch`'s unbudgeted `StatusOf` fallback. Both discovery passes and merge share state and budget.

`TryGetSurface` charges before its indexed lookup and returns detached read-only nested metadata. `TryGetIncidentRecords` uses only already-served entry metadata, performs no I/O/query and snapshots its list. Complete captures use computed incidence, partial existing keys preserve stored incidence, new partial keys remain unknown. KnownEmpty/Missing/Corrupt/Unloaded explicit reads all reserve. Range-only emptiness and covering-page sentinels do not. Reserve repeated keys once and forbid collateral payload reads.

Normalize only the session's known coordinate/slot wrappers to `MapExactOverflowException`, without message matching. Preserve corrupt payload classification. Each result is its decode delta, not budget.Reads. Disposal is idempotent and releases only session resources.

- [ ] **Step 5: Run GREEN and explicit-file format.** `wa_test t8a-green-mapdoc "$MAPDOC" "FullyQualifiedName~SurfaceAcquisitionSessionTests"` expects 3 cases. `wa_test t8a-green-editor "$EDITOR" "FullyQualifiedName~SurfaceAcquisitionBudgetTests"` expects 10 cases. Run Task 7 storage regression filters from canonical Task 8 once after the changed read seams, plus the explicit-file diagnostic format command above.
- [ ] **Step 6: Commit and stop for review.** Stage only 8A's listed files, subject `feat(mapdoc): add bounded producer acquisition sessions`. Reviewer checks real gate placement, immutable reservation provenance and unchanged legacy independent calls before 8B.

### Task 8B: Scoped coordinator, M4 preflight and immutable publication

**Files:**
- Create: `KhaozEngine.MapDoc/Storage/MapScopedSurfaces.cs`, `MapScopedAcquisition.cs`, `MapBoundAcquisition.cs`, `MapAcquisitionWork.cs`, `MapAcquiredFacts.cs`.
- Create: `KhaozEngine.MapDoc/Identity/MapScopedIdentity.cs`, `MapWitnesses.cs`, `KhaozEngine.MapDoc/Surfaces/MapLatticeRanges.cs`.
- Create fixtures: `KhaozEngine.MapDoc.Tests/Storage/AnchorFixtures.cs`, `ScopeFixtures.cs`, `AcquisitionBoundFixtures.cs`, `AcquisitionConformanceFixtures.cs`.
- Apply frozen tests: `KhaozEngine.MapDoc.Tests/Storage/ScopedAcquisitionTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/ScopedAcquisitionWorkTests.cs`.
- Create: `KhaozEngine.MapDoc.Tests/Storage/ScopedProducerConformanceTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/ScopedProducerBoundWorkTests.cs`.

**Interfaces:**
- Consume 8A's public session. Copy/validate request and asset digests before opening one `using` session. Never obtain required refs by rebuilding a dictionary from legacy `source.Surfaces`.
- Produce all canonical Task 8 consumer types in their prescribed namespaces and shape. `MapScopedSurfaces.Surfaces` is `IReadOnlyList<MapSurfaceRef>` containing only needed captured refs, with detached nested tags. Property types are `MapAcquireStatus Status`, `string? Detail`, `MapSurfaceScope Scope`, `MapCoverageWitness Witness`, `MapScopedIdentity Identity`, `MapReadWitness ReadWitness`, `int PagesRead`, `int RecordReads`.
- Exact public methods remain `Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, int queryPolicyVersion = 1, int buildPolicyVersion = 1)`, `CompleteView(MapSurfaceSet set)`, `MapPatchRead Patch(MapPatchKey key)`, `bool TryRecord(MapRecordRef r, out MapTopologyRecord? record, out MapPatchStatus status)`, `IEnumerable<MapTopologyRecord> RecordsIn(MapPatchKey key)`.
- Retain internal `Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, MapAcquisitionWork work)`, `TryAcquiredPatch(MapPatchKey key, out MapSurfacePatch? patch, out MapPatchStatus status)` and `int PatchClones`. `MapAcquisitionWork` has canonical `long LargestBoundSlotRange`, `long BoundSlotVisits` observation fields, never budget authority.
- `MapLatticeRanges.CellRect(MapLatticeFrame frame, MapPatchKey lattice, int slotCell) -> MapExactXz[]` and `CellRange(MapLatticeFrame frame, MapExactXz min, MapExactXz max) -> MapCellRect` implement canonical exact positive-area arithmetic, including NegativeZ.
- `MapScopedAcquisition` owns coordination and BFS. `MapBoundAcquisition` owns M4. `MapAcquiredFacts` owns private detached payload/record/ref snapshots and consumer copy access. Keep helpers internal, with the factory as the sole constructor path for consumer witnesses and identities.
- `ScopeFixtures.AllRoles`, `Around(WorldFrame frame,float x,float z,float half=2f,MapQueryLimits? limits=null)`, `Acquire(IMapSurfaceSource source,MapSurfaceScope scope)` and `Whole(MapDocument doc)` are the canonical fixture helpers. Scope Acquire supplies empty asset digests.

- [ ] **Step 1: Implement canonical fixtures and apply the original 12 scoped/work cases unchanged.** Implement FarAnchors and all four bound fixtures from the original definitions. Directory creation cleans up if fixture setup fails. Keep all exact assertions and arithmetic, including MicroBound and the already-acquired-range exception.

- [ ] **Step 2: Write 19 public producer/coordinator conformance cases.** `AcquisitionConformanceFixtures` supplies a trusted `R2AcquisitionProbeSource : IMapSurfaceAcquisitionSource`, its `R2AcquisitionProbeSession`, a legacy-only `R2LegacyProbeSource`, `IsolatedIncident()`, `CapThreeBounds()`, `GrossOverflowBounds()` and `AssertNoFacts(MapScopedSurfaces view)`.

The probe wraps fixed detached metadata/patch reads and a scripted finite Find result. Its source `Surfaces` getter and legacy callbacks count invocations, and its session records ids looked up, explicit keys read, incidents queried, reserved keys and disposal. It implements truthful lifecycle and before-read reservations for normal tests. Fault injection is explicit in coherence/exception tests only. Fake observations prove coordinator behavior, never built-in backend admission.

`AssertNoFacts` asserts empty Witness.SurfaceIds/Present/KnownEmpty/Records/Unavailable, Identity.Patches/Records/KnownEmpty and view.Surfaces, and false Witness/Identity/ReadWitness.Complete. It also checks no acquired patch or record is exposed by Patch/TryRecord/RecordsIn. It does not require empty captured request/asset-policy metadata.

| Named test and rows | Fixed assertions and setup |
| --- | --- |
| `Acquire_CapableCustomProducerAndExplicitEmptyIncidentsComplete` | One present seed, true/empty incidents. Complete, exact copied ids/frame/root, source legacy callback counts=0, legacy Surfaces getter count=0, one Find, Dispose=1. Surface lookup only for the seed's id. |
| `Acquire_CopiesRequestBeforeOpeningTheSession` | Supply mutable Roles, SpaceIds and asset-digest arrays. During OpenAcquisition the probe mutates those original arrays. Assert the session receives the pre-Open copied request and the published scope/identity retain the original roles, space ids and asset digests. No public collection exposes the original arrays. |
| `Acquire_LegacyOnlySourceRefusesBeforeCallbacks` | Assert required unsupported message, Find/Read/Surfaces callback counts all 0. No dry/empty fallback. |
| `Acquire_UnknownIncidentsPublishIncompleteFacts` | Present seed, false/null incidents. Incomplete, exact `MapUnavailable("patch",seed,Unloaded)`, detail contains `incident`, all three completeness flags false, RequireComplete throws `incomplete`, no fabricated KnownEmpty for seed. |
| `Acquire_IncidentOnlyAnchorIsIndependentOfEveryOtherPath(MapPatchStatus status)` with Present/Missing | `IsolatedIncident`: native ground metre lattice, seed ground(0,0), remote ground(300,0), flat 4x4 patches. Only remote contains authored chain `isolated-rim` with ground corners (1,0),(1,4), heights 1000. No seed records, footprints or forward refs. Stored index incidents on seed contain that exact ref. Complete reads remote once and includes the record. Delete its payload for Missing: exact unavailable record ref/Missing, Incomplete, never empty. Check remote is absent from seed and all bound/forward paths. |
| `Acquire_UndeclaredRequiredSurfaceRefusesAndDisposes` | Two controls in one Fact: present seed names an undeclared surface id, then declared seed footprint names an undeclared lower bound. Each throws MapDocumentException before dependent key reads, records requested missing id, Dispose=1. |
| `Acquire_ReservationsDistinguishExplicitStatusesFromSentinels` | In one Fact loop Present/KnownEmpty/Missing/Corrupt/Unloaded explicit A reads, each reservation contains A exactly once. Add a covering-page sentinel C with the same Detail as an explicit unavailable read, plus a range-only known-empty fact. Neither reserves C or range slots. Repeated A never increases count. Assert precise unavailable statuses where applicable and detached ordinal reservation snapshots. No parsing Detail or enumerating a range for accounting. |
| `Acquire_AnchorKeysConsumeCandidateCapacityBeforeReads` | Seed has two allowed record refs anchored at distinct B/C. MaxCandidatePatches=2, MaxRecordReads=64 and ample depth. Seed+B exhaust cap, C callbacks=0, result CapacityExceeded and AssertNoFacts. B/C share no anchor. Separate record-read and depth cases remain original tests. |
| `Acquire_RejectsIncoherentProducerFactsBeforePublication` | One Fact loops wrong returned key, wrong Find snapshot, wrong Find scope, stale semantic digest and a collateral reserved key after explicit read. Each is explicit producer misconduct: MapDocumentException and Dispose=1, no result published. Recompute payload digest and compare, do not trust strings. |
| `Acquire_DisposesEveryTerminalSession(string terminal)` with Complete/Incomplete/FindCapacity/ReadCapacity/ExactOverflow/Unexpected | Inject each path after Open. Dispose exactly once. Capacity and exact outcomes satisfy AssertNoFacts. Exact detail contains `overflow`. Unexpected InvalidOperationException propagates unchanged. Find capacity publishes no internal reservations as facts. Successful and incomplete views never call back after return. |
| `Acquisition_DeepCopiesAllNestedRecordsAndNeededMetadata` | Start with `TopologyRecordFixtures.WithEveryRecordKind()`. Before capture, replace a space record with a copy whose DomainTags is the mutable array `["space-original"]`, and attach a MapIndoorSpan with mutable DomainTags `["span-original"]` to a surface ref. Use an existing space record/anchor for ParentSpace and valid offsets 0/400. Capture the resulting synthetic native document, then acquire it. Mutate producer buffers, every public Patch copy, TryRecord results and RecordsIn results: seam Pairs, chain Vertices, portal Interval, opening/footprint SlotCells, link Openings/Portals/GeometryOwners, space DomainTags/Walls/Portals/Links, ref IndoorSpan.DomainTags. Subsequent copies, record values and all digests remain unchanged. Public copies may be mutable, never shared with view-owned lists. |
| `Acquire_BoundReadsDoNotStartAnotherRecordExpansion` | Seed footprint requires one excluded-role lower bound key. Put a record there referencing a third anchor. Assert lower key read, third anchor never read, RecordReads unchanged by bounds. Bound-ref metadata is still requested despite scope role exclusion. |
| `CompleteView_CapturesEveryResidentFactWithoutBoundEnumeration` | Use canonical AcquiredBounds set. CompleteView has origin frame, both patches and records, full resident-slot scope `[0,64] x [0,64]`, surface-ref-only root and zero page/record reads. Its public API exposes no bound-visit counter. In the same Fact, use MicroBound with micro cell unit `1/1024`: the finite 256-slot lower range plus the top patch would exceed ordinary M4 capacity, but CompleteView returns Complete with its two resident patches. Source review verifies no bound-enumeration call. Mutate the original set after return and assert unchanged view/digests. Empty set is a complete empty view with valid origin scope. |

- [ ] **Step 3: Write the two new M4 observation Facts.** `ScopedProducerBoundWorkTests` uses the internal factory overload, exact reserved sets and real `MapAcquisitionWork` observations.

`CapThreeBounds()` scripts explicit known-empty A=`seed(0,0)` and present B=`top(0,0)`, cap 3. B is a one-cell patch at slot-cell 1, world x `[1,2]`, z `[0,1]`, top unit 1. Its footprint lower is `low` with cell unit `3/128`, upper top. The one lower range maps to cells x `[42,86)`, z `[0,43)`, hence low slots x `[0,2)`, z `[0,1)`: exactly two fresh keys in ONE range. A is on a different surface. Only one allowance slot remains.

```csharp
// BoundPreflight_UsesKnownEmptyReservationsBeforeOneOverLimitRange
Assert.Equal(MapAcquireStatus.CapacityExceeded, view.Status);
AcquisitionConformanceFixtures.AssertNoFacts(view);
Assert.Equal((2L, 0L), (work.LargestBoundSlotRange, work.BoundSlotVisits));
Assert.Empty(probe.Session.ExplicitReads); // A and B were served inside Find, no later bound read
Assert.Contains("bound slots", view.Detail);
```

`GrossOverflowBounds()` has a one-cell ceiling lattice with cell unit `int.MaxValue/1`, a lower lattice `1/int.MaxValue`, one footprint at cell 0 and Ceiling-only scope around `(0.5,0.5)` with half 0.25. Exact lower cell maximum is `int.MaxValue * (long)int.MaxValue`, representable as long. Each slot width fits long, but their checked product overflows. `BoundPreflight_GrossCountOverflowIsCapacity` asserts CapacityExceeded, `bound slots`, AssertNoFacts, zero bound visits/reads. It must not map this count overflow to NotRepresentable. Keep original AdversarialUnits for exact overflow.

- [ ] **Step 4: Run RED.** `wa_test t8b-red "$MAPDOC" "FullyQualifiedName~ScopedAcquisitionTests|FullyQualifiedName~ScopedProducerConformanceTests"`. Expected compiler failure names `MapScopedSurfaces`/consumer types/`MapLatticeRanges`, not fixture mistakes. Then internal RED filter `FullyQualifiedName~ScopedAcquisitionWorkTests|FullyQualifiedName~ScopedProducerBoundWorkTests` for absent work/factory overload.

- [ ] **Step 5: Implement the canonical coordinator and immutable results.** Check opt-in before legacy callbacks, copy request/assets and open one session. Find once, copy exact ReservedPatchKeys, then BFS with canonical ordering and separate depth/additional-anchor-read checks. Use only allowed space Parent/AliasOf references, excluding Walls/Portals/Links direct expansion. Incident lookups on served seed/anchor patches remain distinct from direct refs.

Before each fresh anchor, preflight candidate capacity and record-read allowance. After its read, reconcile the exact requested reservation set. Missing/corrupt/unloaded retains precise unavailable facts. Unknown incidents yield the approved incomplete row. Needed surface refs come through charged TryGetSurface, undeclared refs refuse explicitly.

M4 uses every reserved discovery/anchor key whatever status plus bounded pending keys. Count gross with checked arithmetic, subtract overlap by scanning bounded known keys, compare fresh against remaining before enumerating that range, skip fresh-zero ranges, and collect the ENTIRE fresh bound requirement before any bound read. Covered-empty ranges and sentinels are not explicit known keys. Bound reads do not recurse. Do not add a stronger ban on visiting an earlier fitting range before a later range refuses.

On capacity/exact refusal discard all temporary facts before witness construction. Preserve pages/record statistics as observations without exposing content. Snapshot needed refs, payloads and nested records before publication. Public copy access clones every call, internal TryAcquiredPatch returns only the private acquired instance for later read-only consumers. CompleteView follows canonical semantics without sessions or bound enumeration.

- [ ] **Step 6: Run GREEN and explicit-file format.** `wa_test t8b-green-mapdoc "$MAPDOC" "FullyQualifiedName~ScopedAcquisitionTests|FullyQualifiedName~ScopedProducerConformanceTests"` expects 29 cases. `wa_test t8b-green-editor "$EDITOR" "FullyQualifiedName~ScopedAcquisitionWorkTests|FullyQualifiedName~ScopedProducerBoundWorkTests"` expects 4. Run the diagnostic format gate with every 8B source/test path explicitly listed.
- [ ] **Step 7: Commit and stop for review.** Stage only 8B files, subject `feat(mapdoc): coordinate immutable bounded scoped acquisition`. Review all original scoped assertions, factory ownership, unknown incidence and M4 before 8C.

### Task 8C: Whole scheme-2 identity and original acceptance closure

**Files:**
- Create: `KhaozEngine.MapDoc/Identity/MapAuthoredIdentityV2.cs`, `MapAuthoredIdentityProjection.cs`, `KhaozEngine.MapDoc/Storage/MapStoredSurfaceSource.Identity.cs`, `MapWholeIdentityWork.cs`.
- Modify: `KhaozEngine.MapDoc/Storage/MapStoredSurfaceSource.cs` (immutable original resolver/recipe/root asset retention), `KhaozEngine.MapDoc.Tests/SurfaceStorageFixtures.cs` (canonical Apply and SemanticSnapshot only).
- Apply frozen tests: `KhaozEngine.MapDoc.Tests/Identity/SchemeTwoIdentityTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/SchemeTwoRepackTests.cs`.
- Create: `KhaozEngine.MapDoc.Tests/Identity/SchemeTwoEditingIdentityTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/StoredWholeIdentityTests.cs`.
- Modify living documentation: `KhaozEngine.MapDoc/README.md`, `docs/USING-KHAOZENGINE.md` for the newly implemented session and identity contracts only. No future R2 capability claims.

**Interfaces:**
- New public `MapAuthoredIdentityV2.Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options) -> string` and `Compute(MapStoredSurfaceSource source, MapAssetClosure assets, MapResolveOptions options) -> string` in `KhaozEngine.MapDoc.Identity`.
- New stored immutable internal properties `MapResolverIdentityDoc? ResolverIdentity { get; }`, `MapSupportRecipe SupportRecipe { get; }`, with private detached read-only original `NativeAssets` retained at original manifest parse. No public manifest accessor or mutable globals document.
- New internal `IEnumerable<MapSurfaceIndexEntry> EnumeratePinnedEntries(MapWholeIdentityWork? work)` verifies original directory/index closure and yields canonical key order. It never uses a scoped query or a 256-candidate ceiling. Reuse stored `Payload(entry)` for existing byte/index/semantic validation, hashing one returned payload before reading the next.
- New internal `Compute(MapStoredSurfaceSource source, MapAssetClosure assets, MapResolveOptions options, MapWholeIdentityWork work) -> string` observes actual `PayloadReads` and `MaximumPayloadsHeld`. Work counters surround the single payload verification/hash lifetime, never define completeness or budget correctness.
- `MapAuthoredIdentityProjection` is internal shared hashing of the exact canonical Task 8 projection. Reuse `MapCanonical.HashHex`, `MapSurfaceSemantics.RootDigest/SurfaceDigest/PatchDigest` and verified closure Hash. Do not call V1 MapAuthoredIdentity for resolver 2.
- `SurfaceStorageFixtures.Apply(MapDocument doc,string change)` uses the original five mutations exactly. `SemanticSnapshot(MapDocument doc)` becomes scheme-2 with Assets and `new MapResolveOptions("headless",1,"options",ResolverVersion:2)`, preserving Task 7 rollback tests.

- [ ] **Step 1: Apply the original 10 identity/repack cases unchanged and add three public editing/root cases.** `SchemeTwoEditingIdentityTests` uses V2 options and the existing Assets fixture.

```csharp
[Theory, InlineData(false), InlineData(true)]
// SchemeTwo_CompleteUnsavedEditsUseCurrentSemantics(bool fromTiled)
// false: ThreeSurfaces directly. true: save then LoadTiled WHOLE, assert !Tiles.IsPartial.
// Compute before, mutate ground(0,0).Heights[5]++, compute without saving -> valid new token.
// Clone equivalent current authored values with Tiles=null -> equal new token.
// Persisted baselines/ref hashes are not corruption certificates for unsaved complete editing.
// SchemeTwo_AssetRootMembershipMatchesTheVerifiedClosure
// ThreeSurfaces with one root removed or a digest changed -> MapDocumentException.
// Matching original roots compute. Do not load an unverified replacement closure implicitly.
```

- [ ] **Step 2: Write five internal stored identity cases.** `StoredWholeIdentityTests` uses actual stored callback observations and existing `MapTiledFile.Save(..., new MapSurfacePacking(...))` for public synthetic layouts.

| Named test and rows | Fixed assertions and setup |
| --- | --- |
| `StoredIdentity_LegacyMetadataRefusesBeforePayloadWork(string invalid)` with resolver/recipe/options | Resolver row: use a valid resolver-1/LegacyXzCallbackV1 native document without authored surfaces, save and Open it, then Compute with V2 options must refuse with work.PayloadReads=0. Options row: valid resolver-2 source plus ResolverVersion=1 options, Compute refuses with work.PayloadReads=0. Recipe row: save a valid resolver-2 source, change only the manifest recipe to LegacyXzCallbackV1, and assert Open itself refuses with the recipe validation reason. ReadManifest validates this pairing before a source exists, so do not claim that row reaches Compute. Delete payload files in that row before Open and require the recipe error rather than a payload error, preserving the parser-before-payload causal control. No validation bypass, rescue or current-manifest reread. |
| `StoredIdentity_LazyClosureStreamsOnePayloadAtATime` | Save FlatPatches(257), Open with all indices initially unread. Compute succeeds, equals whole in-memory token, actual PayloadReads=257 and MaximumPayloadsHeld=1. This is a finite 257 small 4x4 patch control, not a stress run or total-memory claim. Deleting a required payload refuses Corrupt/Missing explicitly, never computes from index digests alone. |
| `StoredIdentity_UsesOriginalPinnedClosureAfterManifestReplacement` | Open old valid ThreeSurfaces. Save another valid generation in the same directory after changing one height, which sweeps old bytes. Old source's RootSha256/ResolverIdentity/SupportRecipe stay original. Old Compute refuses missing old payload, never returns replacement token. New Open computes replacement token. Also verify original root-membership retention by changing replacement manifest roots after old Open. |

- [ ] **Step 3: Run RED.** `wa_test t8c-red "$MAPDOC" "FullyQualifiedName~SchemeTwoIdentityTests|FullyQualifiedName~SchemeTwoEditingIdentityTests"`. Expected compiler failure names `MapAuthoredIdentityV2` or canonical Apply. Then internal RED `FullyQualifiedName~SchemeTwoRepackTests|FullyQualifiedName~StoredWholeIdentityTests` for absent identity traversal/work APIs.

- [ ] **Step 4: Implement strict whole identity.** For MapDocument, check window first and include `window` in refusal, then `(1,2)`/AuthoredBindingsV2/options resolver 2 and valid builder fields. Complete edited data uses current semantic digests even when saved baselines are stale. Reuse `MapBoundDocumentValidation.Validate` for complete document local/closure validation after the Task 8-specific preflight, not as a stored lazy-source validator.

For stored input, validate retained original resolver/recipe/options and original root membership before payload reads. A freshly opened source is lazy, not a requested window. Verify every original directory/index/payload address and recomputed patch semantic digest, with precise Corrupt/Missing refusal. Verify recomputed per-surface semantic aggregates against pinned refs and hash the original root plus verified ordered patch facts. Keep one payload at a time, allow index metadata caches, never hydrate a whole payload document, reread a newer manifest, use a giant scope or accept cached semantic digests without payload verification.

Hash exactly RootDigest, ordered `(patchKey,semanticDigest)`, closure hash, builder id/version, options hash and resolver version under `kemap/native-authored/2`. Exclude storage/embedding/packing/incidents. Keep scoped hashing exactly as canonical Task 8. Whole projection must not reject valid complete edits by comparing their resident values against old storage baselines.

- [ ] **Step 5: Run GREEN and full Task 8 closure once.** `wa_test t8c-green-mapdoc "$MAPDOC" "FullyQualifiedName~SchemeTwoIdentityTests|FullyQualifiedName~SchemeTwoEditingIdentityTests"` expects 12 cases. `wa_test t8c-green-editor "$EDITOR" "FullyQualifiedName~SchemeTwoRepackTests|FullyQualifiedName~StoredWholeIdentityTests"` expects 6.

After those pass, run the changed Task 7 storage regression filters once because SemanticSnapshot changed. Run explicit-file diagnostic format with every 8C `.cs` path. Record the three substep logs as Task 8's 64-case proof, do not repeat all passing tests just to produce another count. Canonical final solution build/test/full format and repository guards still run at the later controller candidate gate.

- [ ] **Step 6: Update living usage and verify documents.** Explain custom producer conformance, reserved-key provenance, true-empty versus unknown incidence, one session budget and visits versus decodes, unsupported-source refusal, pinned whole identity and scoped immutable witnesses. Sweep Markdown for changed names and old behavior. Run canonical dashes/prose/file-size/doc-version guards through wa_run, each once, exit 0. No engine version or changelog change in this substep.
- [ ] **Step 7: Commit and stop for review.** Stage only 8C paths, subject `feat(mapdoc): verify scheme-2 identity from pinned authored facts`. A fresh reviewer checks unsaved editing versus stored corruption, lazy whole streaming, original 22 cases, closure membership and documentation. Controller records the reviewed Task 8 boundary before later work.

## Case inventory and stop conditions

| Added class | Methods | Theory rows | Added cases | Owner |
| --- | ---: | ---: | ---: | --- |
| SurfaceAcquisitionSessionTests | 3 | 0 | 3 | 8A |
| SurfaceAcquisitionBudgetTests | 8 | 4 | 10 | 8A |
| ScopedProducerConformanceTests | 13 | 8 | 19 | 8B |
| ScopedProducerBoundWorkTests | 2 | 0 | 2 | 8B |
| SchemeTwoEditingIdentityTests | 2 | 2 | 3 | 8C |
| StoredWholeIdentityTests | 3 | 3 | 5 | 8C |
| Total added | 31 | 17 | 42 | |
| Original prepared cases | 16 | 8 | 22 | 8B/8C |
| Entire Task 8 | 47 | 25 | 64 | |

Final project counts for these exact classes are MapDoc.Tests 44 and MapEditor.Tests 20. Embedded controls and loops are assertions within those cases, not extra discovery counts. Existing Task 7 regressions add their own current counts and must not be reported as new Task 8 cases.

Stop and report a concrete blocker for source/API drift, an approval gap, first RED failing for unrelated setup, any required contract that cannot be tested finitely, a need to weaken a frozen assertion, or a review finding outside the worker's scope. Do not invent a null/dry fallback, increase allowances, silently broaden source support, scan a whole world during scoped acquisition, add geometry work or advance Task 9 to make Task 8 pass.

Parent self-review completed the spec coverage, checkable-step, type/signature, Review Focus and proportion checks. Independent review returned Ready, with its nonempty nested-tag fixture clarification incorporated. These are source/design checks only. Owner written-plan approval is recorded as OA19. These reviews do not establish a runtime result or Task 8 implementation completion.
