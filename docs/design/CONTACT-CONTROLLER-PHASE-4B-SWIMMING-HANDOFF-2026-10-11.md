# Contact controller phase 4b: swimming handoff and deep-water walkers

Date: 2026-10-11. Implementation specification for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438), phase 4b, and the runtime
part of [#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Status: implemented on `feature/swimming-contract`, staged for 20.33.0.
Written under the owner's 2026-10-10 instruction to continue the program to completion, with every decision
recorded as a ruling for review. Source baseline: `feature/swimming-contract` at `3feec2aed`, which carries main
through phase 4a.

The [program](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md),
[phase 3](CONTACT-CONTROLLER-PHASE-3-AIR-AND-STATE-2026-10-10.md),
[phase 4a](CONTACT-CONTROLLER-PHASE-4A-GROUND-SIGNALS-2026-10-11.md) and
[swimming owner amendment](SWIM-STEP-SUPPORT-DEPENDENCY-2026-10-09.md) remain binding. This specification follows the
owner's 2026-10-09 ground ownership decision and 2026-10-10 instruction to integrate the swimming branch.
Every choice below is a numbered ruling with its cost if wrong.

## Scope and current evidence

**Ruling 4b-1.** One contact controller owns the land tick on every explicit path. Grounded, wading and
land-origin airborne ticks use its existing ground, slide and air algorithms. Swimming owns water classification,
policy, buoyancy, surface movement and water-origin flight. It also owns the medium part of placement proofs,
authenticated read admission, transport and certified sweep completeness. The explicit adapter composes those
owners. It does not contain another ground or step algorithm. Cost if wrong: restructuring the internal tick
composition, rather than preserving two diverging movement models.

Success means the two StepFootprint tests pass unchanged, explicit land ticks exercise the controller's complete
tick, every selected path has shell, support and medium evidence, and WadeOnly stops and slides at its deep line.
The branch already includes phases 2 to 4a. The merge conflict described in the earlier research digest is
historical and is not an implementation prerequisite at this baseline.

At the source baseline, the explicit core called `Settle`, its own gravity/jump path and `TryStepUp`
(`KhaozEngine.Locomotion/ExplicitCharacterMovement.cs:71`, `:107`, `:129`). Its three land-selection sites are
`ExplicitCharacterMovement.Queries.cs:43`, `ExplicitCharacterMovement.Steps.cs:23` and
`ExplicitCharacterMovement.Continuation.cs:31`, all under `KhaozEngine.Locomotion/`.
The old resolver's slope-dependent centre offset includes a 1 mm skin
(`KhaozEngine.Locomotion/MovementSupportResolver.cs:79`). A mere candidate substitution leaves that body model.

At that baseline, the dry controller acquired its own physics lease and rejected water
(`KhaozEngine.Locomotion/Contacts/ContactController.cs:54`, `:102`). Its mode loop, remaining climb budget and
signal accumulation already exist (`:260`, `:280`, `:331`). Those algorithms are the implementation to share.

Out of scope are the public MoveTuning selector, navigation capture and bake identity, any further wire generation,
native G1b adapters, World Authoring R4, engine release, game adoption and world edits. #1299 remains open until
phase 5 delivers its navigation acceptance. Release, game pin and adoption keep their own approvals.

## Internal entry and shared tick

**Ruling 4b-2.** Add this internal entry. The public explicit entry points resolve the command and call it once.
It borrows the caller's composed lease. It neither acquires nor disposes a physics or environment lease.
Cost if wrong: an internal signature change. No new public movement entry or tuning selector is required.

```csharp
namespace KhaozEngine.Locomotion.Contacts;

internal static partial class ContactController
{
    internal static MovementStepResult StepLeased(in FramedMovementState state,
        Vector2 moveDir, float speedFraction, bool run, bool jump, float dt,
        in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries,
        MovementBoundary? boundary = null, float? faceYaw = null,
        GroundCoreSettings? settings = null);
}
```

This is a proposed signature. `ContactController` becomes partial only to give leased composition its own file.
`settings == null` still means `new GroundCoreSettings()`, never a zero-valued default struct
(`KhaozEngine.Locomotion/Contacts/GroundCore.cs:9`). The explicit public signatures stay unchanged.
The entry accepts all modes because a tick can cross their boundary. Swimming's mode handlers live beside
`ExplicitCharacterMovement.Fluid.cs`, with its medium ownership retained.

Factor the controller's current prelude, mode runner and finalization into shared internal tick machinery.
`ContactController.Step` remains its dry front door, using its existing lease acquisition, zero placement offset
and ordinary stateless delegates. `StepLeased` supplies a leased query context and swimming's medium handlers.
Both call the same movement and state algorithms. No default dry behavior or arithmetic order changes.

**Ruling 4b-3.** Add an internal `ContactQueryContext` with distinct support, shell and accepted-path operations.
Its dry implementation forwards today's operations. Its leased implementation guards the captured view, physics
lease and environment pin, then invokes those same primitives. Do not hand unrestricted `_view`, `_physics` or
`_pin` to the explicit wrapper. Cost if wrong: a few additional internal calls on the hot path. Their cost joins
the existing support cost gate, rather than justifying an unguarded query bypass.

The operations are:

| Operation | Dry implementation | Leased implementation |
| --- | --- | --- |
| Read analytic height/normal | Existing delegates | Producer-certified analytic field under the pin, or known absence |
| Find support | `FootSupport.Find` | Scope guard, same Find, then producer binding of its certified source |
| Query shell sweep/contacts/recovery | Existing shell backend calls | Scope guard and complete certified shell queries under the composed lease |
| Propose and select a motion route | Existing passes and retry order | Same passes and retry order, with medium/boundary constraints and proof |
| Accept a segment/placement | Existing local commit | Commit only after the selected route earns all required evidence |
| Classify medium | None | Existing full-body sampling and swimming policy |

The context is tick-local and non-owning. It holds no process-global movement history. The methods preserve a
failure availability separately from `SupportStatus.None` or a proved collision. A failed query must not turn
into a dry world, a wall, a new support grant or a claim that a route is unreachable.
Known absence of analytic ground supplies no terrain floor. It does not install a height-zero delegate into
the controller's recovery or backstop. Static footprint support remains available through the same context.

## The explicit tick

**Ruling 4b-4.** Prepare and finish one whole tick, whichever modes it visits. Do not wrap several calls to
`ContactController.Step` or several public explicit ticks around a crossing. Cost if wrong: mode handlers need
to return a continuation record, but timers, commands and signals cannot advance twice.

1. Validate framed state, carried values, dt, water policy, shell tuning and footprint settings. Assert the composed
   lease current. Validate a carried selection or rebuild it under the existing null-hint witness. Keep the
   distinction between a bad frame, invalid facts, unavailable facts and a forbidden initial placement.
2. Prove the initial full-body water placement and the shell placement. A legal below-knee overlap is not a
   full-capsule solid refusal. Recovery belongs to the contact controller and must earn the proofs below.
3. Classify water at the published body. Query certified support without first moving through the old Settle path.
   Swimming's `FootingAllowed` decides whether that support may grant land footing in this medium. Support
   geometry and the resulting ground/slide/air choice belong to the contact controller.
4. Prepare commitment once with the existing swimming water-abort rule and shared commitment helper. Resolve
   traction once from the admitted tick-start footing, as the controller does. Resolve land intent through the
   controller. Apply swimming's `MediumSpeed` to the land drive when wading, without overwriting the carried
   entity `SpeedScale`. Resolve swimming drive through its existing swim-speed rule.
5. Initialize one tick continuation. Run the mode selected below. After each selected, accepted timed substep,
   classify the resulting published-body pose and eligible footprint support. On an ownership change, stop that
   mode and pass its exact remainder to the next. Speculative routes never change mode.
6. Finalize phase 4a ground signals once for the whole tick. Capture landing impact and pre-jump grant. Decide a
   land jump last, through the controller's existing jump/coyote/buffer rules. A surface jump is swimming's own
   immediate launch and cannot also consume a land jump.
7. Resolve facing once using the original resolved command, full dt and `ResolveFacing`. Freeze it for a
   commitment-controlled tick. Finish commitment once, classify final water, bind final selection, check finite
   output and frame rebinding, assert the lease current, then return. The caller publishes before disposal.

The existing explicit command resolution is `KhaozEngine.Locomotion/ExplicitCharacterMovement.cs:15` and `:25`.
The shared controller finalization is `KhaozEngine.Locomotion/Contacts/ContactController.cs:125`, `:135`, `:166`
and `:174`. The swimming commitment exception is `KhaozEngine.Locomotion/ExplicitCharacterMovement.Commitments.cs:12`.
The wade multiplier and medium history are `KhaozEngine.Locomotion/ExplicitCharacterMovement.Fluid.cs:7`, `:24`
and `:45`. Reuse them, rather than deriving water from an analytic terrain height.

### Mode selection and handoffs

**Ruling 4b-5.** Water-origin history owns flight until a supported land/wading landing or recapture into water.
A dry position alone does not turn it into land-origin flight. Supported shallows count as land movement.
Cost if wrong: the entry/exit boundary must be revised, but existing water-jump speed and no-recapture guarantees
remain intelligible and testable.

| Accepted classification | Owner for the next time segment |
| --- | --- |
| Dry or supported shallows, Grounded, no water-origin excursion | Controller Ground, with wade drive where applicable |
| Dry or shallows without footing, no water-origin excursion | Controller Air or Slide by its existing rules |
| SurfaceSwimmer with Swimming or Surface | Swimming surface/buoyancy handler |
| AirborneFromWater, rising or outside a wet interval | Swimming water-origin flight handler |
| Water-origin descent with eligible walkable support | Shared contact landing placement, then controller Ground |
| Land-origin descent/step entering swim-required free water | Swimming handler, clearing land footing and owner |
| WadeOnly at forbidden depth, or DryOnly at water | Stay in the controller mode, obstruct and slide through medium constraints |

Land-origin Air and Slide keep all phase 3 transitions. A land tick that enters supported wading stays in the
controller. A surface or water-origin tick that reaches a bank obtains its landing through the contact-owned
footprint and placement primitive, not `TryStepUp` or `MovementSupportResolver`.
An ascending water-origin arc never gains buoyancy recapture merely by intersecting water
(`KhaozEngine.Locomotion/SurfaceWaterMotion.cs:27`, `ExplicitCharacterMovement.Fluid.cs:36`).
At a tie, eligible proved land footing wins, matching the current classifier's footing-first branch
(`KhaozEngine.Locomotion/ExplicitCharacterMovement.Fluid.cs:27`). Deep or closed water cannot use that tie to
grant footing, because `FootingAllowed` denies it first.

**Ruling 4b-6.** Handoffs occur after accepted timed substeps, using the same spent-substep convention as the dry
controller. The changing substep has spent its time. Only the unspent substeps run in the receiving mode.
Cost if wrong: a medium transition can be quantized by one bounded substep. Exact event-time integration would be
a separate numerical contract, not a hidden change to the existing gravity-first integrators.

Carry a `ContactTickContinuation` containing kernel feet, published-body mapping, vertical and horizontal carry,
remaining seconds, remaining climb metres, traction gate, current mode/support/binding, first commanded drive,
last segment's carry/drive/achieved/time, landing speed and phase 4a ground accumulators. This is an internal
return value, never wire state. Start with `remaining = dt` and
`budget = MaxStepClimbSpeed > 0 ? MaxStepClimbSpeed * dt : infinity`, in double as today.

GroundCore, SlideCore and AirPass return that remainder without making another whole-tick budget. Their current
handoffs are `KhaozEngine.Locomotion/Contacts/GroundCore.cs:155`, `ContactController.cs:264`, `:304`, `:326` and
`:341`. Add an accepted-substep medium handoff result to those shared paths. A substep rejected by a retry has
spent no time, budget, signal or command. A proved boundary block spends the attempted substep and permits its
proved tangent, as a wall does. Unknown evidence aborts the entire tentative tick.

The receiving mode runs exactly the remaining seconds. Swimming recomputes its drive from the same resolved
command and current medium, with SwimSpeed through the surface and water-origin arc. A supported landing carries
the contact-clipped velocity into the ground remainder, as the existing air-to-ground transition does. The next
whole land tick resolves normal land drive. Wading retains the swimming-owned speed multiplier.

For a mode interrupted before its proposed duration, commit the dynamics evaluated for the consumed duration.
Do not hand on a velocity integrated over time the receiving mode will also spend. Retain AirPass's existing
gravity give-back discipline (`KhaozEngine.Locomotion/Contacts/AirPass.cs:172`). The swimming handler calls
`SurfaceWaterMotion.TryPropose` for its scheduled piece, and a water-origin piece integrates gravity there once.
Its shared shell flight and landing helpers must not call AirPass's gravity integration again. A surface launch
is attempted once per command. Submerged or unresolved presses remain consumed without a delayed buffer.
This duration rule governs the new medium handoffs. Existing dry transitions retain their established arithmetic
and parity. A medium-interrupted AirPass returns its consumed-time carry without reintegrating the accepted
position prefix. Its continuation must retain enough entry-speed and terminal-cap information to avoid giving
back gravity that the cap had already discarded.

Retain a total cap of eight ownership/mode segments per tick, shared with the dry controller's guard. A medium
cycle must spend a timed substep. If the guard is exhausted with time left, return EnvironmentUnresolved and
hold the input, rather than publish a prefix. Do not restart the cap on a fluid handoff.

### Signals, facing and commitments

**Ruling 4b-7.** Phase 4a stays the sole ground-signal implementation. Aggregate accepted walking ground segments
only and resolve once with whole-tick dt. Cost if wrong: mixed ground/water ticks can lose a ground presentation
event when they end in water. They cannot emit a false stair run from buoyancy or a landing.

StepRise, MoveRise, GroundTravel, rise/drop evidence and carried lag use the existing controller accumulator
(`KhaozEngine.Locomotion/Contacts/ContactController.cs:285`). Landing, flight, slide, recovery, medium correction
and the placement offset contribute no ground signal. A final swimming, water-origin airborne, sliding,
wedged, refused or corrected tick zeros all three climb fields and resets EWMA. A final walking remainder may
emit its actual ground contribution. A land jump consumed at finalization keeps the pre-jump ground signals.

GroundSignals look-ahead runs through the leased support context, including scope and owner binding. Its three
ahead samples reach `StepHeight + 2 * k * CapsuleRadius`, not merely StepHeight
(`KhaozEngine.Locomotion/Contacts/GroundSignals.cs:47`). Preserve its wire-quantum continuation rules and exactly
once StepDeltaY prediction behavior. Never reconstruct a step from final net Y.

`SupportGranted` is the contact controller's final pre-land-jump footing, not an OR of past modes. Swimming alone
grants none. A buffered land relaunch keeps the landing impact and grant. Commitments use the existing
preparation, latched heading and finish helper. Water entry aborts with EnteredWater, through the swimming rule.
No mode runs another facing update, decrements timers again or reinterprets a new press after admission.

## Support and canonical owner binding

**Ruling 4b-8.** Add an optional producer-owned capability on the acquired environment pin,
`IMovementContactSupportPin`. The capability supplies scoped analytic ground facts and binds installed static
features to canonical owners. A missing capability on a required explicit query returns Unresolved. It never
falls back to an owner invented from a static number. Cost if wrong: generic producers need a new capability.
Keeping it optional avoids silently changing the meaning of existing pin implementations.

`IMovementEnvironmentPin` currently exposes enumeration and reconstruction, but no finite-feature owner binding
(`KhaozEngine.Locomotion/IMovementEnvironmentProvider.cs:17`). `SupportSample` carries Static, FeatureId and
Witness, not a canonical space or owner (`KhaozEngine.Locomotion/Contacts/SupportSample.cs:15`).
The proposed capability has two operations:

```csharp
MovementAvailability SampleAnalyticGround(in MovementSupportRequest request,
    out MovementAnalyticGround? ground);
MovementAvailability BindContactSupport(in MovementContactSupportRequest request,
    out MovementContactSupportBinding binding);
```

These are proposed public producer-seam types, grouped in `MovementContactSupportFacts.cs`.
Known with null analytic ground means the producer certifies no analytic field at this axis. A present analytic
fact contains axis height, normal, canonical owner/space and transition evidence. It cannot be an unrestricted
global terrain callback spanning unrelated rooms. Repeated height/normal reads at one axis use the same fact.

A binding request contains the full-body query, canonical transition origin, exact kernel axis, certified
support height/error/normal/witness, nullable installed StaticHandle and FeatureId. The wrapper constructs it
only from the shared support query. A binding result contains canonical Owner and Space, optional TraversedLinkId,
and the query identity. The pin validates the installed source and feature against its own captured registration
and generation. Local handles are lookup keys under that pin, never canonical keys or transport evidence.

The query wrapper validates result world, identity and legal-transition evidence, and preserves the certified
geometric sample unchanged. A cross-space result needs producer-certified legal link evidence from the actual
accepted segment's origin. A missing, excluded, stale or ambiguous registration returns an explicit failure.
Mapping is not a second height-selection algorithm. The foot primitive still selects the support.
For joined polygons, bind the sample's certified provenance, including its installed member/feature, rather than
guessing the owner from the normal of the plane that capped the contribution
(`KhaozEngine.Locomotion/Contacts/SupportCertification.cs:11`).

Support feet always use the body's XZ. An off-axis touched point remains Witness only. A pushed placement obtains
new support and binding at its reported axis before acceptance. Final Grounded selection holds that owner's key.
Air, slide without footing and swim carry no support key. A wedge follows the controller's existing footing
contract and uses its proved contact binding, not a fabricated walkable surface.

**Ruling 4b-9.** Cold placement, exact correction and live continuation share these support/placement operations,
but never fake a zero-dt tick. Cost if wrong: a dedicated placement helper is required, but movement timers and
accepted buffers survive migration correctly.

ValidatePlacement reconstructs membership and proves the exact shell/full-body-medium pose without moving it.
SettlePlacement may reach nearby eligible support through contact-owned placement and a proved approach.
ReclassifyContinuation does not move or advance time, and preserves an accepted land buffer when the destination
is legal land. Water reclassification still clears an ineligible land buffer. These existing boundaries are
`KhaozEngine.Locomotion/ExplicitCharacterMovement.Placement.cs:8`, `:46` and
`ExplicitCharacterMovement.Continuation.cs:8`.

Remove explicit production calls to the old Settle/TryStepUp land solver. Retain the public
MovementSupportResolver.Select compatibility door and its canonical candidate guards, including exact XZ equality.
After those guards, route its land placement to the shared footprint and shell placement operations. Do not retain
its tangent-plane placement as another explicit ground model. The off-column test must still fail before any
attempt to replace the producer's malformed candidate with a physics witness.

## One interval, scope and path proofs

**Ruling 4b-10.** Every handoff query runs inside the one acquired MovementQueryLease. Scope checks happen before
the backend or producer call, and currentness is checked around it. Cost if wrong: an undersized caller scope
refuses a tick. It cannot silently widen itself or query uncertified space.

Reuse the exact `_view`, authentic `_physics` and `_pin` captured at acquisition. Preserve source reference,
selected-view reference, geometry/environment generations, frame, epoch and origin checks
(`KhaozEngine.Locomotion/MovementQueryLease.cs:37`, `:72`). The selected view's exclusions remain effective.
Acquisition stays prepare, physics read lease, prepared pin. Disposal stays pin then physics
(`KhaozEngine.Locomotion/MovementEnvironmentContext.cs:47`, `MovementQueryLease.cs:85`).

The leased context checks selection readiness and world membership before analytic reads, complete footprint
certification, owner lookup, shell recovery, lifting, sweeps, seats and signal look-ahead.
For a support request, retain MaxRise/MaxDrop limits, the full-body support envelope and transition-origin
inclusion (`KhaozEngine.Locomotion/MovementQueryLease.Buffers.cs:16`). Staged support gates also enclose the
complete expanded foot column and represented axis/foot proposal paths with `SupportFootprintBounds.TryCreate`.
A legal body envelope alone does not authorize the expanded query. An undersized scope refuses before
backend or producer work. The same physics lease reaches `FootSupport.Find` and footprint authentication.

For shell sweeps and contacts, guard the actual shell shape, represented affine path and skin/error enclosure in
double, mirroring `MovementQueryLease.Sweeps.cs:56` and `MovementQueryLease.Solids.cs:24`. For medium queries, guard
the full-body skin enclosure with the existing TraceWater path checks (`MovementQueryLease.Buffers.cs:59`).
The explicit offset below is included before those envelope comparisons. No float-rounded bound widens the scope.

Caller preparation must cover the intended displacement, up/down reach, all admitted pushes and signal look-ahead.
The maximum signal reach is `StepHeight + 6 * CapsuleRadius`. Slide wedge probes may reach a full body height
(`KhaozEngine.Locomotion/Contacts/SlideCore.cs:216`). Air landing can reach the full substep drop. A caller must
request these capabilities and their spatial envelope before acquisition. It may refuse an excessive requested
tick. It may not repin halfway through it. Do not disable look-ahead merely because today's Scene asks for
MaxRise 1 (`KhaozEngine.Game.Tests/Locomotion/ExplicitCharacterMovementTests.cs:224`).

**Ruling 4b-11.** Add bounded, attempt-local motion-route evidence to the shared passes. Every selected segment
and clearance push earns medium and boundary proof before local movement acceptance. An endpoint test or a
sweep of the final chord is insufficient. Cost if wrong: more route evidence and trace calls on a stepped or
sliding move. The cost is required to make the leased admission claim true.

GroundCore currently selects between lifted and standing attempts and commits placement/budget only afterward
(`KhaozEngine.Locomotion/Contacts/GroundCore.cs:318`, `:355`, `:419`). GroundPlacement can push an accepted axis
(`KhaozEngine.Locomotion/Contacts/GroundPlacement.cs:35`, `:80`). ShellMotion can produce several wall tangents
(`KhaozEngine.Locomotion/Contacts/ShellMotion.cs:83`, `:153`). Preserve those actual intermediate paths.

An attempt records an ordered placement route. From kernel feet f0, its chosen lift l gives the route
f0 -> f0 + l * UnitY -> every achieved side-sweep vertex at that lifted height -> the paced seat target -> each
accepted clearance-push vertex. A standing attempt has l = 0. These are mapped full-body configurations for
placement proof, not an extra jump or extra net climb payment. Every connecting segment is proved. The selected
route retains its real wall tangents, rather than replacing them with their endpoint chord. The seat target is
the budget-paid feet, not the unpaid support height. The temporary clearance lift consumes no separate time,
does not classify a new movement mode and does not export a step. Its intermediate body route must nevertheless
be admissible. This can reject a lifted attempt whose endpoints are allowed, which is why the standing retry
must earn a separate proof.

Recovery records each MTV path. Lag payment records its vertical path. Air records each accepted flight segment
and landing seat. The bound backstop records its correction and placement. Query-only touch offsets and an
unselected lift are not movement and never consume time or classify medium. They cannot certify a different
physical body route. Failed route evidence cannot survive selection as an endpoint-only approval.

Each physical segment uses the shared rounding enclosure, full-body TraceWater, water-policy constraints and
`MovementBoundary.ProveSegment`. Preserve all coverage spans, overlap certificates and error brackets.
The current combined resolver's full-route retrace and rounding proof are the source to factor, not behavior
to remove (`KhaozEngine.Locomotion/MovementCapsuleResolver.cs:167`).

A forbidden-medium contact contributes its complete active normals alongside shell constraints. Reuse swimming's
`MovementWaterBoundary` and `MovementConstraintProjection`. Retrace every resulting prefix and tangent against
shell, full-body medium and boundary. Requery support and owner at the achieved axis through GroundSeat and
GroundPlacement before committing. This is a constraint on the shared algorithm, not another step search.
Lifted and standing retries keep separate tentative routes, budgets and signal contributions. Only the selected
proved route commits. A denied clearance push invalidates that attempt. A denied recovery cannot publish a
forbidden pose. The dry fallback and retry order stay the shared controller's choices.

Keep the existing finite proof limits for coverage, domain contacts and corrections. Route storage is bounded by
the existing pass/substep limits and may be streamed at acceptance, with local retry checkpoints. Do not use the
old nine-endpoint whole-resolver buffer as a truncation limit for a multi-substep ground tick. Exhaustion,
uncertified sweep, stale binding or failed retrace discards the whole tentative explicit tick, including an
earlier accepted ground or swim prefix. Proven obstruction returns Blocked. Initial forbidden medium returns
PlacementRefused. Invalid evidence returns EnvironmentInvalid. Unavailable, capacity or stale evidence returns
EnvironmentUnresolved. Hold semantics keep the pose and consume the command, with transient effects cleared.

## Body split and the explicit placement skin

**Ruling 4b-12.** All explicit solid proofs use ContactShell, including initial/cold placement, ground, air,
swimming, water-origin flight, recovery and continuation. All medium sampling/tracing uses the full original
body. Cost if wrong: below-knee solid overlap is newly legal, as the owner ruled. Retaining an obsolete full-body
solid precheck would prevent the bank acceptance and disagree with the shared body model.

ContactShell delegates to the controller's shell geometry (`KhaozEngine.Locomotion/Contacts/ContactShell.cs:13`).
The shell spans actual feet + StepHeight through actual feet + 2 * HalfHeight
(`KhaozEngine.Locomotion/Contacts/ShellGeometry.cs:7`). FootSupport retains the default disc radius fraction 0.5.
Validate the shell against the banded traction slope and the landing footprint reach. Water body radius and
half-height do not shrink to the shell. A wet leg or outer capsule overlap remains a medium fact even when
the shell is dry. Refactor the combined resolver to take distinct solid and medium geometry, retaining its
complete contact/sweep contracts. Ground ticks do not call that resolver as a replacement ground solver.

**Ruling 4b-13.** The explicit adapter has one fixed vertical placement offset `s = 0.001 m`, for every explicit
mode. It is a profile convention, not new support height or repeated step energy. Cost if wrong: a 1 mm offset
affects ceiling clearance and the water-depth boundary. Its identity and phase 5 capture must include it.

Let C be the published full-body centre, H the original half-height, f the contact solver's kernel feet and h the
certified geometric support height:

```text
f = C - UnitY * (H + s)
actual body feet = f + UnitY * s
published centre = f + UnitY * (H + s)
unpaced rest on level support: f.Y = h, actual feet.Y = h + s
```

FootSupport queries and returned heights use the original world and f's reference height. They are not translated
or inflated. The adapter maps shell configurations to actual feet `f + s` before shell queries and full-body
medium queries to `f + H + s`. It is not a translated physics world, and it does not modify an installed pose.
The same mapping applies in flight, on a slide and at a mode boundary, so no transition adds or removes s.
Frame conversion and canonical binding retain the actual world datum. Binding carries geometric h and the
published body separately. All geometry-sensitive scope/proof calculations see the mapped physical pose.

This keeps FootSupport's arithmetic and the ground core's seating, step, drop, pacing and signal algorithms.
Their proposed changes are query/proof plumbing, not a new skin rule. The dry controller keeps s = 0.
Do not add s only to the final output, where the head could enter a roof without a proof. Do not make it a
normal-dependent `s / normal.Y` tangent offset, which would restore slope float. Do not charge s to the climb
budget or count it as StepDeltaY. Preserve ordered, outward-enclosed float conversion at the adapter boundary.

The unchanged bank test has h = 0.25 and H = 0.75, so the resting centre is 1.001, inside [1.0009, 1.0011]
(`KhaozEngine.Game.Tests/Locomotion/ExplicitCharacterMovementTests.StepFootprint.cs:12`). The raw certified
support remains 0.25. A 0.25 m riser still spends 0.25 m, not 0.251 m.

**Ruling 4b-14.** Invariant 4 is profile-aware. Phase 5 must capture explicit resting body feet h + s, with
geometric support h retained separately for support proofs. Dry capture remains h. Cost if wrong: a bake/profile
format migration is required. Capturing h while the explicit body rests at h + s recreates the resting mismatch.

This is a named amendment to the program's literal invariant 4, not a claim that 4b already changes navigation.
Explicit capture, arrival and traversal must use the same adapter and pacing-free resting convention in phase 5.
Hash s and the support/body model, and do not silently reinterpret an old bake. The invariant's actual player
contract remains that the captured resting feet and the simulated resting body feet agree.

## #1299 through the swimming medium seam

**Ruling 4b-15.** WadeOnly is the explicit implementation of the requested non-swimmer rule. DryOnly is a stricter
existing policy, not the shallow-water behavior requested by #1299. Add no DeepWaterBlocks flag or SwimEnabled
field in 4b. Cost if wrong: phase 5 must expose the existing policy through its selector/configuration boundary.
There is no second threshold or duplicate water classifier to maintain.

The existing boundary already traces complete full-body coverage. For a free water level W, original half-height
H and enter fraction e, it forbids a non-tangent wet overlap at or below
`C.Y = W + H * (1 - 2 * e)`, conservatively expanded by certified error/proximity. It uses domain normals at
a lateral entry and UnitY at a vertical crossing (`KhaozEngine.Locomotion/MovementWaterBoundary.cs:47`, `:53`).
WadeOnly permits proved supported shallows and uses the existing wade speed. It uses the enter threshold even
when carried state says Swimming. DryOnly forbids every non-tangent wet overlap. All explicit policies refuse
closed, non-free-top water placement. SurfaceSwimmer permits free water and retains the existing surface,
buoyancy and water-origin rules. The relevant tests already cover thin intervals, body-dependent depth,
shore tangents, corners, retraces and stale swimming history
(`KhaozEngine.Game.Tests/Locomotion/ExplicitCharacterMovementTests.NonSwimmer.cs:10`).

For a walker on geometric support h with placement skin s, the depth fraction is
`(W - (h + s)) / (2 * H)`. At H = 0.75, e = 0.65, W = 1, the nominal deep line is actual feet Y = 0.025,
or h = 0.024 for this explicit profile. Derive the line from the supported body's actual pose. With H = 0.4,
the same threshold is actual feet Y = 0.48. It is not a global water-area bit.

The shared contact passes must treat forbidden coverage as an obstruction and keep the feasible along-shore
component. A descent onto a shallow-to-deep floor cannot bypass the rule through GroundPlacement's down pass.
A thin deep band cannot be skipped by a longer tick, even if both endpoints are dry. A tied shore/solid corner
projects against all active constraints and retraces. Recovery, seats, pushes, lag payments and continuation
remain subject to the same policy. Missing depth or overlap certificates remain unresolved, never dry.

Tangency allows an outward or parallel move with certified non-overlap and blocks inward travel. The predicate
remains inclusive at the swim-enter threshold, with the existing error enclosure, not a new epsilon. Starting
inside forbidden water is a placement refusal, not an invented escape path or a swimming fallback.

**Ruling 4b-16.** Depth and bridge/cave facts stay behind swimming's bounded medium query. The controller never
projects a water footprint onto a ground plane or samples a MapDoc directly. Cost if wrong: native producers
must deliver sufficient interval/overlap evidence. R4 cannot be emulated by a generic terrain shortcut here.

A bridge deck above water or a dry cave below it stays traversable when the full-body proof certifies no forbidden
overlap. R4 can later supply its bounded interval and depth facts through the same producer seam. This draft
does not assert native bridge or map completeness. Phase 5 bakes deep cells as unwalkable for the same explicit
WadeOnly profile. Until then, only #1299's runtime acceptance is delivered.

The flag-off acceptance needs a precise baseline. Unconfigured CharacterMovement legacy behavior stays
byte-identical. SurfaceSwimmer's medium policy/dynamics stay unchanged. The mandated contact-controller adoption
does change explicit ground geometry and land-jump timing, so the old round-bottom explicit land path cannot be
byte-identical. Compare WadeOnly versus SurfaceSwimmer on the same new core for policy-isolation rows. Do not
claim old explicit ground bytes are preserved while replacing its algorithm.

## Identity, wire and owned invariants

**Ruling 4b-17.** Keep generation 14 and its excursion byte. Add no new carried mode detector, owner handle or
lease token. After the integrated behavior passes its suite, change the explicit profile model identity to
`contact-shell-footprint-swim-v1` and include its fixed footprint fraction, placement offset and handoff/proof
recipe versions. Cost if wrong: explicit portable fingerprints change, intentionally preventing an old motion
proof from certifying a new controller.

Generation 14 remains in `KhaozEngine.NetWorld/MoveProtocol.cs`. The verified profile in
`KhaozEngine.Movement/ExplicitMovementProfile.cs` is `contact-shell-footprint-swim-v1`, with footprint
radius fraction 0.5, placement offset 0.001 m, handoff recipe version 1 and route-proof recipe version 1.
I1 rejects the old F3 certificate and matches an independently encoded recipe for dry and wet motion.
R1 compares both server heads and their clients on the same wire basis, then corrects and replays a
cross-medium stream through an isolated 0.25 m step without counting its local impulse twice. Phase 5 still owns the public
selector, canonical bake/tuning identity and any later transport amendment. Runtime and explicit motion probe
continue to use the same explicit kernel and captured query view.

| Invariant | 4b obligation |
| --- | --- |
| 1. Determinism | Same commands, state, pinned facts and profile give identical bits, including crossings and replay |
| 2. Clearance | Recovery uses shell MTV only, and every accepted push earns path proof and new axis support |
| 3. Rise/drop | Use geometric support heights and the shared paid-step ledger. The fixed offset cancels |
| 4. Capture/rest | Record the h + s explicit requirement for phase 5. No navigation acceptance is claimed here |
| 5. Unsupported ascent | Land air/slide remain phase 3. Water-origin dynamics cannot mint land footing or a fresh jump |
| 6. One surface | Resolve, clamp, placement and look-ahead use the same certified support/pin |
| 7. Eligible footing | Steep/deep/closed media never become walking footing through an adapter shortcut |
| 8. Traction | One tick-start gate follows every land segment and bound/landing query |
| 9. Partition independence | Preserve FootSupport's geometry decision. Partitioned statics bind to producer-owned canonical surfaces |

Also assert one authenticated interval, no accepted prefix on query failure, no double timer/command consumption,
one time/climb budget, shell/full-body separation and correct support keys after every ownership change.

## Suite

**Ruling 4b-18.** Acceptance is geometry-derived and includes dry parity. Preserve the two named regression bodies
byte-for-byte. Cost if wrong: new fixture registration and scope preparation are required. Weakening the bank's
height or providing off-axis feet would conceal the missing seam.

Use real Bepu boxes and meshes where solids are involved, with 30 and 60 Hz rows. Expectations use geometry and
the certified enclosure, normally no finer than half the contact skin. Exact predicate boundary fixtures use
certified synthetic facts. These rows were planned while drafting and are now implemented. Final verification is recorded in
[`2026-10-11-p4b-final.json`](../verification/2026-10-11-p4b-final.json).

| ID | Geometry/input | Required expectation |
| --- | --- | --- |
| B1 | Unchanged `AThirtyHertzColumnProducerApproachReachesAWithinStepHeightBank` | After 30 ticks, X > 1.1, Grounded, centre Y in [1.0009, 1.0011] |
| B2 | Unchanged `AnOffColumnPointAloneIsNotAnAxisSupportPlaneCertificate` | Select returns Invalid and null for the supplied off-axis candidate |
| B3 | Bank top enters a radius 0.125 footprint while axis is before X = 1 | Geometric support is 0.25 at the axis. Witness may be on the bank. Owner is producer's bank |
| B4 | Level support h, sloped plane and a 0.25 riser | Resting actual feet h + s, no slope-dependent offset. Paid rise totals 0.25, no 0.001 step event |
| B5 | Head with less than s free space under a roof | The mapped shell cannot publish its added offset through the roof. Failure/held placement has no prefix |
| L1 | Ground loses a ledge partway through a tick | Air/slide spends only RemainingTime. Budget never resets. Gravity and grant match the shared tick |
| L2 | Land fall onto supported shallows and buffered jump | Shared landing impact before clipping, pre-jump grant true, jump stamps speed and rises next tick |
| L3 | Dry run/jump onto steep support and traction band | Same slide/no-ratchet and hysteresis facts as phase 3, with no wet owner invented |
| H1 | A land substep enters swim-required free water | Swimming spends the remainder, no grounded key retained, land buffer cleared |
| H2 | Surface jump rises out of water into dry space, then returns | Water-origin ownership and swim pace persist, no upward buoyancy recapture or second launch |
| H3 | Water-origin descent lands on a 0.25 bank with time left | Shared footprint landing. Ground spends the remainder and remaining climb budget. Excursion becomes None |
| H4 | Ground, water and ground in one long accepted tick | One commitment preparation/finish, one facing turn, one signal resolution, strictly decreasing remaining time |
| S1 | Isolated bank and stair run in dry and supported shallow water | Same accepted ground ledger. No signal from buoyancy, skin, landing or recovery |
| S2 | FaceCamera turn at finite rate through a crossing | Total turn bounded by rate * whole dt. Commitment keeps its latched heading |
| Q1 | Foreign/disposed/stale lease or excluded bank | Explicit refusal, no binding/publication. Query uses the captured selected view |
| Q2 | Support reach, signal horizon, probe margin or pushed route outside scope | Refuse before backend/producer access. No repin or scope upgrade |
| Q3 | Corrected pose after predicted portal crossing | Rebuild from null hint and destination facts. Old predicted owner cannot survive |
| Q4 | Same finite feature mapped to different or missing owner, absent link | Invalid/Unresolved as appropriate. No handle-to-string or witness-to-owner fallback |
| P1 | Lifted side route crosses forbidden water although its endpoints are dry | That route cannot commit. Standing retry, if selected, earns its own complete proof |
| P2 | Recovery or seat clearance pushes across shore/play-area edge | Every push is traced. Denied push cannot publish a forbidden end or an unproved correction |
| P3 | Tangent water correction meets a real solid wall | Retrace shell and medium, keep every tied normal, stop in the joint corner |
| P4 | Late unavailable coverage/binding after earlier accepted substeps | Whole input pose held, no step/landing/grant replay, refused press consumed |
| W1 | W = 1, H = 0.75, e = 0.65, a walkable floor descending across h = 0.024 | WadeOnly stops at the profile's deep boundary and retains along-shore progress |
| W2 | Same body supported in W = 0.3 shallows | Wading remains grounded, speed follows the existing wade formula, no Swimming |
| W3 | H = 0.4 and 0.75 at a thin 0.05 m forbidden band | Each stops at its own body-depth line. Neither tunnels through it in one longer tick |
| W4 | Certified tangent, outward, parallel and inward commands | Outward/parallel admitted, inward blocked. Both contact insertion orders agree |
| W5 | DryOnly at shallow water, WadeOnly with stale Swimming flag | DryOnly blocks shallows. WadeOnly uses enter depth, not swim-exit history |
| W6 | Dry cave below wet level, bridge above it, closed flooded chamber | Located full-body facts keep dry routes dry. Closed overlap refuses placement |
| W7 | Policy contrast on the same contact core, and default legacy fixtures | SurfaceSwimmer keeps free-water behavior. Existing default legacy fluid results are unchanged |
| A1 | Exact correction, cold settle and live continuation on bank/shallow water | Shell proof, same source binding, no continuation movement/time advance, accepted land buffer preserved |
| R1 | Cross-medium command stream, two heads, correction and replay | Same state bits for the same wire basis, no duplicated local step event, generation 14 retained |
| I1 | Runtime/probe and new profile identity | Same outcomes and motion recipe. Old f3-full-capsule-v1 fingerprint cannot certify the new behavior |

Dry parity rows use no medium contacts or speed modifiers. Run flat walking, isolated step up/down, stairs,
smooth ramps, ledge-to-air, landing, coyote/buffered jump, air momentum and commitment scenarios through
`ContactController.Step` and the leased entry. The internal zero-offset leased adapter must match dry Step's
MoveState bits. The production explicit adapter's raw Y intentionally differs by s. For open-clearance rows,
compare the same kernel pose and the declared published-centre mapping, with all non-position state identical.
For roof/wall contacts, test the physical mapped shell expectation separately. A normalization that ignores the
extra head clearance is not parity. No medium applies in these rows, but the explicit placement profile still does.

Update Scene setup outside StepFootprint.cs to retain installed static handles and register floor/bank ownership
with the producer. Its current setup discards them and provides only centre-column candidates
(`KhaozEngine.Game.Tests/Locomotion/ExplicitCharacterMovementTests.cs:217`, `:224`). Widen prepared fixture scopes
for the actual controller queries. Keep B1 and B2 text unchanged and verify that explicitly.

## Files and implementation boundaries

**Ruling 4b-19.** Give the new query/proof and handoff responsibilities separate cohesive files. Preserve the
existing algorithms in their current homes. Cost if wrong: internal file boundaries may change during planning,
but no KESIZE exemption or copied controller is required.

| Files | Responsibility |
| --- | --- |
| New `Contacts/ContactController.Leased.cs`, shared tick composition file as needed | Borrowed entry, common tick lifecycle and ownership handoff |
| New `Contacts/ContactQueryContext.cs` and leased adapter | Distinct guarded support/shell operations and explicit pose mapping |
| New `Contacts/ContactMotionRoute.cs` | Attempt-local ordered path and acceptance checkpoints |
| `Contacts/GroundCore.cs`, `GroundPlacement.cs`, `AirPass.cs`, `SlideCore.cs`, `ShellMotion.cs`, `GroundSignals.cs` | Query/proof plumbing and accepted-substep continuation. Existing support, seat, pacing, air and signal rules remain shared |
| New `MovementContactSupportFacts.cs`, `IMovementContactSupportPin.cs`, `MovementQueryLease.ContactSupport.cs` | Public producer facts/capability, internal guarded binding and analytic reads |
| `MovementQueryLease.Solids.cs`, `.Sweeps.cs` and narrow buffer guards | Explicit solid shell geometry and same-scope admission |
| `MovementCapsuleResolver.cs`, new narrow route-proof helper if needed | Factor complete trace/projection/retrace, distinct solid and medium bodies |
| `ExplicitCharacterMovement.cs`, `.Fluid.cs`, `.Steps.cs`, `.Queries.cs`, `.Placement.cs`, `.Continuation.cs` | Thin entry, swimming-owned handlers and contact-owned placement. Remove duplicate land motion |
| `MovementSupportResolver.cs` | Compatibility guards and shared contact placement, no tangent model on explicit land |
| `KhaozEngine.Movement/ExplicitMovementProfile.cs` | Verified model identity and fixed profile inputs |
| Game/Server environment fixture producers | Registration-based source binding and adequate prepared scopes |
| Game Contacts/explicit tests, Movement profile tests and Server explicit tests | Suite above and existing affected contracts |

All Locomotion rows are under `KhaozEngine.Locomotion/`. Production native MapDoc/MapWorld/TileWorld adapters are
not part of this list. Their eventual implementation belongs to the gated native lane. NetWorld production
changes are limited to necessary scope preparation/validation and shared stepping plumbing. Do not rewrite
prediction, transport, persistence, the excursion codec or legacy overloads to accommodate a local token.

## Ordered execution tasks

**Ruling 4b-20.** Execute these bounded tasks in dependency order. Each implementer has one file responsibility
and its own focused tests. Integrate and review between dependent tasks. Cost if wrong: less write parallelism,
but the ground algorithm, medium proof and shared fixture cannot diverge through concurrent edits.

This is the task breakdown for a later subagent-driven implementation plan, not a dispatch instruction for this
drafting session. Each future brief names this worktree and branch, preserves unrelated work, and leaves merge,
push, release and cleanup to the owning orchestrator. Every test command runs once synchronously through
build-slot. No local stress or flake loop. Full verification belongs to the final orchestrator pass.

1. **Producer support facts and same-pin guards.** Own the new contact-support facts/capability and lease wrapper.
   Extend fixture producer registrations, including the retained real static handles. Test B3, Q1 to Q4 and
   analytic absence/room separation. Compile and run the new `ContactSupportBindingTests` plus affected
   `MovementEnvironmentContractTests`. No movement rewrite yet.
2. **Shared contact query seam and skin adapter.** Own ContactQueryContext and the mechanical query plumbing in
   Contacts. Keep FootSupport's selection/certification algorithm and dry output unchanged. Test zero-offset
   dry parity, B4/B5 and Q2, including actual shell/head scope and slope-independent skin. Run focused
   `ContactLeasedQueryTests` plus existing `ContactControllerTests`, including its Signals partial.
3. **Selected-route proof and medium constraints.** Own ContactMotionRoute and proof plumbing at shared pass
   acceptance sites. Factor the existing medium/projection/rounding logic without adding a step search.
   Test P1 to P4, tied boundaries, push/lag paths and attempt rollback. Run focused `ContactMediumRouteTests`
   plus `MovementCapsuleResolverTests` and affected non-swimmer retrace facts.
4. **Shared whole land tick through StepLeased.** Own the leased entry/common lifecycle and thin explicit land
   entry. Remove explicit land Settle/TryStepUp motion. Test B1/B2 unchanged, all dry parity rows, L1 to L3 and
   S1/S2. Run `ExplicitCharacterMovementTests.StepFootprint` by the two full method names and new
   `ExplicitContactLandTests`, plus affected controller and explicit ground/commitment fixtures.
5. **Swimming ownership and within-tick continuation.** Own swimming handlers and the shared continuation
   protocol. Keep SurfaceWaterMotion's dynamics and independent launch speed. Test H1 to H4, one gravity/time/
   climb budget, zero-time guard exhaustion and landing-before-buffered-jump. Run new
   `ExplicitContactWaterHandoffTests` plus explicit swimming/surface-jump/commitment fixtures.
6. **Placement and continuation doors.** Own shared contact placement helpers and the three explicit admission
   doors. Update public Select compatibility through those helpers. Test A1, Q3/Q4, below-knee placement,
   forbidden placement, retained land buffer and no prefix on late failure. Run focused contact placement,
   cold/correction and cell-continuation fixtures. Do not advance a continuation's timers to make it settle.
7. **Deep-water acceptance on the integrated core.** Own focused #1299 tests and any medium-constraint repair
   the new path requires. Test W1 to W7 at both body heights, supported descending banks, tangents, thin bands,
   bridge/cave intervals and closed water. Run `ExplicitCharacterMovementTests.NonSwimmer`, new
   `ExplicitContactDeepWaterTests` and affected default legacy swim fixtures. No nav bake or MoveTuning flag.
8. **Profile, heads and documentation integration.** After behavior verification, own the profile identity and
   shared runtime/probe tests I1/R1. Check both heads and replay with generation 14, null-hint correction and
   publication inside the lease. Run affected Movement explicit-profile tests and Server explicit movement/
   admission/replication tests. Sweep public docs, index this spec and update the ledger as implemented results
   warrant. Keep phase 5 and native gates explicit. The orchestrator performs whole-branch review, reconciles
   current main and runs the required full Release build/test, format and repository guards once.

These task tests are proposed fixture names where a new suite is needed. Existing fixture filters must be
resolved from source when writing the execution plan. Do not treat a shortened file stem as an executable
FullyQualifiedName. Test expectations are the Suite rows, not the example names.

## Risks and known limits

The largest integration risk is accepting a shared-core pose without its complete actual route. A geometry
result is not a medium certificate. Recovery, lifted retries, wall slides and rounded clearance pushes must
remain visible to the acceptance seam. A raw physics lease alone is also insufficient for environment scope.

The fixed skin makes the explicit and dry placement profiles distinct. Their support algorithm is one algorithm,
but raw centres, very tight roof clearance and the depth threshold cannot all be byte-identical. Parity tests
must state the offset, rather than hide it inside a loose tolerance.

The leased `IMovementContactSupportPin` now supplies owner binding. The generic fixtures implement
registration-backed binding, but this is not evidence a native producer has supplied it. Equal geometry across frames must map to canonical
owners through producer identity, never equality of local handle numbers. FootSupport's existing local tie order
does not establish insertion-order-independent canonical ownership for coincident distinct semantic owners.
Such ambiguous semantic registrations refuse until the producer supplies one authoritative owner.

Adoption blockers remain #1355 certified sweep shapes, #1334 support cost, #1347 hidden support and
#1344 small bodies. W1 proves the descending bank through certified analytic ground. A tilted Bepu
box was refused by the certified shell sweep, so this is no native tilted-bank completeness claim.
The support cost gate #1334 remains open with 96 harness-corrected time rejections and owner-approved
hosted repeat proof pending. Staged complete footprint discovery repairs hidden support, with #1347
closable only after every shape row passes and the orchestrator pushes the verified commit. Signal
look-ahead increases scope and query cost. Phase 5 navigation capture remains separate. Phase 4a's short
staircase horizon, overhung-ramp step classifier and #1354 limits remain. This phase does not repair them or claim they disappeared.

An ownership change uses bounded substep time, as phase 3 does. Surface motion preserves its existing numerical
recipe within each scheduled piece, but a mixed tick can differ from the old whole-tick explicit motion. Freeze
pure-water regression outputs and derive mixed-tick expectations from consumed pieces. Do not claim a new exact
continuous-time entry solver.

## Implementation rulings

The task ledger records the authorized migrations made after drafting. Ruling 4b-21 allows
old explicit land jump/settle/step expectations to follow the shared controller's closed forms,
with every change and its geometric derivation in the task reports. B1/B2 and water, policy,
transport, lease and admission contracts stay protected.

Ruling 4b-22 permits a fixture-only StepHeight of 0.25 for the small-body policy rows, preserving
their radius, half-height, water geometry and Blocked expectations. Real small bodies remain #1344.
Ruling 4b-23 passes certified support statuses through unchanged and skips unavailable signal
look-ahead without vetoing movement. Availability failures still refuse the whole tick.
Ruling 4b-24 retains the dry low-roof behavior, with X 1.6 and centre Y 0.849 derived from the
command and roof geometry. Ruling 4b-25 gives the separated-water-level row shorter physical
arc ticks so it keeps sequential ownership without a simultaneous-level ambiguity.

## Questions for the owning lanes

The generic draft makes the choices above rather than leaving implementation-blocking choices implicit. The code
cannot establish these native producer facts, which must be answered before G1b or phase 5 adoption:

- What released R4 producer interface will certify the full-body water intervals, free tops and bridge/cave
  overlap facts required by this medium seam?
- What native registration table will bind installed static/feature provenance, including joined features and
  frame migration, to canonical support owners and legal links under the environment pin?
- What phase 5 capture format will retain geometric support h separately from the explicit resting feet h + s,
  and distinguish old bakes from this profile without changing generation 14 prematurely?

No native answer is inferred from the generic fixtures or the historical swimming ledger.
