# Swim environment facade F2

Proposed immutable review revision F2. Supersedes F1's candidate contracts, not any released API.
No implementation authorization. Engine baseline ca13d62d7. Cave comparison revision 0bd1d69d8.
The migration lane's six F1 findings are addressed below. F1 remains preserved at commit 98f82b6be.

## 1. Scope and proposed gate amendment

Migration owns canonical occupied spaces, support owners, geometry, water containment, native
adapters, #1300 rendering and #1301 tiled bake/storage/residency/seam infrastructure. Generic
movement owns capsule clearance, movement policy, non-swimmer boundaries, surface jumps and
profile inputs. Native adapters consume canonical data once, never another sampler in the game.

Proposed amendment, requiring joint F2 acceptance followed by owner approval:

- G1a allows the generic query contracts, read-scope/complete-clearance prerequisites, generic
  P1/P2 consumers and deterministic synthetic fixtures. It does not require native adapter code.
- G1b retains approval of R2/R3/R4 native interfaces and native adapter/completeness proofs.
  Native P3, real seam/bake acceptance, game adoption and WH1 carving remain blocked by it.
- Neither gate is open now. G0 is already satisfied. Art retains its independent gates.
  No release is authorized. Generic proof does not certify native data or migration parity.

## 2. Stable identity, local handles and query lifetime (F1-2, F1-3)

`MovementDomainKey`, `MovementSpaceKey` and `MovementSupportKey` each contain cached immutable
`string WorldId, string LocalId`. Comparison is ordinal. These are opaque semantic identities,
not storage page IDs or hashes assumed collision-free. An adapter resolves them to native owners.
Snapshot-local handles are `uint` indices paired with a local lease ID and are never persisted,
replicated or placed in bake identity. No cross-process comparison of local generation numbers.

`MovementQueryIdentity` contains semantic closure ID, query-policy version and the exact scoped
dependency digest. `MovementQueryLeaseId` contains source-instance ID, geometry generation,
environment generation and frame epoch. The first is portable compatibility/provenance, the second
is a process-local lifetime guard. Required scope has a coverage witness naming the bounded space,
geometry and medium resources actually certified, including negative/empty regions proved by the
canonical directory. Missing data cannot certify emptiness. Full-world residency is not required.

### Enforced read interval

F1 incorrectly treated a restricted physics query view as immutable. Bepu's view forwards queries
and Origin to its live owner, while only the exclusion selection is snapshotted.

Add a GPU-free `MovementEnvironmentContext` that acquires a `MovementQueryLease` before any
query. The lease is synchronous, owner-thread bound, non-reentrant and disposable. Acquisition
pins environment resources and their coverage witness, takes the shared source read gate, binds
the exact physics owner/view and current generations, and verifies the frame descriptor. Release
occurs only after result validation and movement-state publication in that simulation step.

Every mutation of the participating physics owner goes through that same gate: static/dynamic
add/remove, shape/pose changes, simulation stepping, rebasing and disposal. While a lease is open,
same-thread mutation throws before touching state and another thread cannot enter a mutation.
Environment republish/eviction uses the same read interval or acquires the same ordered gate.
The adapter cannot hand out a raw mutable owner that bypasses this enforcement. Acquisition order
is physics-source gate then environment pin, and publication uses the same order to avoid deadlock.
Query methods validate lease ownership and declared scope. Before/after version checks remain
defense in depth, not the concurrency mechanism. Unsupported backends refuse explicit mode.

The generic prerequisite adds this lease support to the physics seam and Bepu owner, with tests
that attempt mutation/rebase/eviction during a lease. Tests are finite deterministic calls, not
thread-contention stress. Legacy callers without explicit movement retain their existing lifecycle.
Offline nav runs can use a private query world under the same contract instead of pinning a live
server for an entire bake. R8 later owns actual bounded resident-set acquisition/publication.

### Coordinate contract

All facade vectors and SurfaceY/interval heights are float metres in the leased physics frame,
Y up. The frame descriptor retains the source origin, current `WorldFrame` anchor and epoch.
The native adapter converts world-datum Y exactly once by subtracting the physics origin Y.
Results convert back through that same captured origin, never whatever origin is live afterward.
Native integer/address precision remains migration-owned. This does not rewrite MapDoc coordinates.

