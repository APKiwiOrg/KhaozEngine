# Contact controller phase 5: selector, ordinary water and navigation

Design for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5 and the navigation half of
[#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Source baseline: `ec20a2b61` on `main`, staged 20.34.0,
phases 1 to 4b and the support gates. Status: draft for independent review. Written under the owner's instruction to
continue the program to completion, so every choice below is a recorded ruling for the owner's review.

The program row reads: `MoveTuning` selector, any wire fields, bake identity, traversal probe and capture on the
selected controller. Exit: both controllers selectable, legacy bakes still load, a new-controller bake round-trips.
Earlier phases added binding obligations, listed under Inherited obligations.

## Scope

In scope:

- A public selector that lets every ordinary head choose the legacy stepper or the contact controller.
- Water for the contact controller on the ordinary path, which today throws on any water.
- Navigation capture, traversal probe and bake identity on the selected controller, including explicit profiles.
- The #1299 navigation acceptance: deep cells are unwalkable for a profile that cannot swim.

Out of scope: native MapDoc movement and `MapNavTiling` vertical layers (World Authoring G1b), a production
`IMovementEnvironmentProvider`, #1355 certified sweep shapes, #1344 small bodies, the SG-12 budget and any hosted or
local load run, engine release, tag, game pin, Grimhollow edits and world edits.

## Current state

Facts at `ec20a2b61`, from code.

- No selector exists. The ordinary heads always call `CharacterMovement.Step` or `StepTowards`, which run the legacy
  `StepCore` (`KhaozEngine.Locomotion/CharacterMovement.cs:97`, `:136`, `:155`). The explicit heads always run
  `ContactController.StepLeased`, chosen by a non-null `ExplicitMovement` config
  (`KhaozEngine.NetWorld/ExplicitPlayerMovement.cs:72`, `:79`, `:87`).
- Ordinary call sites: `PlayerMoveSimulator.Step` (`PlayerMoveSimulator.cs:145`, which also serves `WorldServer`,
  the sharded clamp and `WorldClient` prediction), `PlayerMovementSystem` (`:165`), `CharacterController3D` (`:195`),
  and `GroundMoveContext.Step` (`:82`), which serves the nav bake probes, NPC movement and range approach.
- The contact controller's dry entry `ContactController.Step` (`Contacts/ContactController.cs:30`) has no production
  caller. Its `DryContactQueryContext.CheckMedium` throws on any wet sample (`ContactQueryContext.cs:93`) and
  `TraceMedium` throws without a lease (`:77`).
- `MoveTuning` is a positional `readonly partial record struct` with 30 parameters and no `Validate` or identity
  (`MoveTuning.cs:14`). `GroundCoreSettings(float FootRadiusFraction = 0.5f)` is internal
  (`Contacts/GroundCore.cs:9`).
- The water modes are already injected into the controller core (`ExplicitContactModes? fluid` on
  `StepWithQueries`, `ContactController.cs:58`). Their medium facts come from `ContactQueryContext.SampleMedium` and
  `TraceMedium`, which forward to the lease, and `MovementWaterBoundary.Find` and `MovementSegmentProof.Medium` take
  `MovementQueryLease` directly (`MovementWaterBoundary.cs:14`, `MovementSegmentProof.cs:38`).
- The nav bake captures columns with `PhysicsColumnProbe` axis raycasts (`PhysicsNavBake.cs:64-98`,
  `PhysicsColumnProbe.cs:129`) and proves edges with `GroundTraversalProbe` through `GroundMoveContext.Step`, so with
  the legacy stepper. Non-aquatic profiles prove with `DryContext` and never see water.
- KENB v1 shipped in 20.20.0. Its identity holds the engine version, options, sources and per profile 27 tuning
  fields (`NavBakeIdentity.cs:67-111`, `NavBakeIdentity.Tuning.cs:15-46`), with no controller, foot radius,
  placement offset or water policy. Its own rule requires a `FormatVersion` bump for any change after shipping
  (`NavBakeIdentity.cs:17-22`). A wrong version refuses as `UnsupportedFormat`.
- Neither tuning nor any profile identity crosses the wire. Generation 14 (`MoveProtocol.cs:70`) carries the
  replicated `MovementState` fields that the explicit heads already replicate the contact controller with.
- No production `IMovementEnvironmentProvider`, environment pin or contact support pin exists. The test fixtures
  certify a synthetic floor and slab only.
- The first adopter, Grimhollow, runs the ordinary path with `TileMediumSampler` water and loads KENB through its own
  bake code. `TileMediumSampler` holds one surface height per water tile from `TileWaterBodies.Collect`
  (`KhaozEngine.TileWorld.Physics/TileMediumSampler.cs:21-47`).

## Inherited obligations

- Phase 2: the foot disc radius moves into `MoveTuning` together with the bake identity, so bakes change once.
- Phase 3: the contact tick takes the same inputs as legacy `StepCore`, so selection is a one-line switch.
- 4b-13 and 4b-14: explicit bodies rest at h + s with s = 0.001 m. Capture records explicit resting feet h + s,
  keeps geometric support h separately, hashes s and never silently reinterprets an old bake. Dry capture stays h.
- 4b-15: WadeOnly is the #1299 walker rule. Phase 5 exposes the existing water policy through its selector. No
  `DeepWaterBlocks` or `SwimEnabled` field.
- Support gates: phase 5 bakes navigation from the complete footprint rule. The 1 mm arrival rule, roof and footprint
  protections and raw layer identity are preserved.
- Design doc line 214: a context selecting the new controller checks its physics capability at creation.

## Rulings

**P5-1. The selector lives on `MoveTuning`.** Three init-only properties outside the positional list:
`Controller` (`MovementControllerKind`, `Legacy = 0`, `Contact = 1`), `FootRadiusFraction` (default 0.5) and `Water`
(`WaterTraversalPolicy`, default mode `Legacy`). One tuning value already reaches every head, the client predictor and
every bake profile, so the selection cannot diverge between server, client and navigation, and the bake identity
already encodes tuning. Init properties keep the released positional constructor binary compatible. Cost if wrong: a
later separate profile object touches every head.

**P5-2. One switch.** `CharacterMovement.StepCore` and the `StepTowards` path dispatch to `ContactController.Step`
when `Controller == Contact`, with `GroundCoreSettings` built from `FootRadiusFraction`. Nothing else in the legacy
path changes, so a `Legacy` tuning stays byte-identical. The internal `settings` override remains for tests only.

**P5-3. Validation at creation.** A shared internal check runs in every constructor that accepts a tuning and a
world: `GroundMoveContext`, `PlayerMoveSimulator`, `PlayerMovementSystem`, `CharacterController3D` and
`PhysicsNavBake.BuildProfile`. It throws `ArgumentException` or `NotSupportedException` at construction:

- `Contact` requires `ShellGeometry.Validate` to pass, `FootRadiusFraction` in (0, 1], a foot radius
  `FootRadiusFraction * CapsuleRadius >= 0.01 m` (the support gates' configuration floor), a water mode other than
  `Legacy`, and a physics world, when given, that offers `IPhysicsQueryLeaseSource` and `IPhysicsSupportFootprint`.
- `Legacy` requires `FootRadiusFraction == 0.5` and water mode `Legacy`, so a contact-only setting is never
  silently ignored and the v1 bake identity stays complete.

Per-step guards stay as a backstop. Cost if wrong: one extra validation call per head construction.

**P5-4. Ordinary water through a certified field.** A new public `IMovementWaterField` in `KhaozEngine.Locomotion`
has exactly the environment pin's water members, `SampleCentreWater(in MovementBodyQuery)` and
`TraceWater(in MovementMediumSweepQuery, Span<MovementCoverageSpan>, Span<MovementDomainContact>)`, under the same
certification contract (whole-path contiguous spans, `CertifiedErrorMetres <= 0.001`, owned contacts, classified
overlap, split free and closed transitions, the 64 span and 256 contact caps). Heads accept the field. A `Contact`
head given only a `MovementMedium` delegate refuses at creation, because a point sample cannot certify a path. A
`Legacy` head given a field derives its point medium from `SampleCentreWater`, so a game passes one object for
either controller. The decision, scored 1 to 10 per criterion:

| Option | Certified | One water model | Reuse | Cost | Extensible to G1b | Risk | Total |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A. Point-sampled water in the dry context | 3 | 3 | 5 | 7 | 3 | 5 | 26 |
| B. Certified water field into the dry context | 9 | 8 | 8 | 7 | 8 | 7 | 47 |
| C. Ordinary heads run the explicit leased path over an engine-built provider | 9 | 9 | 8 | 3 | 7 | 4 | 40 |

A reintroduces uncertified water into a certified controller. C needs a generic production support enumeration and
owner binding over arbitrary statics, which is G1b's open registration question, and moves ordinary bodies onto the
explicit placement offset. B reuses the injected water modes unchanged and gives G1b a seam its pins already match.
Cost if wrong: the field is a small public interface that a later provider-based route can wrap.

**P5-5. One medium-source seam.** An internal medium source with `SampleCentreWater`, `TraceWater` and the
per-interval domain consistency facts is implemented by `MovementQueryLease` and by an internal ordinary adapter over
`IMovementWaterField`. `ContactQueryContext.SampleMedium` and `TraceMedium`, `MovementWaterBoundary.Find`,
`MovementSegmentProof.Medium`, `GroundCore.Medium` and `ExplicitContactModes` consume the seam instead of the lease.
The lease's coverage validation (`MovementQueryLease.Buffers.cs:59-140`) and domain consistency
(`MovementQueryLease.WaterFacts.cs:11-50`) move into one shared validator that both sources run. Explicit behaviour is
byte-identical and every explicit suite passes unchanged. Cost if wrong: a refactor of lease-bound water proofs.

**P5-6. Two producers.** `TileMediumSampler` implements `IMovementWaterField` exactly. Each water tile is a column
from a certified lower bound at or below its solid bed to its body's free surface. Lateral boundaries are the
axis-aligned vertical tile faces next to a dry tile or a different surface. Each collected body is one domain, and
boundary ids name the face, the surface and the bed. A capsule path is traced in closed form against axis-aligned
planes. A public `MovementWaterField.Level(string worldId, float surfaceY, float floorY)` supplies a flat free surface
over all XZ, for sea-level worlds and as the reference fixture. Cost if wrong: the TileWorld producer is replaced
when the game moves to native worlds, where G1b supplies its own field.

**P5-7. Placement offsets.** An ordinary contact body rests at its geometric support h (s = 0). An explicit body
rests at h + 0.001 (4b-13). Capture stores h per node. The profile identity carries s, and resting feet are h + s,
computed in one function used by capture, arrival and runtime checks.

**P5-8. No wire change.** Generation 14 stays. The explicit heads already replicate the contact controller over the
existing `MovementState` fields, and the ordinary contact path carries the same `MoveState`. Selection is not
exchanged, because both heads build tuning from the same build or published catalog under the consumer protocol
version. Cost if wrong: a mismatched client predicts with the other controller and is corrected every tick. The
repair is a tuning fingerprint in the consumer token, which needs no wire generation.

**P5-9. KENB v2 only when needed.** A bake whose profiles are all `Legacy` writes v1 byte-for-byte, so the golden
fingerprint and every legacy consumer stay unchanged. Any other profile makes the bake v2. Each v2 profile record
appends the controller identity string (`legacy-stepper-v1`, `contact-shell-footprint-v1` or the explicit profile's
`ControllerModel` and fingerprint), `FootRadiusFraction`, s, the water mode and the surface jump speed. The payload
layout is unchanged because nodes store h. The reader accepts v1 and v2. Cost if wrong: two identity encodings.

**P5-10. Capture on the contact controller.** For a non-legacy profile, `BuildProfile` replaces each raw captured
node's axis height with `FootSupport.Find` under the profile's body and foot disc, started from the node's own band.
The node keeps its raw layer identity (cell and layer index). A refusal, or support outside the node's layer band,
removes the node and never re-layers it. Legacy profiles keep the raw axis height.

**P5-11. Traversal on the selected controller.** `GroundTraversalProbe` keeps its hold, slices, budget and 1 mm
arrival rule, and steps whichever controller the profile's tuning selects through the P5-2 switch, with the profile's
water field. Explicit profiles prove edges through `ExplicitMotionProbe` with a caller-supplied
`MovementEnvironmentContext`, the seam the motion probe already uses.

**P5-12. #1299 navigation.** For a non-aquatic non-legacy profile, a node whose resting feet lie past the deep line
is unwalkable. The deep line uses the runtime's formula from `MovementWaterBoundary` (depth fraction of the body
reaching `SwimEnterDepthFraction`). `WadeOnly` stops there by its wall, and `SurfaceSwimmer` would swim, which a ground
route cannot use. `DryOnly` removes every wet node. An aquatic `SurfaceSwimmer` profile keeps float nodes from the
field. Legacy profiles keep today's behaviour exactly.

**P5-13. The bake's water source.** `GroundMoveContext` accepts an `IMovementWaterField`. Non-legacy profiles sample
and trace water through it. Legacy profiles keep the delegate and `PhysicsNavBakeOptions.SampleWater` unchanged.

**P5-14. Stale text.** The dry `CheckMedium` message, the `ShardedClimbSignalExportTests` comment, the controller
design's capability name (now `IPhysicsSupportFootprint`) and the `ExplicitMovementProfile` phase 5 comment are
corrected.

**P5-15. Version.** Phase 5 rides staged 20.34.0, already a minor. If 20.34.0 is tagged first, take the next free
minor.

## Invariants

| Invariant | Phase 5 obligation |
| --- | --- |
| 1. Determinism | Selection is a pure function of tuning. Client and server replay agree on contact rows |
| 2. Clearance | Unchanged. The ordinary path runs the proven dry shell |
| 3. Rise and drop | Unchanged. Capture uses the same support rule as the down pass |
| 4. Capture and rest | Captured h + s equals simulated resting feet within the 1 mm arrival rule |
| 5. Unsupported ascent | Unchanged. Water on the ordinary path cannot mint footing |
| 6. One surface | Runtime, capture and the deep line read the same certified support and water facts |
| 7 to 9 | Unchanged, including partition independence of capture |

## Acceptance

- **A1. Legacy unchanged.** A default tuning is `Legacy`. Every existing legacy movement, bake, golden fingerprint
  and wire test passes without edits.
- **A2. Selection.** For one scene, a contact tuning on each ordinary head gives results bit-equal to calling
  `ContactController.Step` directly. Client prediction replay equals the server on contact rows, including a water
  crossing.
- **A3. Creation refusals.** Each P5-3 refusal and the P5-4 delegate refusal throws at construction, before any step.
- **A4. Ordinary water.** SurfaceSwimmer entry, exit, buoyancy and hop, the WadeOnly deep-line wall with shore slide,
  DryOnly refusal and bed clipping run on the ordinary path with the flat field and with the TileWorld field.
  Expectations derive from geometry, with s = 0.
- **A5. One validator.** Each malformed producer result (a span gap, error above 1 mm, a contact outside its span, an
  unsplit free and closed transition, a cap overflow) refuses identically from the lease and from the field.
- **A6. TileWorld producer.** Coverage is exact across tile edges, between bodies with different surfaces, along
  diagonal paths through corners, for a capsule straddling a lateral face (tangent and overlapping) and over dry
  tiles. An independent exact oracle agrees.
- **A7. KENB.** An all-legacy bake writes v1 with the unchanged golden fingerprint. A contact bake writes v2 and
  round-trips to an identical graph. v2 refuses as `ProfilesChanged` when the controller, foot fraction, s or water
  mode changes. A v1 file loads under the new reader.
- **A8. Capture equals rest.** On the #1270 low-lip scene, stairs, a ramp, the crate under a wedge and curved props,
  every contact node's h equals the dry body's unpaced resting feet within 1 mm. Raw layer identity is preserved and
  the 0.41 m ledge row holds.
- **A9. #1299.** A WadeOnly profile marks deep cells unwalkable, leaves shallow wading cells walkable, and agrees with
  the runtime deep line within 1 mm. A legacy profile's water facts are byte-identical.
- **A10. Explicit capture.** Explicit nodes rest at h + 0.001 with h retained, edges prove through the motion probe
  with the fixture environment, and the bake round-trips.
- **A11. Allocation and cost.** A warm ordinary contact tick with a water field adds no allocation beyond the
  recorded dry inventory. One cold and one warm local sample are recorded, not asserted. No hosted run.

## Tasks

1. Selector and validation: P5-1 to P5-3, A1 to A3 without water.
2. Medium-source seam and shared validator: P5-5, A5, explicit suites unchanged.
3. Ordinary water field: P5-4 and the flat field, A4 on the flat field.
4. TileWorld producer: P5-6, A6, and A4 on tiles.
5. Heads and prediction: field plumbing on every ordinary head, A2 with water.
6. KENB v2 and capture: P5-7, P5-9, P5-10, P5-13, A7 and A8.
7. Traversal, #1299 and explicit capture: P5-11, P5-12, A9 and A10.
8. Documentation, changelog, A11 sample and final verification.
