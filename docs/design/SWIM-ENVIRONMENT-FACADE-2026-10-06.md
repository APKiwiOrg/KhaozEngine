# Swim environment facade proposal

Revision F1, proposed for migration/swimming joint review. Not approved for implementation.
Inspected engine main ca13d62d7. No new runtime API exists yet.

## Purpose and ownership

Permit review of generic swimming, surface jumps and the opt-in non-swimmer boundary independently
of native MapDoc storage. This does not approve the R2 cave representation or R3/R4 native adapters.
Migration owns water containment, native identity, exact support/collision data, #1300 appearance
and #1301 tiled bake/storage/residency/seam infrastructure. Swimming owns generic movement rules,
capsule traversal, surface jumps and profile inputs. Both lanes own seam/shore acceptance proofs.

Tracking: [#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299),
[#1291](https://github.com/APKiwiOrg/KhaozEngine/issues/1291),
[#1300](https://github.com/APKiwiOrg/KhaozEngine/issues/1300),
[#1301](https://github.com/APKiwiOrg/KhaozEngine/issues/1301),
[consumer design](https://github.com/APKiwiOrg/Grimhollow/blob/b28560ab/docs/superpowers/specs/2026-10-06-swimming-and-deep-water-design.md).

## Inspected gaps

- `MovementMedium` conflates no provider/known dry in its legacy default and has no domain identity
  or availability. That existing value remains unchanged for deliberately legacy callers.
- `CharacterMovement.StepCore` samples the medium at the starting feet and returns early through
  `SwimStep`. The latter does not receive physics, so generic swimming does not currently collide
  with statics. `GroundMoveContext.SwimClear` checks nav probes separately. New generic swimming must
  share swept collision with runtime and nav rather than treating a valid bake as player collision.
- Current hop-out tests require shallow submersion. Surface jumping needs its own eligibility,
  launch speed and replayable arc state, without changing unconfigured legacy callers.
- `groundHeight(x,z)` cannot select stacked cave/bridge supports. Native support cannot be reduced
  to that legacy fallback, even if a flat fixture could pass through it.

## Candidate API table

Names and fields below are concrete review candidates. Implementers may not start from this table
until its revision is accepted and the gate amendment below is owner-approved. Keeping the facade
in GPU-free locomotion/physics packages avoids a dependency on MapDoc or rendering.

| Candidate | Signature or fields | Contract |
| --- | --- | --- |
| `MovementQueryAvailability` | `Unresolved = 0`, `Known`, `Stale`, `Invalid` | Default is never usable. Native missing/ambiguous data never becomes known dry |
| `MovementQueryStamp` | `string ClosureId`, `ulong SnapshotRevision`, `ulong FrameId` | Closure/policy identity, immutable published query snapshot, and local-coordinate epoch. Equality is exact and ordinal |
| `MovementDomainKey` | `string WorldId`, `string BodyId` | Opaque stable domain identity supplied by the adapter. Cached immutable strings, no per-query formatting or hash-only identity |
| `MovementCapsuleQuery` | `Vector3 Centre`, `float Radius`, `float HalfHeight`, `MovementQueryStamp Expected` | Metres in the snapshot's physics-local frame, Y up. Positive finite body geometry. Engine derives feet |
| `MovementWaterSample` | `Availability`, `Stamp`, `bool InWater`, `MovementDomainKey Domain`, `float SurfaceY`, `float WadeSpeedScale` | Only a known wet result carries a domain and finite surface. Known dry is explicit. Ambiguous membership returns Invalid |
| `MovementColumnQuery` | `Vector3 Point`, `MovementDomainKey Domain`, `MovementQueryStamp Expected` | Point and vertical interval refer to one selected water domain, not all water above XZ |
| `MovementColumnSample` | `Availability`, `Stamp`, `float LowerBoundaryY`, `float SurfaceY` | Vertical extent of the connected water interval containing the point. The lower boundary is a volume limit, not necessarily solid support |
| `MovementSupportQuery` | `MovementCapsuleQuery Body`, `float MaxDrop`, `float MaxRise`, `float MaxSlopeRadians` | Finite bounded capsule support search at this body, not a highest-surface-at-XZ query |
| `MovementSupportSample` | `Availability`, `Stamp`, `bool HasSupport`, `Vector3 Feet`, `Vector3 Normal` | Known no support differs from unavailable support. A true result must satisfy the full capsule and normal limits |
| `MovementMediumSweepQuery` | `MovementCapsuleQuery Body`, `Vector3 Delta` | Swept body in one fixed local frame. No silent sampling across changing snapshot/frame revisions |
| `MovementMediumSpan` | `float EnterFraction`, `float ExitFraction`, `MovementWaterSample Water` | Ordered partition of the entire sweep, fractions in [0,1]. Known-dry spans are explicit |
| `MovementTraceResult` | `Availability`, `Stamp`, `int Written`, `int RequiredCapacity` | A partial/capacity-limited trace is Unresolved, never a complete collision proof |
| `IMovementEnvironmentQueries.SampleWater` | `MovementWaterSample SampleWater(in MovementCapsuleQuery query)` | Provider selects actual 3D membership. No bridge tag or global surface subtraction |
| `IMovementEnvironmentQueries.SampleColumn` | `MovementColumnSample SampleColumn(in MovementColumnQuery query)` | Optional to movement decisions unless a consumer explicitly requires bed/extent information. Surface jumping does not require it |
| `IMovementEnvironmentQueries.FindSupport` | `MovementSupportSample FindSupport(in MovementSupportQuery query)` | Native implementation delegates to selected exact support geometry, not a parallel terrain sampler |
| `IMovementEnvironmentQueries.TraceWater` | `MovementTraceResult TraceWater(in MovementMediumSweepQuery query, Span<MovementMediumSpan> destination)` | Conservative full-footprint domain coverage, with no omitted thin wet interval |
| `IMovementEnvironmentQueries.Stamp` | `MovementQueryStamp Stamp { get; }` | Queries belong to one immutable residency snapshot. Rebase or republish between steps, never during one |

Solid sweeps and penetration remain the existing `IPhysicsWorldQueryView` contract. The movement
context binds that view and these environment queries to the same complete owner/snapshot/frame.
Do not introduce a second collision implementation. The native adapter is responsible for proving
both views refer to the same closure and residency. A context refuses mismatched stamps before a step.

The existing physics frame is local float metres. This proposal does not choose MapDoc's stored
coordinate precision or claim unbounded float range. R2/R3 own world-to-local conversion and error
bounds. Shift/rebase tests compare equivalent local fixtures at different world origins, and prove
the frame token changes so pending queries cannot mix origins. Rotation axes follow existing engine
conventions. Domain identity survives rebasing. Cell transfer rebinds the query frame explicitly.

### Semantics that the table must not hide

Water at the body's feet determines centre-domain membership. Full capsule-footprint coverage
constrains movement near boundaries. If a footprint overlaps several compatible pieces of one
logical domain, the adapter resolves them with the agreed boundary ownership rule. Distinct
simultaneous incompatible domains are Invalid, not whichever was enumerated first. The trace must
report the earliest conservative contact with a forbidden interval. Native seam adjacency never
changes domain identity merely because storage tile ownership changed.

This full-footprint rule is intentionally a joint-review point. If native geometry cannot expose
the needed ordered coverage cheaply and exactly, refine this facade before implementation rather
than substituting endpoint sampling. Swept collision already supplies solid contacts. Water trace
supplies fluid-domain boundaries, not the final movement policy.

For each piece of the supported motion path, movement evaluates body submersion as selected
SurfaceY minus capsule feet Y. Water-column extent comes only from SampleColumn. Support comes
only from FindSupport/solid queries. No one scalar stands in for all three. The generic engine
chooses the earliest solid or forbidden-medium limit and performs collide-and-slide. Slope/step
resolution may split a path into several segments, each of which is traced completely.

An unresolved result returns a movement step outcome of `EnvironmentUnresolved`, carrying the
unchanged last verified state and zero newly executed effects. It is a hold, not a fabricated dry
state. Invalid data produces `EnvironmentInvalid` for diagnostics/recovery. These outcomes are
candidate additions to an explicit-step overload, while existing legacy Step signatures keep their
legacy results. An invalid spawn or unsupported non-swimmer placement is refused with a typed
result. The game selects its established recovery policy, never the engine inventing a spawn.

## Candidate movement policy and state

Use an explicit `WaterTraversalMode` with `Legacy = 0`, `SurfaceSwimmer`, `WadeOnly`. Do not
overload absence of a provider with native dry data. An explicit mode requires the query facade.
Legacy callers continue using their old delegates and byte-identical fixtures.

Proposed explicit-mode inputs, in addition to existing capsule/pace/hysteresis tuning:

- `SurfaceJumpSpeed`, finite and nonnegative. Zero disables the new surface jump. The game computes
  it from its independent 1.0 m water-apex knob through the existing tick-aware formula.
- `SurfaceContactToleranceMetres`, candidate 0.03 m, to be accepted against physics and wire precision.
  Test inclusivity at the exact lower/upper boundary, not only a comfortably centred float.
- Existing `SwimEnterDepthFraction`, `SwimExitDepthFraction`, `SwimSurfaceSubmersionFraction` and
  `SwimBuoyancyStiffness` stay explicit inputs. No callback retunes them per head.

Surface jump needs a known containing domain, centre within the allowed band around the resting
waterline, a fresh eligible press and capsule clearance. It never requires nearby bed/support.
Below the band, Space is ignored in surface-only mode, not saved as an ascent or buffered jump.

Proposed carried state is `WaterExcursionState` with `None = 0`, `Surface`, `AirborneFromWater`,
plus consumed jump-command identity from the existing sequenced input stream. Surface-to-airborne
launch is atomic with consuming that command. Replay of the same simulation history reproduces
the result but cannot append another launch/effect. A second press while airborne cannot launch.
Determine whether existing sequenced edge input already supplies this identity before adding wire
fields. Do not infer press edges from a reused boolean without inspecting input packing/replay.

The water-origin arc retains swim horizontal tuning, applies normal gravity/collision and cannot
be captured by buoyancy on its first ascending step. On descending water contact it returns to
Surface. On supported landing it returns to None. The state is carried through authoritative
snapshots, prediction seeds, remote interpolation, checkpoints and cell handoff. The game uses
it to keep action restrictions and cosmetic stowing, but the engine knows neither policy.

Candidate step API: `MovementStepResult StepExplicit(in MoveState state, in MoveCommand command,
float deltaSeconds, in MoveTuning tuning, WaterTraversalMode mode, MovementEnvironmentContext context)`.
`MovementStepResult` carries `MoveState State` and `MovementStepOutcome Outcome` (`Advanced`,
`Blocked`, `EnvironmentUnresolved`, `EnvironmentInvalid`, `PlacementRefused`). World-space NPC
stepping consumes the same internal core. Exact overload placement and field additions require
the current input/snapshot inventory before signature acceptance.

## Navigation contract

Planner outcome must distinguish `Complete`, `Partial`, `ProvenUnreachable`, `Unresolved` and
`Invalid`. A partial route alone does not prove no attack position exists. Only a complete search
over the required resident query set with no valid supported stance proves unreachable. The game
owns evade/reset, never the environment query or generic route planner.

Bake/profile identity covers movement mode, capsule dimensions, slope/step limits, water thresholds,
resting fraction, surface clearance policy, shore-transition proof policy, any speed/cost inputs
used by the actual planner, query schema/policy revision and world closure. Do not add unused cost
knobs. Surface jumps are manual in the consumer and do not automatically add jump edges.

Generic fixture proofs use supplied deterministic test adapters and the real solid-query backend.
They do not implement a production MapDoc sampler. Native adapter and seam acceptance remains in
migration, and cannot be claimed from a synthetic fixture alone. The aggregate 8 MiB gzip -9
consumer bake budget remains binding.

## Required contract fixtures

| Fixture | Facts | Assertion |
| --- | --- | --- |
| Dry cave under ocean | Ocean interval [-20,0], dry cave capsule centre Y=-30 | Known dry cave, not wet because surface is above the body |
| Stacked water | Independent intervals [-60,-50] and [-20,0] at the same XZ | Correct domain/SurfaceY per containing interval, no cross-domain buoyancy |
| Supported bridge | Deck feet Y=1.525 over SurfaceY=-0.37 | Known dry at actual body position, support from deck, no bridge exemption below deck |
| Deep-bed jump | Identical surface/body state over lower bounds -2 and -100 | Same configured 1.0 m apex, no SampleColumn/support requirement for eligibility |
| Submerged/edge press | Body below contact band, then reaches it without a fresh press | No launch, no ascent command and no delayed launch |
| Thin forbidden channel | Swept WadeOnly body crosses a deep interval narrower than a whole tick | Earliest boundary blocked/slid, not skipped by endpoint sampling |
| Shore and ceiling | Slope shore and solid bridge underside | Runtime and profile probe agree on clearance and supported exit |
| Availability | Missing, stale, mismatched stamp, capacity-short trace and ambiguous membership | Typed unresolved/invalid, no dry fallback or proven-unreachable attack stance |
| Seam/origin | Same water across tile seam, shore exactly on seam, frame rebase and cell handoff | No accidental shore, wrong domain or coordinate jump, old frame query refused |
| Legacy | No explicit-mode caller/provider | Existing fluid and dry fixtures unchanged |

## Proposed P0/G1 gate amendment

This amendment is NOT in effect. It requires joint acceptance of a precise facade revision and
then owner approval of this written dependency split.

Replace P0's requirement that native R2/R3/R4 signatures be approved before any generic work with:

1. G1a accepts a storage-independent semantic facade revision, exact generic API/state signatures,
   regression fixtures and file ownership. Both lanes review it against the cave proposal.
2. After owner approval of the amendment, G1a permits only generic P1/P2 consumers and synthetic
   fixtures in the engine. It does not permit production native adapters or claim native seam proof.
3. G1b remains closed until approved R2/R3/R4 native interfaces and adapters satisfy the facade.
   Native P3 integration, profile/seam/bake acceptance, game adoption and WH1 carving retain that gate.
4. Art proceeds under its existing independent authoring and owner-look gates. #1300/#1301 stay
   migration-owned. Releases remain owner-authorized. G0 approval alone does not activate this amendment.

F1 still requires review of footprint coverage, context/stamp binding, bounded failure outcomes,
input identity and state transport. A revised accepted F2 or later replaces these candidates before
implementation. No prototype code or production sampler is authorized by this document.