The explicit step takes `FramedMovementState`, containing MoveState plus its frame descriptor.
Rebind to the acquired frame occurs before querying, using the recorded old anchor/origin, and
validates the local envelope. If that is impossible, return `FrameMismatch` with the original
framed state tagged as uncommitted. Never stamp an old local position with a new frame token.
The host retains the old framed state or uses its explicit handoff/recovery path. No malformed
framed result can be published as an advanced move. Domain/space/support keys survive a rebase.

## 3. Membership, containment and support contracts (F1-2, F1-4)

The following exact names replace F1's sample/support candidates. Factory validation enforces
finite geometry and known-result invariants. Default availability is Unresolved.

| Type or member | Candidate fields/signature |
| --- | --- |
| `MovementAvailability` | `Unresolved = 0`, `Known`, `Stale`, `Invalid`, `CapacityExceeded` |
| `MovementBodyQuery` | `Vector3 Centre`, `float Radius`, `float HalfHeight`, `MovementSpaceKey CurrentSpace`, `MovementSupportKey? CurrentSupport` |
| `MovementWaterPoint` | Availability, selected Space, nullable Domain, `bool InWater`, `float SpeedScale`, selected interval below |
| `MovementWaterInterval` | `float LowerY`, `float UpperY`, `float NominalSurfaceY`, `bool UpperIsFreeSurface`, lower/upper boundary provenance keys |
| `MovementSupportCandidate` | `MovementSupportKey Owner`, `MovementSpaceKey Space`, `Vector3 Feet`, `Vector3 Normal`, nullable traversed portal/link key |
| `MovementSupportRequest` | Body query, finite `MaxRise`, `MaxDrop`, `MaxSlopeRadians`, explicit current support/space and allowed transition context |
| `MovementSupportSet` | Availability, count, required capacity, selected coverage/provenance identity. Known empty means complete query found no eligible candidates |
| `MovementQueryLease.SampleCentreWater` | `MovementWaterPoint SampleCentreWater(in MovementBodyQuery body)` |
| `MovementQueryLease.EnumerateSupport` | `MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates)` |

Centre-feet membership is a point query with the canonical boundary rule, not a footprint union.
Several different domains under different footprint portions are legitimate. Ambiguity means
two incompatible memberships for the same queried point/space with no canonical ownership.

The actual containing vertical interval is `[LowerY, UpperY)` except where canonical portal
ownership selects the adjoining space on an exact boundary. `UpperY` may be a cave ceiling below
`NominalSurfaceY`. Only `UpperIsFreeSurface` permits surface settlement/jump eligibility at that
interval's upper bound. A connected free surface elsewhere in the body is insufficient. Surface
height and membership are determined from canonical space/volume geometry, not global XZ depth.

Body submersion uses the applicable water interval and capsule feet. Column extent is UpperY minus
LowerY. Solid support is an independent candidate query with provenance. A bridge is dry because
its supported body occupies a dry interval, not a bridge tag. Portal/opening planes are membership
boundaries, never implicit solid floors.

Support enumeration returns canonical owners within the finite step/drop search envelope and
legal current-space/portal context. Generic `MovementSupportResolver` applies capsule clearance,
slope and rise/drop policy using the leased shared physics view. Prefer the current support owner
when equally valid. Cross-space support requires a declared traversed portal/link. If canonical
ownership cannot break an equal valid tie, return Invalid rather than choosing enumeration order.
Never hop to a higher unrelated stacked floor solely because its XZ ray intersects first.

Files and ownership: generic value types and `MovementSupportResolver` belong to Locomotion,
lease/gate and complete contact access belong to Physics and Physics.Bepu, and Movement consumes
them for profile probes. Migration's native adapter supplies canonical membership, candidate
owners, provenance, coverage and legal transitions from its selected R2/R3 geometry. It does not
implement a second capsule solver, and generic movement does not resample native terrain itself.

## 4. Complete swept coverage and sliding (F1-1)

