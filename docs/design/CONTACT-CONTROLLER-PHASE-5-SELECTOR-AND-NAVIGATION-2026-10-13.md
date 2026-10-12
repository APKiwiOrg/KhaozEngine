# Contact controller phase 5: selector, ordinary environment and navigation

Design for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5 and the navigation half of
[#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Source baseline: `ec20a2b61` on `main`, staged 20.34.0,
phases 1 to 4b and the support gates. Status: revision 2 after independent review round 1 (verdict REVISE, findings
F1 to F13, answered in the review response at the end). Written under the owner's instruction to continue the program
to completion, so every choice below is a recorded ruling for the owner's review.

The program row reads: `MoveTuning` selector, any wire fields, bake identity, traversal probe and capture on the
selected controller. Exit: both controllers selectable, legacy bakes still load, a new-controller bake round-trips.
P5-10 states exactly what "legacy bakes still load" means.

## Scope

In scope:

- A public selector that lets every ordinary head choose the legacy stepper or the contact controller.
- Water for the contact controller on ordinary heads, which today cannot run it at all.
- Navigation capture, traversal probe, bake identity and runtime graph compatibility on the selected controller,
  including explicit profiles.
- The #1299 navigation acceptance: deep cells are unwalkable for a profile that cannot swim.

Out of scope: native MapDoc movement and `MapNavTiling` vertical layers (World Authoring G1b), resolver-1 MapDoc
contexts on the contact controller (P5-15), #1355 certified sweep shapes, #1344 small bodies, the SG-12 budget and any
hosted or local load run, engine release, tag, game pin, Grimhollow edits and world edits.

## Current state

Facts at `ec20a2b61`, from code.

- No selector exists. Ordinary heads call `CharacterMovement.Step` or `StepTowards`, which run the legacy `StepCore`
  (`KhaozEngine.Locomotion/CharacterMovement.cs:97`, `:136`, `:155`). The explicit heads run
  `ContactController.StepLeased`, chosen by a non-null explicit configuration
  (`KhaozEngine.NetWorld/ExplicitPlayerMovement.cs:72`, `:79`, `:87`), and each ordinary head already has that
  explicit branch (`PlayerMoveSimulator.cs:141`, `PlayerMovementSystem.cs:166`, `WorldClient.cs:433`,
  `GroundMoveContext.Explicit.cs:11`).
- Ordinary call sites: `PlayerMoveSimulator.Step` (`:145`, serving `WorldServer`, the sharded clamp and `WorldClient`
  prediction), `PlayerMovementSystem` (`:165`), `CharacterController3D.Update` (`:195`, which rebuilds `MoveTuning`
  from mutable fields at `:175`), and `GroundMoveContext.Step` (`:82`, serving the nav bake probes,
  `NpcGroundMovement`, `MoveToRange` and `DirectMoveToRange`). `GroundMoveContext` binds no tuning. Each call passes
  one (`:68`).
- The dry contact entry `ContactController.Step` (`Contacts/ContactController.cs:30`) has no production caller. Its
  dry query context throws on wet samples (`ContactQueryContext.cs:93`), has no medium proof (`:20`), accepts every
  segment (`:49`) and skips placement and descent constraints (`:54`, `:56`, `:65`). The leased context owns
  placement, segment proofs, shore-preserving descent and initial classification
  (`LeasedContactQueryContext.Motion.cs:20`, `:26`, `:53`, `LeasedContactQueryContext.Ground.cs:8`,
  `ContactController.Leased.cs:53-92`), and already supports a zero placement offset as an internal parity profile
  (`LeasedContactQueryContext.cs:19`).
- The leased contact path consumes from its environment pin: `RebuildSelection`, `SampleAnalyticGround`,
  `BindContactSupport`, `SampleCentreWater` and `TraceWater` (`ContactController.Leased.cs:48`,
  `LeasedContactQueryContext.cs:74`, `:90`, `:117-131`). The lease authenticates world, identity, frame, currentness
  and the witnessed envelope around those calls (`MovementQueryLease.cs:74`, `MovementQueryLease.Buffers.cs:59-180`,
  `MovementQueryLease.WaterFacts.cs:11-70`).
- No production `IMovementEnvironmentProvider`, environment pin or contact support pin exists. The leased test
  fixtures bind arbitrary requested statics to synthetic owners, and `ContactLeasedQueryTests.Parity.cs:16-118`
  proves zero-offset leased and dry parity on steps, stairs, ledges, slides and walls. They do not establish a
  production provider.
- `MoveTuning` is a positional `readonly partial record struct` with 30 parameters and no `Validate` or identity
  (`MoveTuning.cs:14`). `NavBakeIdentityTests.cs:63-72` asserts exactly 30 instance fields and
  `ExplicitMovementProfileTests.cs:43-48` reflects every float and bool tuning property. `GroundCoreSettings` is
  internal (`Contacts/GroundCore.cs:9`) and `ExplicitMovementProfile` hashes a fixed foot fraction (`:55`).
- The nav bake captures columns with `PhysicsColumnProbe` axis raycasts (`PhysicsNavBake.cs:64-98`), filters
  candidates on axis headroom and compacts them (`PhysicsNavBake.Profiles.cs:53-60`, `:154-161`), then
  `NavLayerBaker` extracts layers, assigning layer ids and bounds from the retained heights (`NavLayerExtractor.cs:73`,
  `:85`, `:110`). Edges are proved by `GroundTraversalProbe` through `GroundMoveContext.Step`, so with the legacy
  stepper. Non-aquatic profiles prove with `DryContext` and never see water.
- KENB v1 shipped in 20.20.0. Its identity holds the engine version, options, sources and per profile 27 tuning fields
  (`NavBakeIdentity.cs:67-111`). The reader encodes the expected identity before reading, accepts one format and
  returns `EngineChanged` before comparing profiles (`GroundNavigationBake.Reader.cs:31`, `:46`, `:59`,
  `NavBakeIdentity.cs:125`). The runtime graph keeps only radius, half-height, slope and step height, and
  `GroundNavigation.ValidateTuning` checks those plus aquatic swim fractions (`GroundNavigation.cs:23`, `:53`).
- `MoveToRange` settling, `SwimPace` and `DirectMoveToRange` read water only through `GroundMoveContext.Medium`
  (`MoveToRange.cs:131-139`, `SwimPace.cs:16`, `DirectMoveToRange.cs:117`).
- Neither tuning nor any profile identity crosses the wire. Generation 14 (`MoveProtocol.cs:70`) carries the
  `MovementState` fields the explicit heads already use to replicate the contact controller.
- The first adopter, Grimhollow, runs ordinary heads with `TileMediumSampler` water, which keeps one surface height per
  water tile and discards body identity and bed data (`TileMediumSampler.cs:21-47`). The authored bed corners are in
  `TileGroundTriangles.cs:91-98`.

## Inherited obligations

- Phase 2: the foot disc radius moves into `MoveTuning` together with the bake identity, so bakes change once.
- 4b-13 and 4b-14: explicit bodies rest at h + s with s = 0.001 m. Capture records explicit resting feet h + s, keeps
  geometric support h, hashes s and never silently reinterprets an old bake. Dry capture stays h.
- 4b-15: WadeOnly is the #1299 walker rule, exposed through the phase 5 selector. No new water flag.
- Support gates: phase 5 bakes navigation from the complete footprint rule. The 1 mm arrival rule, roof and footprint
  protections and raw layer identity are preserved.
- Controller design line 214: a context selecting the new controller checks its physics capability at creation.

## Rulings

**P5-1. The selector lives on `MoveTuning`.** Three init-only properties outside the positional list: `Controller`
(`MovementControllerKind`, `Legacy = 0`, `Contact = 1`), `FootRadiusFraction` (default 0.5) and `Water`
(`WaterTraversalPolicy`, default mode `Legacy`). One tuning value reaches every head, the client predictor and every
bake profile, so the selection cannot diverge between them. Init properties keep the released positional constructor
binary compatible. Equality, hashing and `with` include them. Cost if wrong: a later profile object touches every head.

**P5-2. Ordinary contact heads run the leased path.** The decision, scored 1 to 10 per criterion after review round 1
priced option B's real adapter:

| Option | Certified | One runtime path | Reuse | Cost | Extensible to G1b | Risk | Total |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A. Point-sampled water in the dry context | 3 | 3 | 5 | 7 | 3 | 5 | 26 |
| B. Certified field plus a new dry adapter with its own placement, segment, shore and descent proofs | 9 | 4 | 4 | 3 | 7 | 4 | 31 |
| C. An engine ordinary environment under the existing lease and leased context, at zero offset | 9 | 10 | 9 | 5 | 8 | 7 | 48 |

C reuses identity, frame, currentness, envelope, placement, segment, shore and descent proofs unchanged, and G1b's
native providers later plug into the same path. Cost if wrong: one engine provider that a richer one replaces.

**P5-3. The ordinary environment.** An engine `OrdinaryMovementEnvironment` in `KhaozEngine.Movement` implements
`IMovementEnvironmentProvider`, its pin and `IMovementContactSupportPin` over one effective physics query view, the
game's analytic ground delegates, a `MovementBoundary` and an optional `IMovementWaterField`:

- One space per world, `MovementSpaceKey(worldId, "ordinary")`. `RebuildSelection` returns that space with no
  support, which the controller then resolves.
- `SampleAnalyticGround` answers from the ground height and normal delegates, as the dry context does today.
- `BindContactSupport` binds any static installed in the pinned physics view to the owner
  `MovementSupportKey(worldId, "static/<handle>")` in the ordinary space, and refuses statics outside the view.
  Ordinary worlds have no migrating installations, so handles are canonical within one physics generation.
- `EnumerateSupport` returns the analytic ground candidate only, because contact support comes from the footprint
  query and is bound through `BindContactSupport`.
- `Prepare` and the witness cover any scope inside the world, so every bounded query is admitted. Generations are the
  physics geometry generation and the water field's generation.
- Coordinates follow the existing explicit frame rules. The pin converts physics-local queries to the field's absolute
  coordinates and converts intervals, surfaces and `IntervalColumnXZ` back, including a nonzero physics Y origin.

The lease keeps every check it performs today. Cost if wrong: support keys need a richer owner table, which is G1b's
native question and does not change ordinary behaviour.

**P5-4. Zero placement offset.** Ordinary contact bodies compose the leased context at s = 0, promoting the internal
parity profile to an internal ordinary composition. They rest at their geometric support h. Explicit bodies keep
s = 0.001 (4b-13). One function computes resting feet h + s for runtime, capture and arrival.

**P5-5. The water field.** A public `IMovementWaterField` in `KhaozEngine.Locomotion` has `SemanticIdentity`
(a stable string for bake sources and the lease scope digest), `Generation` (constant for immutable water, advanced on
any change), `SampleCentreWater` and `TraceWater`, with the pin's types and certification contract (whole-path
contiguous spans, `CertifiedErrorMetres <= 0.001`, owned contacts, classified overlap, split free and closed
transitions, 64 span and 256 contact caps). Queries and results are in absolute world coordinates. The query carries
the ordinary space and the field echoes it. A field must be immutable for a given generation. Two producers ship:

- `MovementWaterField.Level(string worldId, float surfaceY, float floorY)`: a flat free surface over all XZ with a
  finite lower bound, for sea-level worlds and as the reference fixture.
- `TileMediumSampler` implements the field from an immutable snapshot built with the colliders. Each water tile is a
  finite column from a lower bound at or below its lowest authored bed corner to its body's free surface. Domains are
  collected bodies per region, so equal surfaces in adjacent regions stay separate domains. Boundary ids name each
  outer face, the surface and the bed. Coverage is exact capsule geometry against the union of finite columns,
  including faces, edges and rounded corners, with internal faces between tiles of one domain suppressed and
  certified rounding enclosures. An expanded box or independently inflated planes is not acceptable.

Cost if wrong: the TileWorld producer is replaced when the game moves to native worlds.

**P5-6. Head configuration.** Each head names its contact configuration, validated where the head receives the
values:

- `WorldServer`, `WorldClient`, `ShardedWorldServer` and `PlayerMoveSimulator` take the tuning at construction. Each
  gains an `IMovementWaterField` alternative to the medium delegate. With a `Contact` tuning the head builds the
  ordinary environment and an internal explicit configuration at s = 0 from the tuning's water policy and its
  `WorldBounds` boundary, and runs its existing explicit branch. A `Contact` tuning together with a public explicit
  configuration is refused as two selections.
