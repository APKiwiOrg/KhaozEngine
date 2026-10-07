# R2 Task 8 producer acquisition completion

Status: written contract approved directly by the owner on 2026-10-08 (OA18).
Approved revision: `86890fe22b340b483eaeed936dfb766b758ea53f`, original SHA256
`7d61c91e7b07963267f631b62b5b4055656ed21c72df0bcb7fed0953592ab800`.
Executable-plan reconciliation and review precede producer-session implementation.
Baseline: Task 7 checkpoint `e0e975cb4c2e0768b51d740fe14ff31e1ee08101`.
This completes the incident-discovery and before-work budget requirements of the approved R2 plan.
It does not reopen cave geometry, rational lattices, privacy, native ownership or release gates.

## Problem and intended result

`MapScopedSurfaces.Acquire(IMapSurfaceSource, ...)` must discover incident-only record anchors and
share one bounded read allowance across initial discovery, anchor reads and bound acquisition.
The current producer interface supplies neither incident metadata nor shared before-read accounting.
Adding independent calls' decoded-page counts afterward cannot enforce the promised work boundary.

The stored whole-identity overload also needs the parsed resolver and support-recipe facts. Those
can be retained internally from the manifest already read at Open. They require no general public
manifest accessor.

The result must preserve custom trusted producer participation, immutable captured/pinned generations,
strict capacity refusal before excess work and factory-only consumer witnesses. Unknown incident data
must never certify an empty list. No new sampler, global registry, residency scheduler or physics lease
is introduced.

## Alternatives

Scores are design judgments from 1 to 10, higher is better. Weighted totals use the stated percentages.

| Criterion | Weight | Built-in internal session only | Opt-in public producer session |
| --- | ---: | ---: | ---: |
| Incident truth and before-work correctness | 35% | 10 | 10 |
| Custom large-map streaming participation | 25% | 4 | 10 |
| Small compatibility and API footprint | 20% | 10 | 8 |
| Implementation and proof simplicity | 15% | 8 | 6 |
| Clear finite lifetime | 5% | 8 | 10 |
| Weighted total | 100% | 8.1 | 9.0 |

An internal session is smaller and keeps public interfaces unchanged, but would make the generically
typed acquisition API support only the two built-in sources. Converting an external streaming source
to a whole captured document is not a sound large-world substitute.

The recommended opt-in session adds a small producer contract and conformance obligations. Existing
independent source methods remain unchanged. Capable custom sources can supply the same truthful facts
and strict budgets as built-ins. Unsupported sources are refused explicitly rather than silently
wrapped with incomplete discovery or weaker accounting.

## Approved public contract

All new types are in `KhaozEngine.MapDoc.Storage`. Existing source and result signatures remain unchanged.

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
    bool TryGetIncidentRecords(MapPatchKey acquiredKey,
        out IReadOnlyList<MapRecordRef>? records);
}