`MovementMediumSweepQuery` is a body query plus `Vector3 Delta`, executed under one lease. Water
coverage examines the swept horizontal capsule footprint, a closed disc of Radius inflated by
the explicit 0.001 m conservative skin, along the piecewise movement segment. Centre membership
is queried separately. Solid clearance always uses the full upright capsule, not that disc.

Replace the single-water-per-span model with two caller buffers:

- `MovementCoverageSpan`: EnterFraction, ExitFraction, ContactStart, ContactCount, HasDryCoverage.
- `MovementDomainContact`: Domain, Space, containing interval, contact normal in the local frame,
  conservative contact fraction and canonical boundary provenance.
- `TraceWater(in MovementMediumSweepQuery, Span<MovementCoverageSpan>,
  Span<MovementDomainContact>) -> MovementCoverageResult`, which returns Availability, both written
  and required counts and a complete coverage identity.

Each span can name multiple domains and simultaneous dry coverage. Contacts are a complete
conservative set, not one chosen representative. An adapter must bound trace error at or below
the 0.001 m skin. If it cannot certify that bound, it returns Unresolved. Domain boundary normals
come from canonical boundary geometry. Depth-threshold contacts inside a domain combine the
selected surface and the already resolved support-path geometry in the generic solver.

Intervals are ordered by increasing entry fraction, half-open at exit except the final endpoint
which is included. Exact centre membership uses canonical half-open portal/domain ownership.
For footprint collision only, tangency to a forbidden volume is a contact: allow displacement
away from or tangent to its outward normal, block inward displacement. Zero displacement still
reports starting coverage and penetration. Start overlap is not converted into a fake t=0 safe
position. Return a typed recovery/placement refusal if the complete contact solver cannot resolve it.

Simultaneous contacts within the 0.001 m spatial tolerance are resolved as one constraint set,
sorted by stable domain/boundary keys for determinism. Project displacement into the intersection
of permitted half-spaces, not successively choosing a convenient single normal. At a corner with
no feasible component, stop. Trace again after each solid slide, step-up/down, support correction
or vertical correction. Every committed path segment has both complete solid and water proof.

Initial work limits: 64 coverage spans, 256 domain contacts and 8 movement correction iterations
per segment. Capacity/error/iteration exhaustion discards the entire tentative step, returns
Unresolved and commits no movement/effects. There is no usable prefix. These limits enter the
query/profile policy identity and are acceptance targets, not stress-test invitations.

## 5. Complete collision prerequisite (F1-5)

`SwimStep` bypasses physics today. `SwimClear` explicitly allows a deepest bed penetration to
hide a shallower wall contact. Neither is adequate for the approved player behavior.

G1a therefore includes `MovementCapsuleResolver`, shared by explicit-mode player/NPC movement and
nav edge probing, plus a complete capsule-contact enumeration seam on the actual Bepu backend.
Proposed seam `QueryCapsuleContacts(CapsuleShape, Pose, Span<CapsuleContact>) -> CapsuleContactResult`
returns every relevant contact within the declared leased scope, with normal, separation/penetration
and owner provenance. Capacity exhaustion is explicit. It must not implement enumeration by
repeatedly asking only for the deepest MTV and assuming the last answer proves completeness.

Use existing swept capsule queries to advance along each candidate path, enumerate all start/end
contacts to resolve overlaps, and apply the whole feasible contact constraint set. Generic nav
calls this same resolver. No independent nav collision clone, endpoint-only acceptance or
deepest-MTV-only standing proof. Preserve existing legacy solver behavior until explicitly selected.

P0 allocates backend contact-enumeration and read-gate implementation before P1/P2. Their red/green
fixtures include a deeper walkable bed contact plus shallower side wall, a low ceiling plus post,
shore entry/exit, initially overlapping capsule, tangency and two simultaneous corner constraints.
If the real backend cannot meet the completeness/error contract, G1a fails rather than proceeding
with a plausible analytic stub. Native G1b still proves real geometry and scoped coverage.

## 6. Surface jump, input, transport and holds (F1-6)

### Inspected input and state inventory

