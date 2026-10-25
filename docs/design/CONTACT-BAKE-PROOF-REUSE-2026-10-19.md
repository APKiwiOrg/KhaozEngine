# Contact bake proof reuse

Design for [#1401](https://github.com/APKiwiOrg/KhaozEngine/issues/1401), option B of the
[#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) contact-controller navigation lane.
Source baseline: `f23e19ec0b814add04dd5d19ab05ea2b5129b7b9`, staged 21.5.2.
Status: implemented under BB-R1.
[Verification](../verification/2026-10-19-bake-proof-reuse.md) records the unchanged payload/byte matrix,
measured counters and final Release checks. Living use belongs in the Physics, Bepu and Movement
READMEs and [consumer contract](../USING-KHAOZENGINE.md#exact-completed-footprint-reuse).
BR-R4 and BR-R6 supersede the original staged-version premise after the releases of 21.5.2 and 21.6.0. This lane stages 21.6.1.

This document retains the operative evidence and contracts. Product code and pinned hashes at the research's requested
`3eb6eeb22` and actual `f23e19ec0` baselines are identical. Only AGENTS.md changed between them.

## Problem and evidence

Option A partitions the contact bake into parallel row bands. Every directed edge still runs the
contact controller from a canonical initial state, repeats a Hold and reacquires movement leases
for its travel steps. Tick-local `SupportRequestMemo` already avoids identical Finds inside one
interval. It contains arena-owned evidence and cannot become a cross-lease cache.

One diagnostic construction of each pinned fixture preserved every normalized full-file hash.
It found repeated absolute physics requests across a source node's outgoing same-layer edges.
The counter denominators below record the original non-SIMD stair-wall arithmetic path. The
[proof record](../verification/2026-10-19-bake-proof-reuse.md#arithmetic-path-correction-after-main-ci)
documents the Linux x64 SIMD path and its deterministic two-sweep control. The saved-work
targets are identical, while that path performs four fewer baseline volume evaluations.

Source-local completed-footprint reuse projects to 37,095 fewer evaluations out of 79,499
(46.66%) over the seven pinned fixtures. On the 576-column fixture it projects to 29,234 fewer
out of 69,532 (42.04%). Sharing the initial Hold separately projects to 2,888 fewer of 34,992
edge movement steps (8.25%) there. These reductions overlap and must not be added.

These are work projections from exact recorded inputs, not measured cache savings or speedup.
Every diagnostic footprint was Walkable, with zero refusal and curved-work counts. Box fixtures
cannot certify curved payloads or price curved savings. The historical issue comment's poor
parallel speedup was corrected as machine load in #1409. Neither historical extrapolation is a
new idle-world timing claim or acceptance evidence for this lane.

## Binding ruling and candidate dispositions

> BB-R1: implement research candidates 1 (source-local exact completed-footprint reuse) and 2
> (same-source initial Hold reuse) as one lane. Candidates 3 and 4 are future certificate work
> with no savings booked. Candidates 5 and 6 are rejected with their reasons.

| Candidate | Disposition and reason |
| --- | --- |
| 1. Source-local completed-footprint reuse | Required. Reuse only a bit-identical absolute physics request in an authenticated unchanged selected view. |
| 2. Same-source initial Hold reuse | Required. Copy only successful numeric state under the explicit stable-provider contract. Keep the first real Hold. |
| 3. Certified horizontal stratum with exact arithmetic replay | Future certificate work, zero savings booked. Level endpoints do not certify all intermediate footprint, shell, roof, medium and arithmetic behavior. Eight level-support pool proofs already reject. |
| 4. Translated complete-edge reuse | Future certificate work, zero savings booked. The complete conservative geometry key has zero translated repeats. A column stencil is incomplete and translation does not commute with rounded arithmetic without a certificate. |
| 5. Copy reverse-direction verdict | Rejected. Four stair pairs and 28 row-band pairs have asymmetric verdicts. Independent Holds, climb/drop budgets and arrival remain directional. |
| 6. Remove unreachable regions | Rejected. Removing nodes, layers, exits or links changes bytes. This API supplies no profile start set. A separately identified scoped bake would require another owner decision. |

The evidence supports BB-R1. The research's optional wording for candidate 2 is superseded by
the ruling, not a reason to omit its stable-provider certificate. No owner question remains.

## Global constraints

- Preserve the Euclidean 0.001 m arrival ball, roof and footprint protections and raw layer identity.
- Raw physics identity remains `(StaticHandle, ElementId)`. Coincident members never coalesce.
- No tolerance, skin, band, epsilon, reach, work ceiling or existing storage limit may be widened or added to obtain a hit or meet a target.
- Preserve the independent 0.001 m placement offset, 0.0001 m contact/join band and 0.0005 m HeightError ceiling.
- Preserve 256 raw members, 512 witness slots and the cumulative 1,048,576-event curved-work ceiling, including its sticky refusal and existing storage bounds.
- The complete KENB bytes equal the current serial bake at the same engine identity for every pinned baseline and every supported parallel degree, independently of iteration order.
- Every miss, stale view, unsupported backend or refusal takes the full existing path, including its original refusal or exception when authentication fails.
- No wall-clock timing, load run, stress run, test loop, consumer launch, world bake, release or tag is authorized by this lane.
- Under finishing rulings BR-R4 and BR-R6, stage one patch bump to 21.6.1 with its own dated CHANGELOG entry and guarded declarations. Preserve released 21.5.2 and 21.6.0 history.

## Requirements

### BB-1. Reuse an exact completed physics fact, never a movement verdict

The reusable fact is the exact Complete output of `IPhysicsSupportFootprint.QuerySupportFootprint`.
It is not a `SupportSample`, selected owner, `MovementSelection`, movement outcome, producer
binding, continuity handle or old leased `SupportFootprintResult`. Movement and Locomotion
continue the normal analytic sampling, scope validation, binding, medium/body coverage, support
selection, shell checks and travel recurrence. `FootSupport` captures new current-arena evidence.
The backend certifies and publishes geometry. Movement never relabels an expired result.

### BB-2. Add a backend-neutral, non-owning reuse capability

Add `IPhysicsSupportFootprintReuseSource` and `IPhysicsSupportFootprintReuseSession` in Physics.
The exact selected receiver opens a thread-affine session using an ordinary current physics
lease. The session authenticates that view and its source, origin, exclusions and generations.
It borrows the lease for authentication only. It never acquires or retains a second explicit
read lease across movement lease acquisition. Disposing the opening lease does not invalidate
the immutable session basis, but every later lookup needs its own authenticated current lease.
Open lazily on the source group's first already-required Step lease, after its edge prechecks.
Do not add a separate movement acquisition or producer pin to open the session.

Reset the session for each source domain. A domain is the pair of immutable profile and raw
source identity tokens owned by Movement. Both use reference identity. Their complete values
are fixed for the domain lifetime. Reset clears all entries even if the next domain looks equal.
There is at most one active session for a selected receiver on a thread. Nested attempts decline
without disturbing the outer session. Unsupported or forwarding backends that cannot certify
the exact receiver decline. They retain the original path without a new mandatory capability.

### BB-3. Use the complete bitwise key and unchanged-view certificate

The logical key is the following tuple. Fields constant within a session or source domain may
be stored once in its authenticated basis. They remain part of equality and invalidation.

| Key component | Exact contents |
| --- | --- |
| Receiver and source | Exact receiver object, exact source world object and selected-view object identities. A selected view is never interchangeable with its unrestricted world or another equal-looking view. |
| Exclusions and installation | Immutable exclusion-set identity and contents, installed-static generation (`_staticGeometryVersion`), public static geometry generation and current lease geometry generation. Removed/reused static handles invalidate the basis. |
| Origin | All three binary32 origin bit patterns, including both zero signs. |
| Domain | Immutable profile token and raw-source token. The latter records capture column X/Z, original surface ordinal, raw height bits and derived layer separately. The profile token binds the full effective proof tuning, composition, area/footprint policy and build identity. |
| Disc and band | `Axis.X`, `Axis.Y`, `Radius`, `CosMaxSlope` and `ContactBand` as binary32 bits, `BandLower` and `BandUpper` as binary64 bits. |
| Axis proposal | Probe radius and length bits, Start.Position X/Y/Z bits, Start.Orientation X/Y/Z/W bits and Distance bits. |
| Foot proposal | The same complete field list independently for FootProposal. Probe object identity is not a substitute for its dimensions. |
| Filter | `QueryFilter.Mobility`, `Layers` and `CullBackFaces`, including validation of currently unsupported values. |
| Join mode | The consumed `SupportCertificationJoins.Take()` value. Full-matrix and certification-only payloads never alias. |
| Evaluation mode | Reference evaluation and any non-default diagnostic evaluation/work-limit policy bypass lookup and publication entirely. No such result seeds a later optimized query. |

Use integer bit patterns, not float equality, rounded positions, shape radius alone, quaternion
normalization, approximate hashing or a neighborhood stencil. Hash collisions require full key
equality. Invalid or non-finite requests follow original validation and are never cached.
Analytic callbacks are deliberately outside this physics key because BB-1 runs them again.

### BB-4. Copy the complete owned payload and publish fresh metadata

Remember only Complete results, including Complete with zero members. Own immutable copies of
all ordered members, every witness field and occupied-slot count, both proposal verdicts and
every word of the complete requested join matrix. Preserve all signed zeros, errors, enclosure
bounds, raw IDs, witness roles, ordering and context-only rows. No reconstruction from selected
support, omitted empty result or transitive join inference is allowed.

At a hit authenticate the current lease and view before lookup and again before committing
output. Check caller capacities and prepare `SupportFootprintResult.Completed` with the current
receiver and current lease before touching either caller span. Copy only the result-owned
output range, leaving unused tails untouched, then return without further fallible validation.
`AssertFootprintCurrent` must accept the fresh result and
continue rejecting the original result under the later lease. Continuity capture uses the
fresh payload and current arena, preserving the existing direct endpoint-join contract.

### BB-5. Preserve validation, refusal and bounded lifetime

Keep `SupportCertificationJoins.Take()` first, including refused and throwing calls. Preserve
lease authentication, query entry, null probe checks, filter validation and caller join-span
validation in their original order before a hit can write. An insufficient member span retains
CapacityExceeded and RequiredElements behavior. A short join span still throws ArgumentException.
Both spans and unused tails retain the original untouched-output contract on failure.

Never cache exceptions, Unsupported, Ambiguous, CapacityExceeded or work-limit refusals. A
miss or ineligible session executes the original evaluator with its original work policy.
A stale basis cannot be repaired by accepting a newer result against an older bake capture.
The existing authentication path must refuse that build. A reference or diagnostic policy that
changes evaluation must bypass reuse rather than inherit a successful unrestricted entry.

Each worker retains at most 512 keys and 2 MiB of payload storage. Use no eviction and no shared
world-sized cache. Account for actual owned key/value and member/join buffer capacities with
their actual element sizes, including both inline witness slots and proposal verdicts. Document
the deterministic accounting and separately bound fixed table metadata by 512 entries. Do not
confuse a guessed per-member allowance with measured allocation. Capacity zero, saturation and
budget overflow decline publication or lookup and execute the full path without changing a verdict.
Reset releases prior-source payloads, failure unwinds the active domain and disposal releases
all retained storage. A worker's next phase or a new OS thread cannot inherit a prior session.

### BB-6. Preserve original raw source addresses through layering

Record capture column, original ordinal and raw height bits before CandidateColumns compacts
accepted samples or parallel admission merges them. Retain their association with the raw
NavSpace cells before `WithRestingHeights` replaces heights. Attach the derived layer as a
separate address component. Never recover identity from resting height alone or coalesce two
raw slots because their resting anchors coincide. An unresolvable source mapping disables
reuse for that group and keeps the existing proof, rather than choosing an arbitrary match.

Open/reset only around one accepted node's contiguous same-layer outgoing direction loop.
Keep admission and Stair proofs unchanged. No reuse spans profiles, source nodes, raw layers,
bands or worker phase transitions. Session storage may be provisioned per worker, but mutable
entries and the active domain belong exclusively to the thread running that source group.

### BB-7. Share only the identical successful initial Hold

After each edge's existing finite and endpoint-footprint prechecks, the first eligible outgoing
same-layer edge executes the real initial Hold. Store its complete numeric MoveState, frame,
outcome and published feet only after Advanced, grounded, finite and footprint checks and the
unchanged Near-to-source test pass. Keep that first Hold distinct from SettlePlacement.

The Hold key includes BB-6's full source/profile domain, all canonical initial MoveState bits,
frame identity/epoch/origin bits, every field of the effective proof tuning and contact
composition, EdgeProbeSeconds bits, MaxEdgeProbeSteps, query scope/witness identity, physics
and environment generations, selected view, registration/space/binding identity, analytic and
boundary provider identity, water identity/generation/extent and the authenticated build basis.
Immutable per-proof identities can bind fixed values once. Delegates alone do not certify their
contents. `CreateProofBinding` still sets WalkSpeed and RunSpeed to 1 and AirMomentum to false.
Scope/witness identity here means authenticated logical scope and stable build identity, not
the transient lease, pin or witness object. Authenticate each current witness independently.

Later eligible directions may copy that numeric state, outcome and frame. Store no Selection,
SupportSample, lease, owner binding, pin or arena continuity handle. Authenticate the edge's
current basis through normal Acquire before using the copy, even for an edge that immediately
arrives after Hold. Dispose that lease normally. Clear Selection before the next real movement
step exactly as Step currently does. Each reused Hold still consumes logical slice zero, so
travel starts at slice one and never gains an extra step under MaxEdgeProbeSteps.

Do not cache a rejected Hold or skip the per-edge checks. Travel direction, verdict, supported
feet, arrival checks and remaining steps are always computed for the current directed edge.
Admission Holds and Stair Holds are neither shared nor seeded into this memo.

### BB-8. Make stable-provider eligibility explicit

Add `NavBakeExecution.UseStableContactHoldReuse`, default false, for serial callers. True
asserts all analytic ground/normal, medium, water, registration, boundary and area providers are
deterministic functions of inputs, independent of call order, and fixed for capture and build.
It permits omitting redundant Hold callback executions. It does not assert a constant field.

Degree greater than one already requires stable, deterministic, thread-safe providers. That
existing contract permits Hold sharing there. Null execution and degree one without the new
assertion retain every historical Hold execution, including stateful callbacks. Physics-payload
reuse can still operate because callbacks continue running for those Finds. Legacy profiles
ignore the new property. Neither the property nor reuse policy changes bake identity or bytes.

Capture provider and frame identities before proofs. Authenticate at each reused edge and at
the original final publication check. A provider or water mutation after the last hit must still
refuse the entire build. Unsupported Hold eligibility uses the real Hold without disabling a
sound independent footprint session. Reference evaluation disables both reuse layers.

### BB-9. Preserve controller arithmetic and geometric protections

Keep GroundTraversalProbe.Near's exact 3D Euclidean 1 mm rule, including equality and adjacent
float cases. Separate X/Z comparisons are not equivalent. Keep absolute-coordinate recurrence,
direction normalization, speed fractions, substep prefixes, frame subtraction/addition and
centre-to-feet rounding in their current order. A short step budget can still refuse an edge
with a geometrically clear straight segment. No shortcut replaces those failures with acceptance.

Retain all raw stack/headroom, roof, circular footprint, forbidden-area, support ownership,
lower-surface suppression, lifted shell, water and medium checks. The eight rejected pool
edges and every asymmetric Stair and row-band pair keep their verdicts.

### BB-10. Separate actual work, reused work and movement work

Add counters for FootprintReuseHits, FootprintReuseMisses, FootprintReuseStores and
FootprintReuseBudgetSkips. Record retained keys/bytes and their high water, with zero live
retention after reset/disposal. Record EdgeSteps for real controller calls and InitialHoldReuseHits
separately. Existing Find and FindMemoHits retain their meanings. Volumes and Proposals count
actual geometry work, not work a cached payload once performed. Hits do not replay historical
curved-work events. Preserve the seven current curved categories and first-failure diagnostics.

Update CounterCount, snapshots, subtraction, worker exchange/merge/restoration and tests as one
contract. Additive events sum across workers. Retention high water uses maximum per active
worker session, never a sum masquerading as a per-worker bound. No safety or limit enforcement
depends on diagnostics being enabled. Holds legitimately remove their own Finds and callbacks
only under BB-8. Compare modes to distinguish this from per-Find geometry reuse.

### BB-11. Measure the projections honestly

Use immutable, deterministically constructed fixtures, once per named configuration. Establish
reuse-off reference, footprint-only and combined modes. Footprint-only preserves edge steps,
Finds and provider behavior. Combined mode removes only certified redundant Holds. Do not add
the reductions below or count existing tick-local hits again. These measured targets use the
full-profile admission-inclusive denominator, while EdgeSteps excludes admission Holds.
Reuse-off disables only this lane's two optimizations. The existing tick memo remains enabled.
The separate SupportReferenceEvaluation oracle is not the work-counter reference denominator.

| Fixture | Reference footprint evaluations | Target avoided with footprint-only | Target remaining | Target reduction | Reference edge steps | Target saved Holds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| low-lip-bank | 8,108 | 2,386 | 5,722 | 29.43% | 4,032 | 328 |
| deck-over-ground | 16,024 | 7,944 | 8,080 | 49.58% | 8,064 | 656 |
| wade-only-pool | 24,752 | 12,308 | 12,444 | 49.73% | 12,416 | 548 |
| rebased-origin | 8,108 | 3,940 | 4,168 | 48.59% | 4,032 | 328 |
| mixed-legacy-contact | 8,012 | 3,972 | 4,040 | 49.58% | 4,032 | 328 |
| stair-accepted-refused | 4,116 | 1,259 | 2,857 | 30.59% | 1,686 | 17 |
| multi-layer-row-bands | 10,379 | 5,286 | 5,093 | 50.93% | 6,076 | 80 |
| Seven pinned totals | 79,499 | 37,095 | 42,404 | 46.66% | 40,338 | 2,285 |
| wide-lip-576 | 69,532 | 29,234 | 40,298 | 42.04% | 34,992 | 2,888 |
| single-flat | 5 | 0 | 5 | 0% | 0 | 0 |

The observed maximum unique Find-input keys at a source was 330, below 512, but these are
projections from a wider key than the backend request. Measure actual complete-key hits and
payload accounting. Additional exact request equivalences may improve counts. A shortfall
must be reported with fixture, mode, actual counters, key/budget reason and remaining gap.
Never drop a key field, widen a limit, change a fixture or force a hit to obtain these numbers.
Do not claim target acceptance for a shortfall. Report correctness and savings independently.
Curved fixtures require exact outputs and non-increasing actual event work, with no projected
curved percentage, elapsed-time threshold or allocation claim.

### BB-12. Preserve every pinned byte and every scheduling contract

Compare actual KENB bytes at identical engine identity between reuse-off serial, footprint-only
serial, combined stable serial and combined parallel. The contract covers every supported
positive degree. The finite executable matrix covers 1, 2, 3, 8 and a degree greater than the
fixture's total layer-row count for each pinned fixture. Also compare each configuration to the
unchanged normalized full-file hashes below. Keep all existing semantic assertions.

| Fixture | Normalized SHA-256 |
| --- | --- |
| low-lip-bank | `494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922` |
| deck-over-ground | `77addcbc9c054f85e1a14f916bd95af75d02f105c54ad8e56a3a25716b0d44cc` |
| wade-only-pool | `a735ea5a41a1adb58666219b053197ed457b3c7edd830e18f29c7c5c8a1a1635` |
| rebased-origin | `e5c86385e4141a289d41d8efbb66766e5ac7c942f825521fff91dede8e9b4eee` |
| mixed-legacy-contact | `94fc16f1dee6cfa4c99db7ce490443271a0eb2fe3d9f0906312a836c5cd9a856` |
| stair-accepted-refused | `62a061314987a1b8243f9eb8da639cd0239ec200f8f62f11e69e62f5db20959a` |
| multi-layer-row-bands | `ae89cde4394002043b200b5e25e4eb0ef778ced4438cd7e985bde8dd96de811a` |

Preserve phase barriers, canonical source/direction/link ordering, band cutoffs, lowest serial
failing ordinal, partial worker setup cleanup and caller diagnostics restoration. Force different
worker claim orders with deterministic completion gates, not sleeps. No mutable memo crosses
workers and no hit depends on winning a shared-cache race. Admission and Stair phases remain
the existing proofs. No golden regeneration, KENB schema or bake-identity change is permitted.

### BB-13. Prove adversarial near-misses and current publication

Change one key component at a time. For numeric fields change one representable bit or adjacent
ulp while staying in domain where possible, otherwise require the historical invalid-input
behavior. Include every component of both poses/quaternions, probe radius/length and distance,
axis, disc radius, binary64 band bounds, slope, contact band, filter fields, join mode, signed
zero and origin. Change profile and raw-source tokens independently, including equal resting
heights in different raw slots/layers. Force hash collisions and prove full equality is checked.

Use distinct source worlds and same-source views with changed exclusions. Cover stale
generations, static removal/handle reuse, rebase, disposed/wrong-thread sessions, source reset,
nested requests, reference bypass and mutation after the final hit. Prove insufficient member
and join spans, Unsupported, Ambiguous, CapacityExceeded and lowered-test work-limit refusal
never cache or partially write. Test Complete-empty and joins crossing a 64-bit word boundary.

Compare full cached/uncached payload bits on boxes, hulls, meshes, tiny faces, compounds,
tilted cylinders, capsule crowns and lane B/C certified-sweep scenes. Include analytic and
physics wins, coincident raw members, direct-only joins and independent roofs. A cached result
authenticates only with its current receiver and lease. Old results and old arena handles stay
invalid. Reference mode neither reads nor seeds a session.

### BB-14. Finish with durable contracts and honest evidence

Record reds, focused greens, complete byte/hash comparisons, counter targets versus actuals,
bounded storage and provider/failure controls in a committed verification record. Update living
Physics, Bepu, Movement and consumer-use documentation for the capability and serial assertion.
Search all Markdown for changed names and stale callback/cache lifetime claims.

The final implementation task follows BR-R4 and BR-R6 and stages 21.6.1 after merging released 21.6.0
from origin/main. It adds a separate CHANGELOG entry dated 2026-10-19, updates Directory.Build.props
and guarded declarations, and leaves the released 21.5.2 entry untouched. Run the full Release solution build, full solution
test selection required by AGENTS.md in build-slot's heavy lane and format verification once
at that finish. Run the governance guards. The design author commits only the spec, plan and
index row. Neither this design nor an implementation worker authorizes push, main integration, release,
consumer adoption, #1334/SG-12 acceptance or closure of world-scale cost work.

## Acceptance groups

| Group | Required proof |
| --- | --- |
| A. Exact payload and key | BB-1 to BB-5 and BB-13. Current-lease Complete publication, every field and join word, no partial writes, one-bit misses, reference bypass and bounded owned storage. |
| B. Source and Hold | BB-6 to BB-8. Original ordinal retained, one real successful Hold, copied numeric state only, authenticated slice-zero reuse, stable serial opt-in and historical unstable serial path. |
| C. Protection and arithmetic | BB-9 and BB-13. Eight pool rejections, directional stairs, thin wall, lifted roof, circular forbidden corner, headroom/next-raw boundary, unsupported hole, lower deck layer, changing support within the disc, exact Euclidean equality and adjacent floats. |
| D. Bytes and scheduling | BB-12. All seven actual files and hashes, every specified degree, forced direction/source and band order, serial failure ordinal and cleanup. No changed golden. |
| E. Measured work | BB-10 and BB-11. Separate footprint-only and combined counters, no replayed curved work, real retention bounds and explicit target shortfall reporting. |
| F. Finish | BB-14. Living docs, staged 21.6.1 history under BR-R4 and BR-R6, full once-only Release verification and governance evidence. |

## Explicit non-goals

- No horizontal-stratum edge certificate, translated/reflected/rotated full-edge memo or booked savings for candidates 3 and 4.
- No reverse-verdict reuse, reachable-region pruning, new start-set policy or output omission.
- No admission-Hold sharing, Stair-Hold sharing, admission payload reuse or cross-source/world-sized cache.
- No movement/analytic result cache, cross-lease SupportRequestMemo, persisted cache or old continuity export.
- No new dependency on Bepu from Movement or Locomotion, tuning, controller, KENB schema or content change.
- No timing, load, allocation or Hollowmere speedup claim and no game edits, release, tag or automatic pin adoption.