- `GroundMoveContext` gains a constructor overload taking an `IMovementWaterField` and a `MovementBoundary`, and
  records at construction whether its effective query view (`MovementQueries ?? Physics`) supports the contact
  controller. Because tuning arrives per call, a `Contact` tuning is validated on first use and cached by tuning
  equality, and refused before stepping on an incapable context.
- `CharacterController3D` mirrors `Controller`, `FootRadiusFraction` and `Water` beside its existing tuning fields, and
  takes an optional field and boundary. `Update` revalidates whenever the composed tuning, world, field or boundary
  changed since the last validation, before stepping.
- `CharacterMovement.Step` and `StepTowards`, called directly with a `Contact` tuning, run the dry contact path, which
  has proven leased parity on land. A wet medium sample there throws `NotSupportedException` naming the field route,
  because a point delegate cannot certify water.

**P5-7. Validation rules.** One internal check serves every head:

- `Contact` requires `ShellGeometry.Validate` to pass, `FootRadiusFraction` in (0, 1], a foot radius
  `FootRadiusFraction * CapsuleRadius >= 0.01 m`, a water mode other than `Legacy`, an effective query view offering
  `IPhysicsQueryLeaseSource`, `IPhysicsCapsuleContacts` and `IPhysicsSupportFootprint`, a `MovementBoundary` rather
  than a clamp delegate, and no medium delegate without a field.