public sealed class MapSurfaceCapacityException : Exception
{
    public MapSurfaceCapacityException() { }
}
```

The existing internal capacity exception becomes public so a trusted external producer can report
preflight exhaustion without a misleading patch status or exception-message matching. The acquisition
factory maps it to `MapAcquireStatus.CapacityExceeded`, publishing no partial content.

`Acquire` retains its `IMapSurfaceSource` parameter. Without the opt-in capability it throws
`MapDocumentException` containing `surface source does not support bounded scoped acquisition`, before
calling either legacy read method. This configuration refusal does not mean dry, clear or unreachable.
No default implementation supplies empty incidents or resets budgets around legacy calls.

`TryGetSurface` charges the shared metadata allowance before an indexed lookup. It returns immutable
metadata from the pinned root. False means undeclared, not an empty geometric region. A required
undeclared surface refuses acquisition with MapDocumentException. Built-ins prepare that lookup once
at source Open/Capture, not by rescanning or copying all refs per acquisition. The factory obtains only
needed refs through this method. It must not rebuild a whole-world ref dictionary through the legacy
Surfaces list during a scoped operation.

`TryGetIncidentRecords` operates only on a patch already served by that session. It performs no I/O,
query or global scan. True with an empty list certifies no incidents. False means unknown and produces
an incomplete acquisition with `MapUnavailable("patch", key, Unloaded)` and an incident-metadata detail.
Missing/corrupt storage retains its precise existing status. Producer lists are detached read-only copies.
They are trusted producer facts, not factory-only consumer certificates.

`ReservedPatchKeys` is the authoritative global unique-key reservation set for that session. Each
access returns an ordinal key-ordered, detached read-only snapshot, bounded by MaxCandidatePatches.
The session reserves a new key BEFORE attempting that explicit patch read, including a read that later
returns KnownEmpty, Missing, Corrupt or Unloaded. Initial Find discovery uses this same set before
candidate payload work across both captured passes, not a per-pass or merged-Present count. ReadPatch
reserves its requested key in the same set before lookup/I/O. Repeated keys do not reserve again.
It must not perform hidden collateral payload reads under another key.

Covering-page unavailable sentinels and range-only known-empty facts do not reserve keys. The factory
never infers reservations by parsing Detail strings, enumerating empty ranges or treating a sentinel's
minimum slot as an explicit read. After Find it copies the exact reservation set. After each explicit
anchor read it incorporates the requested key and checks coherence with the bounded session set.
M4 uses this exact set, regardless of read status, plus its own pending bound keys. It computes the
entire fresh bound requirement against the actual remaining candidate allowance BEFORE any bound read.
A later session refusal is not a substitute for that preflight. On Find capacity refusal the factory
returns no facts, regardless of reservations already consumed internally.

A complete captured source uses its computed incidence. A partial capture preserves the stored incident
list for an existing indexed patch. A new partial-window patch cannot be certified merely because a
resident-only scan finds no incidents. Unknown metadata remains incomplete.

## Lifetime and accounting

1. Acquire validates and privately copies scope and asset-digest inputs, then opens one session in a
   using scope. Opening captures request and identity metadata only and reuses immutable metadata already
   owned by the source. It does not load world pages, copy all world refs or rebuild global incidence.
   Snapshot, root, referenced surface metadata and requested frame stay fixed.
2. Find executes once before additional key reads. Out-of-order use or use after disposal refuses
   before producer work. Session disposal happens on success and every refusal/exception. It releases
   only session resources, never refreshes a generation, sweeps files or evicts shared cache state.
3. One call-owned budget covers initial find, both captured-source passes and merge, every anchor read
   and every bound read. Page visits charge before cache checks, including cached/unread pages. Lookup
   nodes and surface refs charge before bounds/role inspection. Range bookkeeping charges before its
   work. Decode attempts charge before storage I/O. No reset, refund or post-read repair is permitted.
4. Retain Task 7's separate finite metadata and range allowances, each
   `1 + 4 * 256 * MaxPageReads`. Session PagesRead is cumulative successful new directory/index decodes.
   Each returned result's PagesRead is that operation's delta. Cache hits can exhaust work while decoding
   zero pages. The original FarAnchors decoded count of five remains meaningful.
5. The factory owns canonical breadth-first expansion and MaxRecordDepth/MaxRecordReads checks.
   MaxRecordReads counts additional anchor patch reads, preserving the approved fixture's two-read
   assertion. Space Walls, Portals and Links lists remain excluded from direct expansion.
6. MaxCandidatePatches is one global unique patch-key allowance owned by the session across discovery,
   additional anchor reads and bound reads. Factory preflight uses ReservedPatchKeys, not only visible
   Present rows. A captured pass returning KnownEmpty still consumed its explicit key before the next
   pass reads a different key. The factory checks anchor admission and M4 bound admission before calling
   the session. This does not permit 256 seed patches plus 64 extra anchor patches outside the cap.
7. Preserve M4's checked bound-slot count before enumeration, known-key overlap subtraction and zero
   visits for an already-acquired range. Overflowing the range count is capacity, exact arithmetic
   overflow is NotRepresentable. Bound reads do not trigger further record expansion.
8. Before publication, copy payloads, records, needed surface refs and every consumer witness/identity collection. Validate
   returned keys, snapshot and scope coherence and recompute patch semantic digests as planned.
   Capacity or exact overflow publishes no facts. Incomplete storage/incident data cannot become a
   complete witness. Nothing in an acquired view rereads its producer after return.
9. Session coordinate/range arithmetic reports MapExactOverflowException. Built-ins normalize their
   known coordinate-wrapper paths internally, without message matching or reclassifying corrupt payloads.
   The factory maps that type to NotRepresentable and disposes the session. Gross bound-count overflow
   remains CapacityExceeded as prescribed by M4. Unrelated producer exceptions propagate after disposal.

Custom producers must honor these same units and lifecycle. They remain trusted for source facts,
just as under IMapSurfaceSource today. The factory cannot make an arbitrary dishonest producer safe.
Consumer witnesses stay sealed, internally constructed and get-only.

## Stored whole identity

Retain these immutable internal facts on MapStoredSurfaceSource from its original manifest parse:

```csharp
internal MapResolverIdentityDoc? ResolverIdentity { get; }
internal MapSupportRecipe SupportRecipe { get; }
```

If closure validation needs root asset membership, retain a private read-only copy of NativeAssets.
Do not retain a publicly mutable MapDocument or reread a newer manifest. Validate resolver options 2,
parsed `(1, 2)` and AuthoredBindingsV2 before payload work.

Compute(MapDocument, ...) refuses a requested partial view with `window`. A newly opened stored source
is lazy, not a requested window. Its whole overload enumerates and verifies the pinned closure and
streams one payload at a time, rather than rejecting initial IsPartial residency or using a giant scope.
Decoded index caches remain, so this is not a constant-total-memory claim.

Whole identity keeps `kemap/native-authored/2`. Scoped identity keeps `kemap/scoped/1`, the pinned root
plus acquired facts, request/frame and policies. Storage names, page packing and incident bookkeeping
are not added to semantic identity. A partial editing scoped digest is not a whole current editing hash.

## Finite acceptance additions

Keep all 22 approved Task 8 test cases and their assertions. Add bounded conformance controls for:

- A distinct incident-only anchor outside seed, allowed forward-reference and bound paths. Preserve
  FarAnchors unchanged, whose existing annex-east/door-top coanchor does not independently prove incidence.
- A capable custom producer, an unsupported legacy-only producer refused before callbacks, explicit
  known-empty incidents and unknown incidents producing incomplete results.
- Indexed surface metadata lookups with no per-acquisition world-ref copy/scan, undeclared required refs
  refusing and immutable returned metadata.
- Cold and prewarmed sessions sharing one limit across all phases, with counters proving refusal before
  the excess visit/decode. Captured two-pass work must use the same allowance despite zero decoding.
- A captured two-pass find with explicit known-empty A and present B must refuse before reading B when
  the unique-key cap is one. A capable producer must distinguish an unavailable payload read from a
  covering-page sentinel through ReservedPatchKeys, without strings or range enumeration.
- With cap three, explicit known-empty A and present footprint B leave one slot. If B needs two fresh
  bound keys in one over-limit range, M4 refuses with zero bound reads/visits, despite only B being visible in Present results.
- Additional anchor keys consuming the global candidate allowance before reads, separate from record depth.
- Cumulative metadata and range limits across phases, observed before the rejected inspection/bookkeeping,
  plus normalized exact-overflow refusal, empty facts and disposal. Decoded counts alone are insufficient.
- Per-operation versus cumulative decode statistics and disposal on every terminal path. A new acquisition
  starts a fresh budget against the same immutable source, independent of cache residency.
- Legacy stored resolver/recipe refusal before payload reads, valid lazy stored identity, and the existing
  repack, corruption, partial-view, immutable witness and pinned-root/acquired-fact controls.

No stress, private input, recorder, native game adoption or release is authorized by this amendment.

## Source evidence and implementation boundary

At the Task 7 baseline, inspect `Storage/IMapSurfaceSource.cs`, `MapStoredSurfaceSource.cs`,
`MapDocumentSurfaceSource.cs`, `MapSurfaceStorageIndex.cs`, `MapSurfaceQuery.cs` and
`MapSurfacePageLookup.cs`. Incidents are internal index entries, independent reads create their own
budgets, and stored root globals are available in MapTiledFile.ReadManifest but not retained for the
identity overload. The approved Task 8 section requires all three missing facilities.

Owner approval covers this producer contract completion and its explicit refusal/cap semantics.
Reconcile the executable Task 8 steps and file/test ownership, review that plan amendment, then
execute through the existing subagent and shared compute workflow. Existing R2 geometry,
precision, privacy and release prerequisites remain unchanged.