`WowMovementInput` uses `WasPressed(Space)`. `MoveCommand.Jump` is a per-command request, not held
key state. `MoveProtocol.EncodeMove` carries an int sequence and the jump byte. Both server heads
use `RemoteCommandQueue`, which deduplicates slot/sequence, rejects at/below processed high-water,
dequeues once, returns neutral on an empty queue and may discard stale commands at catch-up.
Both heads record the dequeue acknowledgment before stepping. Do not add another launch-sequence
field to MoveState: the existing session-scoped ordered stream already owns consumption.

The game continues sending edge presses. Generic callers are required to do the same. A malicious
client sending another true request with a new sequence while airborne gets no launch because
the mode is airborne. Returning to a surface permits a new edge request. The protocol does not
claim to prove a physical keyboard release, which the server cannot observe.

Unresolved, Stale, Invalid, CapacityExceeded and PlacementRefused consume and acknowledge that
tick's dequeued command exactly once. Clear any jump buffer for that consumed request. Retain the
last verified framed movement state and emit no movement/action event. Restoring query availability
cannot requeue the old press. Prediction may simulate a request that the server later refuses,
but reconciliation drops it through the same acknowledgment and restores the authoritative state.
FrameMismatch is different: no new-frame state publication, preserve the correctly tagged old
state and request rebind/recovery. Command consumption is still not rolled back.

### New state and disposition

Add one byte `WaterExcursionState` (`None=0`, `Surface=1`, `AirborneFromWater=2`) to MoveState and
MovementState. Existing Swimming remains the current wet-swimming flag, not the whole excursion.
No persistent domain handle is needed to integrate an arc: current domain is queried under the
current lease. Stable current support/space selection travels with the host's canonical movement
context and must be rebound through native stable keys at G1b, never local pointer persistence.

| Route | Required change/proof |
| --- | --- |
| `PlayerMoveSimulator.Step` | Explicit-mode context acquisition and framed conversion, outcomes before publish |
| `PlayerMovementSystem` | Carry excursion through ECS input and output, clear transient events on held step |
| `MovementState.From` and `PlayerMoveState.From` | Copy excursion both directions, not infer it from Swimming |
| `MovementComponents.Set/Read` | Both server heads reconstruct through the same state path |
| `MoveProtocol` registry | Append byte, discrete-sampled with existing movement flags, bump wire identity at implementation |
| `WorldClient.IngestServerState` | Reconcile basis carries excursion, replay consumes only unacknowledged commands |
| Remote interpolation | Sample excursion on the same delayed discrete timeline as movement flags and rendered position |
| Cell handoff | Serialized movement component carries excursion, position rebinds frame, native owner/space keys rebind through adapter |
| Engine persistence | Extend `BuiltinBlobLayout` and generation migration with appended byte. Old blobs default None then placement validates/re-derives water mode before input |
| Consumer position/checkpoint restore | Engine movement fields and native placement validation own physical state. Grimhollow's journal/health checkpoint codec is not a motion snapshot and gains no guessed swim field |
| Teleport/respawn | Clear water-origin arc and classify the validated destination. Explicit invalid destination refuses without partial state |

A launch is Surface -> AirborneFromWater with independent SurfaceJumpSpeed and normal gravity.
The upward launch cannot re-enter buoyancy. A ceiling hit clips upward velocity and keeps the
water excursion until descending into known swimmable water or landing on known supported footing.
On descending entry into another legitimate surface domain, query/classify that domain and become
Surface. Landing on supported wading or dry footing becomes None. Leaving one domain without
entering another continues the collision-resolved airborne arc, not a teleport to a surface.

### Surface tolerance and fully flooded intervals

Use a 0.03 m inclusive centre band around `UpperY - restSubmersion * bodyHeight + halfHeight`,
only when UpperIsFreeSurface is true and full capsule clearance is known. Bed depth is irrelevant.
Reject a below-band request and clear it. This version has no ascent command.

The inspected wire stores position and vertical velocity as float32, not centimetre quantization.
WorldFrame limits planar locals to 512 m and retains Y=0 datum. The cave proposal's vertical probe
envelope is +/-640 m, where one float ULP is 0.00006103515625 m. The proposed 0.03 m band exceeds
that representation spacing and the 0.001 m query skin. These facts justify the candidate value,
but both heads still require exact boundary, one-ULP-below/above, 30 Hz and correction tests.
This is not a waiver of migration's stricter geometry/import tolerances. Beyond certified frame
precision, refuse/rebind the query rather than inflating the band silently.