- `Legacy` requires `FootRadiusFraction == 0.5` and water mode `Legacy`, so contact-only settings are never ignored.
- Explicit configurations read `FootRadiusFraction` from the tuning and hash it in place of the fixed 0.5, so default
  fingerprints are unchanged. Their own `WaterTraversalPolicy` governs. A tuning whose `Water` is neither the default
  nor equal to that policy is refused. `Controller` is not consulted, because an explicit configuration always selects
  the contact controller.

**P5-8. Point water for pacing and approach.** A contact `GroundMoveContext` exposes `Medium` derived from
`SampleCentreWater`, so `MoveToRange` settling, `SwimPace` and `DirectMoveToRange` keep working. Motion itself always
uses the certified trace through the lease.

**P5-9. No wire change.** Generation 14 stays. Ordinary contact heads use the explicit branch's replication, which
already carries the contact controller. Selection is not exchanged, because both heads build tuning from the same
build or published catalog under the consumer protocol version. Cost if wrong: a mismatched client is corrected every
tick, and the repair is a tuning fingerprint in the consumer token, with no wire generation.

**P5-10. KENB compatibility.** "Legacy bakes still load" means: the v1 format stays readable, and an unchanged
all-legacy configuration writes byte-identical v1 identity and payload under the same engine version. A file from
another engine version stays `EngineChanged`, the existing policy under which consumers rebake on every engine bump.
Rules:

- A profile is legacy when it has no explicit profile and its tuning selects `Legacy`. An all-legacy bake writes v1.
  Any other profile makes the bake v2.
- A v2 profile record appends the controller identity (`legacy-stepper-v1`, `contact-shell-footprint-v1`, or the
  explicit `ControllerModel` and fingerprint), `FootRadiusFraction`, s, the water mode and the surface jump speed.
- The reader decodes the stored format first, encodes the expectation in the format its own profiles imply, and
  returns `ProfilesChanged` when the formats differ. Rewriting a loaded bake uses the format its profiles imply.
- The v1 tuning table and its golden bytes are frozen.

**P5-11. Runtime graph compatibility.** `GroundNavigation` stores the controller identity, foot fraction, s, water
mode and surface jump speed, and `ValidateTuning` compares them for fresh and loaded graphs, so a contact or WadeOnly
graph cannot steer a legacy or SurfaceSwimmer body of the same size.

**P5-12. Two-stage contact capture.** For a non-legacy profile:

1. Raw topology is built exactly as for a legacy profile of the same capsule: axis capture, axis headroom filter,
   compaction and `NavLayerBaker` extraction. Layer ids, node cells and order are then frozen.
2. Each frozen node slot runs `FootSupport.Find` for the profile's body and foot disc under a read lease of the bake's
   physics view, from feet start raw h + StepHeight with reach 2 * StepHeight, so its band is
   [raw h - StepHeight, raw h + StepHeight] independently of the layer's observed extrema. Analytic ground
   participates as on the runtime path. Only `Walkable` support inside the band is accepted, and the roof and start
   refusals apply. An accepted slot takes support h. A refused or out-of-band slot is marked blocked in place, never
   compacted or re-layered. Headroom is recomputed at h + s against the column's next surface, and a slot below
   2 * CapsuleHalfHeight is blocked. Layer bounds are recomputed from final heights, with membership and ids unchanged.
3. Links are regenerated from the final grids and proved by the traversal probe.

A cell whose centre axis misses every walkable surface gets no node even where the foot disc could stand, because
seeding would change raw topology. This is a stated capture limitation, filed as an issue, with a test that pins it.
Legacy profiles keep today's capture exactly.

