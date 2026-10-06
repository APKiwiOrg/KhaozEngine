# Cold-selection scope acquisition amendment

Status: approved by the coordinator under delegated authority on 2026-10-07, with migration CS1
and the immutable-witness/readiness clarifications below. Record precedes shared production edits.
F3's native G1b and world/release gates remain unchanged. No heavy run is authorized by this approval.

Migration's durable CS1 record is
[`SWIMMING-COLD-SCOPE-REVIEW.md`](https://github.com/APKiwiOrg/Grimhollow/blob/aec2e2443121da5d6fe124573d5c693b522b12d7/docs/superpowers/programs/world-authoring/SWIMMING-COLD-SCOPE-REVIEW.md).
It reviewed readiness revision SHA-256 d72bbc015502dfec118cf9896e460555c45825fc137c275ea2f12eda6c9a04fb.
The coordinator separately approved the production amendment and all CS1 conditions.

## The acquisition cycle

F3 requires canonical selection to be discarded and rebuilt after correction, cold restore or
handoff. `FramedMovementState.Selection` can therefore be null. The current approved scope shape
requires a valid nonnullable `MovementSpaceKey CurrentSpace`, and its implemented constructor
rejects default keys. `RebuildSelection` needs an acquired environment lease. A cold caller cannot
construct that scope without already knowing the canonical space it is asking the producer to find.

The source evidence is `MovementQueryScope.IsValid` and its constructor in the current swimming
branch, plus F3 section 3's reconstruction requirement. No runtime failure or native implementation
is claimed. The approved R2 producer plan already acquires a bounded surface scope before calling
`MapSpaceMembership.Query(MapFramePoint)`, which does not require a prior SpaceId.

## Recommended scope shape

Add an explicit required world identity and make the current-space hint nullable:

```csharp
public string WorldId { get; }
public MovementSpaceKey? CurrentSpace { get; }

public MovementQueryScope(Vector3 min, Vector3 max, float maxRise, float maxDrop,
    string worldId, MovementSpaceKey? currentSpace,
    MovementQueryIdentity identity, MovementFrameDescriptor frame);
```

Retain the existing constructor as a forwarding convenience when a known current space is present.
It derives WorldId from that valid key. The new constructor rejects blank WorldId, an invalid
present key, or a key from another world. Null means no canonical selection has been established.
It is never a known dry/traversable space.

The provider exposes its served `WorldId`, bound to the actual native world by its factory. Context
acquisition checks the requested WorldId against that binding. A caller cannot relabel a world by
supplying a different string. Native G1b must prove the producer's binding to its actual served root.

Witness/request containment requires matching WorldId and exactly matching nullable hints, in
addition to the existing identity, frame and full bounds/envelope checks. A certificate acquired
with a selected-space hint cannot establish unknown-space reconstruction by dropping that hint.
The producer certifies all membership dependencies needed in the declared bounded scope for a
null hint, under the same finite work limits. It returns explicit refusal when unable to do so.
No whole-world enumeration, new sampler or directory validator is introduced.
CS1 requires all bounded candidate membership dependencies, including stacked spaces, enclosing
vertical bounds and aliases. Neither a discarded SpaceId filter nor Y culling may hide a required
ceiling/floor. Native and facade limits both apply. A hint that changes semantic dependencies changes
the scoped semantic digest. Physical repacking alone does not.

Generic result world checks use Scope.WorldId. Body and support queries still require a valid
canonical space. After `RebuildSelection` returns Known, those queries use its selected space
under the same immutable witness. Local selection hints and backing IDs do not change portable
semantic identity under physical repacking.

### Same-witness transition

The acquired null-hint witness stays immutable after reconstruction. Scope.CurrentSpace remains
null, and its identity/digest and resource list remain the originally acquired values. The returned
canonical space is supplied in subsequent body/support requests. It does not turn that witness
into a newly acquired known-hint certificate. Exact hint equality is an acquisition/containment
check, not a requirement to rewrite the scope to match later query arguments.

A null-hint lease starts with reconstruction readiness Unresolved. Selected-space body, support
and coverage requests are refused before producer access until RebuildSelection returns a valid
Known selection. A failed rebuild clears readiness even if the caller supplied a formerly valid
or predicted-future selection. Readiness resets before every reconstruction attempt and remains
unready on refusal or exception, including after an earlier successful rebuild. A later successful
rebuild can establish readiness again under the
same still-current witness. This is a local lifecycle guard, not a second membership cache or
geometry sampler. The producer still validates canonical membership and legal transitions for
each subsequent query. Known-hint leases preserve the existing selected-space entry path.
Reset occurs after the lease's thread-entry check and before any frame/scope precondition or
producer call. Wrong-thread use remains rejected without entering the owned reconstruction attempt.

## Reconstruction and verification

`MovementQueryLease.RebuildSelection(in FramedMovementState state, out MovementSelection selection)`
keeps the approved signature. Cold/correction/handoff reconstruction requires an explicitly null-hint
certificate. Clearing state.Selection while retaining a discarded hint in Witness.Scope.CurrentSpace
is insufficient. A known-hint certificate is refused and must be reacquired with a null hint.
It requires the recorded frame to equal the lease's frame and the
point to lie inside its certified scope. It passes a state with null Selection to the producer,
even if the caller supplied a discarded predicted future. Only a valid Known result in Scope.WorldId
with the matching portable identity can be returned. Airborne state cannot gain a current support
owner. Refusal clears the out selection and never relabels the input state.

Tests will acquire a null-hint scope and reconstruct the actual occupied space, reject invalid or
cross-world present hints, reject reuse of a differently hinted witness, distinguish unresolved from
known membership, and prove a corrected position cannot retain future support/space. The adapter
fixture is finite and synthetic. Actual native reconstruction remains G1b.
The finite integration fixture acquires a null-hint scope, rebuilds successfully, then performs a
valid body query while asserting the witness object, null scope hint and portable identity never
change. The failure fixture supplies a stale future selection, makes reconstruction fail, then
proves that a selected-space query cannot reach the producer or publish data through that hint.

The independent pure frame-rebinding helper will translate recorded physics origins before querying,
preserve the original framed state on refusal, and validate the existing 512 m planar / 640 m vertical
local envelopes without changing import accuracy. This does not establish canonical membership.

## Focused implementation evidence

The compile-enabled cold RED exited 1 with 19 missing constructor/method failures and one existing
known-hint A versus known-hint B rejection control passing, 20 total with no skips. The corresponding
direct-call GREEN exited 0 with all 20 cases passing and no skips, in
`/tmp/swim-cold-selection-green.log`. Each run used its explicit shared-slot grant and released
compute immediately. No extra fixture or retry ran in those windows.

The synthetic cases cover null-hint acquisition, unchanged witness after reconstruction, successful
body/support/coverage queries under it, readiness reset through refusal/throw and recovery, blocked
access through all three entrypoints, known-hint reconstruction rejection, world/hint/identity/frame
checks, missing/capacity refusal, stacked membership inputs and backing-ID repacking invariance.
The separately requested prior-environment regression window is pending. Support selection and
actual volumetric/native producer proof remain outstanding. Task 3 is not complete.