A fully flooded low-ceiling interval with UpperIsFreeSurface=false has no legal surface-swim
node or surface-jump target. Entry by a surface-only route is refused. An unexpectedly restored
body there receives PlacementRefused/recovery-required, not buoyancy through the ceiling or an
invented diving mode. An accidentally submerged body may settle only in its actual interval when
that interval has a free top and the upward path is clear. A shaft/portal can make such an interval
available, but connectivity elsewhere cannot override a solid local ceiling.

## 7. Profile identity, reachability and acceptance fixtures

Portable profile identity includes capability, capsule dimensions, slope/step/drop limits, water
thresholds, resting fraction, surface tolerance, clearance/trace/contact policy versions and limits,
actual route-cost inputs, semantic dependency identity and canonical shore-transition policy.
Local lease IDs are excluded. A tile seam is not a shoreline. No new route-cost knobs are added.
Keep the consumer aggregate 8 MiB gzip -9 budget until an owner changes it.

Only an exhausted search over a certified complete required scope can return ProvenUnreachable.
Unknown/stale/capacity/invalid scopes return their explicit outcomes. Partial is not automatically
ProvenUnreachable. The engine returns facts, the game decides evade/combat behavior.

Fixtures, all finite and deterministic:

1. Dry cave below ocean, two stacked water intervals, supported bridge above and swimming below.
2. Partly flooded chamber, completely flooded low-ceiling chamber, adjacent dry chamber, and shaft
   through an explicit opening. Surface absence cannot create a ceiling-crossing jump or buoyancy path.
3. Dry centre with footprint touching a narrow deep channel, two legitimate adjacent domains,
   wet/dry mixed footprint, exact start tangency, zero displacement, shoreline corner and tile seam.
4. Capacity/error/iteration exhaustion discards the entire step. Trace each post-slide/step correction.
5. Current support continuity under stacked same-XZ floors, equal owners, lawful portal crossing,
   and missing geometry that is never NoSupport.
6. Mutation/rebase/eviction during a query lease refused before mutation, stale lease and state frame
   rejected, two equivalent origins give equivalent local movement, portable IDs exclude local tokens.
7. Real Bepu bed-plus-wall, low ceiling/post, initial penetration and compound corner contacts.
8. Surface jump over 2 m and 100 m beds gives the same configured apex. Submerged/held input and
   refused command recovery never launch later. New airborne request, duplicate sequence and catch-up
   discard do not duplicate a jump. Ceiling, downward domain crossing and supported landing keep mode.
9. Authoritative correction/replay, delayed remote sample, cold restore, old-blob migration and cell
   handoff preserve mode or explicitly validate recovery. No stale local support handle rides the wire.
10. Deliberately legacy callers retain existing dry, wade, swim and shore-hop behavior.

## 8. Concrete P0 refinement and review exit

G1a cannot be accepted as only the earlier two policy knobs. It now includes these ordered tasks:

1. Pin F2 field validation and value-type locations, implement lease/mutation gate and complete contact
   enumeration in Physics/Bepu with the real-backend fixtures. No MapDoc production adapter.
2. Implement generic membership/coverage/support values and resolver, with bounded caller buffers,
   exact outcome handling, framed state and the shared capsule solver in Locomotion.
3. Implement P1 boundary policy on that solver, then P2 surface jump and the transport inventory above.
4. Make Movement's profile probe consume that solver. Synthetic seam fixtures are generic-only evidence.
5. Preserve G1b for native adapter completeness, actual portal/space ownership, scoped residency,
   real tiled seam/bake identity and game integration. Joint acceptance records both revisions.

Before owner amendment approval, migration reviews all six F1 dispositions against F2, especially
lease enforceability, support/context ownership, trace contact completeness and flooded-interval
semantics. Any required change produces F3, not an in-place alteration of an accepted immutable F2.
Then write the approved concrete task interfaces/tests into P0/P1/P2 before any generic implementation.