**P5-13. Traversal on the selected controller.** `GroundTraversalProbe` keeps its hold, slices, budget and 1 mm
arrival rule and steps the profile's selected controller. Ordinary contact profiles run through the bake context's
ordinary environment at s = 0. Explicit profiles run through `ExplicitMotionProbe` (P5-14).

**P5-14. Explicit profiles in the bake.** `NavBakeProfile` gains an optional `ExplicitMovementProfile`, which is the
discriminator and supplies the portable identity. `GroundNavigationBake.Create` takes an optional
`MovementEnvironmentContext` as the runtime-only binding for explicit profiles. Its capture adapter prepares a bounded
scope per node and edge, acquires a lease, rebuilds the framed start selection, targets resting feet h + 0.001, proves
holds and edges with `ExplicitMotionProbe`, and records results before releasing the lease. Load expectations use the
same discriminator.

**P5-15. #1299 and resolver-1 contexts.** For a non-aquatic non-legacy profile, a node is deep when its resting feet
h + s are at or below W - 2 * H * e, where W is the field's nominal surface, H the capsule half-height and e
`SwimEnterDepthFraction`, which is the runtime wall's centre cap W + H(1 - 2e) in feet terms. Deep nodes are blocked.
WadeOnly stops there by its wall, and SurfaceSwimmer would swim, which a ground route cannot use. DryOnly blocks every
wet node. An aquatic SurfaceSwimmer profile keeps float nodes from the field. Legacy profiles are unchanged.
`MapPhysicsRegistration` resolver-1 contexts stay legacy-only and refuse a `Contact` tuning with a message naming
native G1b. Cost if wrong: a later factory supplies their field and boundary.

**P5-16. Guard maintenance.** Legacy behaviour and the frozen v1 golden bytes stay unchanged. Named structural guards
change deliberately: the `NavBakeIdentityTests` field count splits into the 30 positional fields (v1) and the new
properties (v2), and the `ExplicitMovementProfileTests` reflection theory takes the selectable foot fraction. Each
change is listed in its task report.

**P5-17. Stale text.** The dry `CheckMedium` message, the `ShardedClimbSignalExportTests` comment, the controller
design's capability name (now `IPhysicsSupportFootprint`) and the `ExplicitMovementProfile` phase 5 comment are
corrected.

**P5-18. Version.** Phase 5 rides staged 20.34.0, already a minor. If 20.34.0 is tagged first, take the next free
minor.

## Invariants

| Invariant | Phase 5 obligation |
| --- | --- |
| 1. Determinism | Selection is a pure function of tuning. Client and server replay agree on contact rows |
| 2. Clearance | Unchanged. Ordinary contact heads run the proven leased shell |
| 3. Rise and drop | Unchanged. Capture uses the same support rule as the down pass |
| 4. Capture and rest | Captured h + s equals simulated resting feet within the 1 mm arrival rule |
| 5. Unsupported ascent | Unchanged. Ordinary water cannot mint footing |
| 6. One surface | Runtime, capture and the deep line read the same certified support and water facts |
| 7 to 9 | Unchanged, including partition independence of capture |

## Acceptance

- **A1. Legacy unchanged.** A default tuning is `Legacy`. Every legacy movement, bake, golden fingerprint and wire test
  passes, with only the P5-16 guards changed.
- **A2. Selection matrix.** With a contact tuning, each of `PlayerMoveSimulator` (clamp through the boundary, face
  yaw), `WorldServer`, `WorldClient` prediction replay, the sharded framed head, `GroundMoveContext` (`StepTowards`
  with precise direction), `CharacterController3D`, `NpcGroundMovement`, `MoveToRange` and `DirectMoveToRange` gives
  results bit-equal to the explicit leased path at s = 0 on the same environment, on dry land and across a water
  crossing. Replay equals the server.
- **A3. Configuration refusals.** Each P5-7 refusal throws at the head's configuration point named in P5-6, including
  a restricted query view without the footprint capability and a resolver-1 MapDoc context.
- **A4. Ordinary water.** Each row runs with the Level field and with the TileWorld field, with expectations derived
  from geometry at s = 0:
  - SurfaceSwimmer entry, exit, buoyancy and hop.
  - The WadeOnly wall with shore slide, and DryOnly refusal.
  - Bed clipping and a shallow-to-deep down-seat.
  - A thin dry band crossing and a tied shore and solid corner.
  - A forbidden start, and recovery with lag payment.
- **A5. Environment binding.** Wrong world, wrong identity, stale generation, a frame rebase and a nonzero physics Y
  origin are refused or converted correctly, and a malformed field result refuses as the lease refuses today.
- **A6. TileWorld producer.** An independent exact oracle agrees across tile faces, edges and corners, adjacent bodies
  with different surfaces, equal surfaces in adjacent regions, diagonal paths through corners, a tangent corner,
  rounded-cap vertical contacts and dry tiles. A radius 0.1 capsule axis at (-0.09, -0.09) beside a water tile
  occupying x >= 0 and z >= 0 is not wet (corner distance about 0.1273).
- **A7. KENB.** Frozen v1 bytes from a previous engine return `EngineChanged`. A same-engine all-legacy bake writes v1
  with the unchanged golden fingerprint and loads. A mixed-profile bake writes v2 and round-trips to an identical graph.
  v2 returns `ProfilesChanged` when the controller, foot fraction, s, water mode or surface jump speed changes, and a
  v2 file against an all-legacy expectation returns `ProfilesChanged`.
- **A8. Capture equals rest.** Named fixtures with expected retained nodes and heights:
  - The #1270 low-lip scene, with a nonempty route across the lip.
  - The crate under a wedge, with support at 0.1 m.
  - The SG-23 sphere flank, walkable at -0.2 m, and a rim contribution.
  - Stairs, a ramp and the 0.41 m ledge.

  Every retained contact node's h equals the dry body's unpaced resting feet within 1 mm. Expected removals are asserted, as are a rejected lower layer under a
  surviving upper layer, a height change across the step-height grouping threshold, a zero-width raw layer, a
  near-roof adjusted node and the axis-miss limitation.
- **A9. #1299 and graph compatibility.** A WadeOnly profile blocks deep cells, keeps shallow wading cells and agrees
  with the runtime deep line within 1 mm. `MoveToRange` refuses a graph whose controller, foot fraction or water mode
  differs from the body, and accepts a matching one.
- **A10. Explicit capture.** Through the public bake API and its load expectation, an explicit profile keeps a named
  hold and directed edge at h + 0.001 with h retained, and round-trips.
- **A11. Allocation and cost.** A warm ordinary contact tick allocates only the named lifetime tokens of the explicit
  tick inventory. One cold and one warm local sample are recorded, not asserted. No hosted run.

## Tasks

1. Selector contract: P5-1, P5-7 tuning rules, explicit precedence, the direct dry dispatch, P5-16 guards. A1 and the
   tuning part of A3.
2. Ordinary environment and water field contract: P5-3 to P5-5 with the Level field. A4 on Level and A5.
3. TileWorld producer: the P5-5 snapshot and exact coverage. A6 and A4 on tiles.
4. Heads: P5-6, P5-8 and P5-15's resolver-1 refusal. A2 and A3.
5. Navigation profile schema and identity: the P5-14 discriminator, P5-10 and P5-11. A7 and the graph half of A9.
6. Contact capture and traversal: P5-12, P5-13 and P5-15's deep rule. A8 and the #1299 half of A9.
7. Explicit profiles in the bake: P5-14. A10.
8. Documentation, changelog, A11 sample and final verification.

## Review response, round 1

- F1: P5-3 and P5-5 bind identity, generation, coordinates, space and immutability, and the lease keeps its checks.
  The option table is re-scored and C is chosen. A5 tests binding failures.
- F2: P5-2 runs ordinary contact heads on the leased context, which owns placement, segment, shore and descent proofs.
  A4 adds the named medium rows.
- F3: P5-6 names each head's configuration point, including `GroundMoveContext` per-call tuning and
  `CharacterController3D` mirrored fields.
- F4: P5-10 states the compatibility policy and version-aware comparison. A7 pins every status.
- F5: P5-12 freezes raw topology, defines the band and blocks slots in place.
- F6: P5-14 adds the discriminator, runtime binding and capture adapter. The motion probe claim is corrected.
- F7: P5-11 extends runtime graph compatibility. A9 tests it through `MoveToRange`.
- F8: P5-8 exposes point water for pacing and approach. P5-15 refuses resolver-1 contexts. A2 and A3 cover them.
- F9: P5-5 specifies the snapshot, ids and exact finite-column geometry. A6 pins the corner example.
- F10: P5-16 authorizes the named guard changes.
- F11: A8 and A10 name fixtures, retained nodes, removals and the axis-miss limitation. A2 has a matrix.
- F12: tasks are reordered so contracts precede their consumers.
- F13: the fixture claim is corrected under Current state.
