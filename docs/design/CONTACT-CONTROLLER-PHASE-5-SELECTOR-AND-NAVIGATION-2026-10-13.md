# Contact controller phase 5: selector, ordinary runtime and navigation

Design for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5 and the navigation half of
[#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Source baseline: `ec20a2b61` on `main`, staged 20.34.0,
phases 1 to 4b and the support gates. Status: phase 5a released in 20.37.0, with controller execution
rulings C1 to C20 applied. Phase 5b ground navigation is released in 20.39.0. Contact aquatic navigation,
footprint-only seeds and public explicit bake profiles are deferred to
[#1389](https://github.com/APKiwiOrg/KhaozEngine/issues/1389). The revision 6 design and its review history below
remain the historical baseline. Current APIs, package placement, frame rules and lifetime contracts live in
[Locomotion](../../KhaozEngine.Locomotion/README.md#ordinary-contact-environment),
[Movement](../../KhaozEngine.Movement/README.md#absolute-ground-movement-context) and
[NetWorld](../../KhaozEngine.NetWorld/README.md#ordinary-contact-configuration). Written
under the owner's instruction to continue the program to completion, so every choice below is a recorded ruling for
the owner's review.

The program row reads: `MoveTuning` selector, any wire fields, bake identity, traversal probe and capture on the
selected controller. Exit: both controllers selectable, legacy bakes still load, a new-controller bake round-trips.

**Split.** Review showed two independent bodies of work, so phase 5 splits like phases 2 and 4:

- **5a, ordinary contact runtime** (this revision, specified in full): the selector, validation, an engine ordinary
  environment, certified ordinary water and one effective movement binding through every head's lifecycle. Exit:
  both controllers selectable on every head.
- **5b, navigation** (specified before it starts): capture, traversal, bake identity, runtime graph compatibility and
  #1299's navigation acceptance. Exit: legacy bakes still load, a new-controller bake round-trips, #1299 closes.
  Its binding inputs are listed at the end.

## Scope of 5a

In scope: everything needed for any ordinary head to run the contact controller, dry and wet, with server, client
prediction, sharded cells and every placement door agreeing.

Out of scope: all navigation change (5b), native MapDoc movement and `MapNavTiling` vertical layers (World Authoring
G1b), resolver-1 MapDoc contexts on the contact controller (P5A-12), #1355 certified sweep shapes, #1344 small bodies,
the SG-12 budget, any hosted or local load run, engine release, tag, game pin, Grimhollow edits and world edits.

## General sweep prerequisite disposition

#1355 is complete for the admitted families in the
[general-sweep acceptance record](CERTIFIED-GENERAL-SWEEPS-2026-10-18.md#task-6-captured-content-evidence-and-gb-r13),
including acquired cost-scene ticks and captured player-scale split/compound hull stairs.
[Lane C](CONTACT-CONTROLLER-FOLLOWUPS-C-2026-10-19.md) resolves #1494 exact quarter-turn
tread-edge support using represented installed geometry. Historical GB-R13 evidence is retained. Native G1b, SG-12/#1334 cost, navigation and adoption remain separate. P5A-2, P5A-9 and
P5A-14, including A2, A3 and A5, retain their original obligations. The baseline observations below
remain historical evidence, not the current backend domain.

## Current state

Facts at `ec20a2b61`, from code.

- No selector exists. Ordinary heads call `CharacterMovement.Step` or `StepTowards`, which run the legacy `StepCore`
  (`KhaozEngine.Locomotion/CharacterMovement.cs:97`, `:136`, `:155`). The network heads branch to the explicit leased
  path on a non-null public explicit configuration (`KhaozEngine.NetWorld/ExplicitPlayerMovement.cs:72`, `:79`,
  `:87`, dispatched at `PlayerMoveSimulator.cs:144`, `PlayerMovementSystem.cs:166` and `WorldClient.cs:433`).
- That configuration also gates every lifecycle door: `WorldServer.Placement.cs:37` and `:53`,
  `ShardedWorldServer.Placement.cs:45`, `ShardedWorldServer.Frame.cs:128`, `:145` and `:158`,
  `ExplicitCellAdmission.cs:65-67` and `PlayerMoveSimulator.Explicit.cs:96`.
- `GroundMoveContext` has an ordinary `Step` (`:68`, per-call tuning, serving the nav bake probes,
  `NpcGroundMovement`, `MoveToRange` and `DirectMoveToRange`) and a separate caller-selected `StepExplicit`
  (`GroundMoveContext.Explicit.cs:11`). `CharacterController3D.Update` (`:159-195`) has only an ordinary path and
  rebuilds `MoveTuning` from mutable fields at `:175`.
- The dry contact entry `ContactController.Step` (`Contacts/ContactController.cs:30`) has no production caller. Its dry
  context throws on wet samples (`ContactQueryContext.cs:93`), has no medium proof (`:20`), accepts every segment
  (`:49`) and skips placement and descent constraints (`:54`, `:56`, `:65`). The leased context owns placement,
  segment proofs, shore-preserving descent and initial classification (`LeasedContactQueryContext.Motion.cs:20`,
  `:26`, `:53`, `LeasedContactQueryContext.Ground.cs:8`, `ContactController.Leased.cs:53-92`).
- The placement offset defaults to 0.001 in `LeasedContactQueryContext.Initialize` (`LeasedContactQueryContext.cs:40`),
  and `ExplicitCharacterMovement.Placement.cs:107-110` and `ExplicitCharacterMovement.Continuation.cs:14-20` build
  default-offset contexts for placement, settling and continuation. Zero offset exists only as an internal parity
  profile, and `ContactLeasedQueryTests.Parity.cs:16-118` proves leased and dry parity at zero offset through a
  manually initialized `StepWithQueries` on steps, stairs, ledges, slides and walls.
- The leased path consumes from its environment pin `RebuildSelection`, `SampleAnalyticGround`, `BindContactSupport`,
  `SampleCentreWater` and `TraceWater` (`ContactController.Leased.cs:48`, `LeasedContactQueryContext.cs:74`, `:90`,
  `:117-131`). The lease authenticates world, identity, frame, currentness and the witnessed envelope around them
  (`MovementQueryLease.cs:74`, `MovementQueryLease.Buffers.cs:59-180`, `MovementQueryLease.ContactSupport.cs:38-41`,
  `:128-145`, `MovementQueryLease.ContactQueries.cs:22`, `:79`). Bindings are certified from the pin's captured
  registration, never from a handle (`MovementContactSupportFacts.cs:72`, ruling 4b-8).
- No production environment provider, pin, contact support pin or registration exists. `MovementSupportResolver` is
  the only consumer of `EnumerateSupport` (`MovementSupportResolver.cs:42`) and has no production caller.
- `MoveTuning` is a positional `readonly partial record struct` with 30 parameters and no `Validate` or identity
  (`MoveTuning.cs:14`). `NavBakeIdentityTests.cs:63-72` asserts exactly 30 instance fields and
  `ExplicitMovementProfileTests.cs:43-48` reflects every float and bool tuning property. `ExplicitMovementProfile`
  hashes a fixed foot fraction (`:55`). The explicit kernel requires positive gravity, fall speed and swim-enter
  fraction (`ExplicitCharacterMovement.Queries.cs:46-57`), which `GroundMoveContext` does not check (`:111`, `:129`).
- Ordinary heads hold an `IPhysicsWorld`. The leased capabilities live on a query view
  (`BepuPhysicsWorld.QueryView.cs:25`), created by `IPhysicsWorld.CreateQueryViewExcludingStatics`
  (`IPhysicsWorld.cs:88`), which may throw. Certified sweeps need `IPhysicsCapsuleSweep`
  (`MovementQueryLease.Sweeps.cs:25`).
- The public explicit read scope comes from a game callback given the basis and frame (`ExplicitPlayerMovement.cs:14`).
  `WorldClient` keeps one read across a whole reconcile replay (`WorldClient.ExplicitMovement.cs:16-33`,
  `ClientPrediction.cs:382-385`).
- `MoveToRange` settling, `SwimPace` and `DirectMoveToRange` read water only through `GroundMoveContext.Medium`
  (`MoveToRange.cs:131-139`, `SwimPace.cs:16`, `DirectMoveToRange.cs:117`).
- Generation 14 (`MoveProtocol.cs:70`) carries the `MovementState` fields the explicit heads use to replicate the
  contact controller. Neither tuning nor any profile identity crosses the wire.
- The first adopter, Grimhollow, runs ordinary heads with `TileMediumSampler` water, which keeps one surface per water
  tile and discards body identity and bed data (`TileMediumSampler.cs:21-47`). `TileWorldColliders` installs every
  collider itself with a known kind (`TileWorldColliders.cs:135-143`). Bed corners are in
  `TileGroundTriangles.cs:91-98`.

## Inherited obligations for 5a

- Phase 2: the foot disc radius moves into `MoveTuning`.
- 4b-8: canonical owners come from a captured registration, never from a static number. Analytic ground is a scoped
  fact, not an unrestricted global callback.
- 4b-9: cold placement, exact correction and live continuation share the support and placement operations.
- 4b-13: explicit bodies rest at h + 0.001. 4b-15: WadeOnly is the #1299 walker rule, exposed through the selector.
- Support gates: the 1 mm arrival rule, roof and footprint protections and raw layer identity are preserved, and no
  tolerance widens.
- Controller design line 214: a context selecting the new controller checks its capability at creation.

## Rulings for 5a

**P5A-1. The selector lives on `MoveTuning`.** Three init-only properties outside the positional list: `Controller`
(`MovementControllerKind`, `Legacy = 0`, `Contact = 1`), `FootRadiusFraction` (default 0.5) and `Water`
(`WaterTraversalPolicy`, default mode `Legacy` with surface jump speed 0). One tuning value reaches every head and the
client predictor, so the selection cannot diverge. Init properties keep the released positional constructor binary
compatible. Equality, hashing and `with` include them. Cost if wrong: a later profile object touches every head.

**P5A-2. Ordinary contact heads run the leased path.** Scored 1 to 10 per criterion after round 1 priced option B's
real adapter:

| Option | Certified | One runtime path | Reuse | Cost | Extensible to G1b | Risk | Total |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A. Point-sampled water in the dry context | 3 | 3 | 5 | 7 | 3 | 5 | 26 |
| B. Certified field plus a new dry adapter with its own placement, segment, shore and descent proofs | 9 | 4 | 4 | 3 | 7 | 4 | 31 |
| C. An engine ordinary environment under the existing lease and leased context, at zero offset | 9 | 10 | 9 | 4 | 8 | 6 | 46 |

C's cost and risk are re-scored after round 2 added the registration, served domain and lifecycle binding. It remains
first because every one of those is also required by any certified ordinary route. Cost if wrong: one engine provider
that a richer one replaces.

**P5A-3. One composition value.** An internal `ContactComposition` carries the placement offset and the
`GroundCoreSettings` foot fraction through `StepLeased`, `ValidatePlacement`, `SettlePlacement`, continuation and every
support placement helper. Public explicit entry points compose offset 0.001 and the tuning's foot fraction. Ordinary
entry points compose offset 0 throughout, so an ordinary body rests at h on every door. One function computes resting
feet h + s.

**P5A-4. The ordinary environment.** An engine `OrdinaryMovementEnvironment` in `KhaozEngine.Movement` implements
`IMovementEnvironmentProvider`, its pin and `IMovementContactSupportPin` over an effective query view, a registration,
analytic ground delegates, a `MovementBoundary` and an optional `IMovementWaterField`:

- **Query view.** The binding records view ownership. When the head supplies only a physics world, the binding creates
  the view once through `CreateQueryViewExcludingStatics` with the head's exclusions, retains it and disposes it with
  the binding. A caller-supplied view (`GroundMoveContext.MovementQueries`, or the view of a public explicit
  environment) stays borrowed and is never disposed by the binding, as `GroundMoveContext` documents today. A source
  that cannot create a view refuses at configuration.
- **Space.** One per world, `MovementSpaceKey(worldId, "ordinary")`. `RebuildSelection` returns it with no support.
- **Registration.** A public `OrdinaryMovementRegistration`, built by whoever installs the statics, maps each installed
  static handle and feature range to a canonical owner `MovementSupportKey`, with explicit aliasing (several pieces,
  one owner) and explicit exclusions, captured under the installation read lease. Owner names come from provenance,
  never from handle values, so identical handles in separate cell worlds stay distinct and partitioned copies share
  an owner. A missing, excluded, ambiguous or stale registration makes `BindContactSupport` return Unresolved, as
  4b-8 requires. `TileWorldColliders` builds one: one owner per region's ground and one per placed collider
  provenance. Authored entries, exclusions, extent, semantic digest and installation `GeometryGeneration` remain
  fixed. `ApplyRuntimeStatics` publishes immutable runtime provenance and the expected static revision inside the
  source's exclusive mutation interval. Live `Generation` advances once per successful nonempty batch. Existing
  bindings reacquire against that revision. Raw static writes, independent registrations and partial failures are
  never silently accepted. A possibly edited failed installation must be rebuilt.
- **Analytic ground.** Owner `MovementSupportKey(worldId, "analytic-ground")`. Facts are scoped to the served domain
  (P5A-5), which is the witnessed query region of every lease, never to one request's reach band, so every request at one axis within an interval gets the
  same answer and the workspace's analytic cache stays consistent. When the delegate's height at the axis lies inside
  the region's vertical extent, the pin returns that height and the delegate's normal, and `FootSupport` and the
  terrain-wall checks filter by their own reach, exactly as the dry path reads the delegate. When it lies below the
  region, the region certifiably holds no analytic surface at that axis and the answer is Known with null. When it
  lies above the region, or is non-finite, the answer is Unresolved, so a body is never told there is no terrain
  where terrain encloses the region.
- **Enumeration.** `EnumerateSupport` returns Unresolved. The contact path does not call it, and an ordinary
  environment does not serve `MovementSupportResolver` rather than certifying a false absence.
- **Preparation.** The served domain (P5A-5) is separate from the playable `MovementBoundary`, which only constrains
  capsule centres and centre paths, and extends past the playable edge by the exterior margins. `Prepare` admits
  exactly that one scope and never clips it. The witness lists the physics, registration, analytic and water
  resources, covers known-empty regions, and its generation combines the physics geometry, registration and water
  field generations.
- **Coordinates.** Existing explicit frame rules apply. The pin converts physics-local queries to the field's absolute
  coordinates and converts intervals, surfaces and `IntervalColumnXZ` back, including a nonzero physics Y origin.

**P5A-5. One served-domain scope.** Scopes exist so a streaming provider can certify only what it has prepared. An
ordinary environment has its whole world resident, and the lease's scope checks are constant-time bounds checks
(`MovementQueryLease.ContactSupport.cs:120-146`), so every ordinary lease uses one constant scope, the served domain,
instead of a per-tick plan:

- **Declared extent.** The static installer declares the world's finite extent with its registration: the bounds of
  every installed static, the analytic ground's height range and the water field's extent. `TileWorldColliders`
  derives it from the tile document. A sharded cell declares its cell world's extent. The client's served domain is
  its physics island's horizontal extent with the same vertical extent, so a client never claims data it does not
  hold. Reserve possible runtime static volume in this declaration before binding. Sanctioned batches cannot
  enlarge the extent or rely on the tuning-expanded served domain for containment.
- **Margins.** The domain is the declared extent expanded horizontally by the capsule, the foot disc, the support band,
  the `GroundSignals` look-ahead of three capsule radii and the footprint admission bounds (computed with
  `SupportFootprintBounds`), and vertically by the jump apex over the highest geometry plus body height above, and by
  the support reach below. `MaxRise` and `MaxDrop` span the domain's height.
- **Outside the domain.** A query outside it means the body has left the world (or, on a client, its island). It
  refuses as today, and the step holds with the lease's existing outcome. That is a genuine absence of data, not a
  planner miss, so there is no retry and no attempt boundary.

Because every lease of one binding has the same scope, analytic answers at an axis are identical across ticks,
replays and imports, and client replay runs under the same scope as the original prediction. Server and client agree
wherever both hold the data.

**P5A-6. The water field.** A public `IMovementWaterField` in `KhaozEngine.Locomotion` has `SemanticIdentity`,
`Generation` (constant for immutable water, advanced on change), `Extent` (the finite region it describes, outside of
which it is dry), `SampleCentreWater` and `TraceWater`, with the pin's
types and certification contract (whole-path contiguous spans, `CertifiedErrorMetres <= 0.001`, owned contacts,
classified overlap, split free and closed transitions, 64 span and 256 contact caps). Queries and results use
absolute world coordinates. The query carries the ordinary space and the field echoes it. Two producers ship:

- `MovementWaterField.Level(string worldId, float surfaceY, float floorY)`: a flat free surface over all XZ with a
  finite lower bound, for sea-level worlds and as the reference fixture.
- `TileMediumSampler` implements the field from an immutable snapshot built with the colliders. Each water tile is a
  finite column from a bound at or below its lowest authored bed corner to its body's free surface. Domains are
  collected bodies per region, so equal surfaces in adjacent regions stay separate domains. Boundary ids name each
  outer face, the surface and the bed. Coverage is exact capsule geometry against the union of finite columns,
  including faces, edges and rounded corners, with internal faces of one domain suppressed and certified rounding
  enclosures. An expanded box or independently inflated planes is not acceptable.

**P5A-7. One effective movement binding per head.** Each head, and each sharded cell runtime, resolves exactly one
binding at configuration: `Legacy`, `OrdinaryContact` (engine-built from the tuning, view, registration, boundary and
field) or `PublicExplicit` (the existing public configuration). Every lifecycle door uses that binding instead of
testing the public configuration: movement steps, read scopes, spawn and resume, teleports, configured-spawn reset,
correction and replay, imports, continuation and placement. Each listed public-configuration branch under Current
state is converted. A contact tuning together with a public explicit configuration is refused as two selections.

**P5A-8. Head configuration points.**

- `WorldServer`, `WorldClient`, `ShardedWorldServer` and `PlayerMoveSimulator` take tuning at construction and gain
  an `IMovementWaterField` and `OrdinaryMovementRegistration` alongside their medium delegate. They validate and build
  the binding there. Sharded cells build one binding per cell runtime, shared by stepping, settling and import
  admission.
- `GroundMoveContext` gains an overload taking the field, registration and a `MovementBoundary`, and records at
  construction whether its effective view supports the contact controller. Tuning arrives per call, so a contact
  tuning is validated on first use and cached by tuning equality, and refused before stepping on an incapable context.
- `CharacterController3D` mirrors `Controller`, `FootRadiusFraction` and `Water` beside its tuning fields, takes an
  optional field, registration and boundary, and gains a new leased execution branch. `Update` revalidates whenever
  the composed tuning, world, field, registration or boundary changed, before stepping.
- `CharacterMovement.Step` and `StepTowards`, called directly with a contact tuning, run the dry contact path, which
  has proven leased parity on land. A wet medium sample there throws `NotSupportedException` naming the field route,
  because a point delegate cannot certify water.

**P5A-9. Validation.** Three paths share one internal check:

- **Ordinary contact (heads).** `ShellGeometry.Validate` passes. `FootRadiusFraction` lies in (0, 1] and
  `FootRadiusFraction * CapsuleRadius >= 0.01 m`. The water mode is not `Legacy`. The kernel's requirements hold
  (positive `Gravity`, positive `MaxFallSpeed`, positive `SwimEnterDepthFraction`, as in
  `ExplicitCharacterMovement.Queries.cs:46-57`). The view offers `IPhysicsQueryLeaseSource`,
  `IPhysicsCapsuleContacts`, `IPhysicsSupportFootprint` and `IPhysicsCapsuleSweep`. A registration and a
  `MovementBoundary` are present. No medium delegate is given without a field.
- **Dry direct.** The shell and foot rules hold, and the world offers `IPhysicsQueryLeaseSource` and
  `IPhysicsSupportFootprint`. Clamp and medium delegates are allowed, with the wet refusal above.
- **Legacy.** `FootRadiusFraction == 0.5` and `Water` equals its default (mode `Legacy`, surface jump speed 0), so no
  contact-only setting is silently ignored.

Explicit configurations read `FootRadiusFraction` from the tuning and hash it in place of the fixed 0.5, so default
fingerprints are unchanged. Their own `WaterTraversalPolicy` governs. A tuning whose `Water` is neither the default nor
equal to that policy is refused. `Controller` is not consulted, because an explicit configuration always selects the
contact controller.

**P5A-10. Point water for pacing and approach.** A contact `GroundMoveContext` exposes `Medium` derived from
`SampleCentreWater`, so `MoveToRange` settling, `SwimPace` and `DirectMoveToRange` keep working. Motion always uses the
certified trace through the lease.

**P5A-11. No wire change.** Generation 14 stays. Ordinary contact heads replicate through the explicit fields every
lifecycle door already uses via P5A-7. Selection is not exchanged, because both heads build tuning from the same build
or published catalog under the consumer protocol version. Cost if wrong: a mismatched client is corrected every tick,
and the repair is a tuning fingerprint in the consumer token.

**P5A-12. Resolver-1 MapDoc contexts stay legacy-only.** `MapPhysicsRegistration` refuses a contact tuning with a
message naming native G1b. Cost if wrong: a later factory supplies their registration, field and boundary.

**P5A-13. Navigation waits for 5b.** `PhysicsNavBake.BuildProfile` and `GroundNavigationBake.Create` refuse a non-legacy
profile with `NotSupportedException` naming phase 5b. Legacy capture and every bake byte stay unchanged in 5a.

**P5A-14. Guard maintenance.** Legacy behaviour and the frozen v1 golden bytes stay unchanged. The
`NavBakeIdentityTests` field count splits into the 30 positional fields and the new properties, and the
`ExplicitMovementProfileTests` reflection theory takes the selectable foot fraction. Each change is listed in its task
report.

**P5A-15. Stale text.** The dry `CheckMedium` message, the `ShardedClimbSignalExportTests` comment, the controller
design's capability name (now `IPhysicsSupportFootprint`) and the `ExplicitMovementProfile` phase 5 comment are
corrected.

**P5A-16. Version.** 5a rides staged 20.34.0, already a minor. If 20.34.0 is tagged first, take the next free minor.

## Acceptance for 5a

- **A1. Legacy unchanged.** A default tuning is `Legacy`. Every legacy movement, bake, golden fingerprint and wire test
  passes, with only the P5A-14 guards changed.
- **A2. Selection matrix.** With a contact tuning, each of `PlayerMoveSimulator` (boundary, face yaw), `WorldServer`,
  `WorldClient` prediction replay, the sharded framed head, `GroundMoveContext` (`StepTowards` with precise direction),
  `CharacterController3D`, `NpcGroundMovement`, `MoveToRange` and `DirectMoveToRange` gives results bit-equal to the
  internal ordinary composition (`ExplicitCharacterMovement` over the ordinary environment at offset 0), on dry land
  and across a water crossing. On dry land those results also equal the dry contact path, including a triggered
  stair-signal row that proves the look-ahead was served. Replay equals the server.
- **A3. Configuration refusals.** Each P5A-9 refusal throws at the P5A-8 configuration point: a view without certified
  sweeps, a source that cannot create a view, zero gravity, fall speed or swim-enter fraction, a missing registration
  or boundary, a delegate without a field, two selections, Legacy water with a nonzero surface jump speed and a
  resolver-1 context. The supported direct dry API accepts a contact tuning with clamp and medium delegates.
- **A4. Ordinary water.** Each row runs with the Level field and with the TileWorld field, with expectations derived
  from geometry at offset 0:
  - SurfaceSwimmer entry, exit, buoyancy and hop.
  - The WadeOnly wall with shore slide, and DryOnly refusal.
  - Bed clipping and a shallow-to-deep down-seat.
  - A thin dry band crossing and a tied shore and solid corner.
  - A forbidden start, and recovery with lag payment.
- **A5. Environment binding.** Wrong world, wrong identity, a stale generation, a frame rebase and a nonzero physics Y
  origin are refused or converted correctly. Registration rows: a missing, an excluded and a stale registration refuse,
  split and joined pieces share an owner, reversed installation order gives identical owners, and identical handles in
  two cell worlds stay distinct. Analytic rows compare the full ordinary composition with the direct dry path: a
  purely vertical landing onto flat analytic ground from above `StepHeight` lands in one tick, an analytic barrier
  higher than `StepHeight` blocks as a terrain wall, ground below the served domain is Known with null and a declared
  extent that excludes the analytic range is refused at configuration. `EnumerateSupport` is Unresolved. A malformed field result refuses as the lease refuses today.
  View lifetime: two consumers sharing a caller-supplied view stay usable after one ordinary binding is disposed or
  reconfigured, and an engine-created view is released with its binding.
- **A6. TileWorld producer.** An independent exact oracle agrees across tile faces, edges and corners, adjacent bodies
  with different surfaces, equal surfaces in adjacent regions, diagonal paths through corners, a tangent corner,
  rounded-cap vertical contacts and dry tiles. A radius 0.1 capsule axis at (-0.09, -0.09) beside a water tile
  occupying x >= 0 and z >= 0 is not wet (corner distance about 0.1273). `TileWorldColliders` owners follow P5A-4.
- **A7. Served domain.** Each row advances under the ordinary composition with no held step or skipped signal:
  - A multi-command replay, a preparing commitment that launches inside a replay, and a triggered stair signal.
  - `WalkSpeed` above `RunSpeed`, an amplified directional scale and an amplified water pace.
  - An ungrounded body on a 60 degree analytic slope with 100 m/s contour carry and a run command along the contour
    (RunSpeed 100, Gravity 10, dt 0.1), matching the direct dry path.
  - A swimmer at centre Y 0 under a Level surface at Y 1000 with floor Y -2000, rising about 264 m in one tick.
  - A high airborne body over analytic terrain, a footprint at the domain edge and a centre exactly on a playable edge
    with its exterior queries covered.

  A body that leaves the served domain holds with the lease's existing outcome. `TileWorldColliders` declares an
  extent covering its terrain, statics and water, and a client island domain refuses queries outside the island.
- **A8. Lifecycle at offset 0.** On both server heads: spawn, teleport, restore, configured-spawn reset, correction
  replay, cross-cell transfer with destination admission and live continuation all place the body at h, including a
  row under a low roof and a row at the WadeOnly deep-line boundary.
- **A9. Navigation refusal.** A non-legacy profile is refused by the bake until 5b.
- **A10. Allocation and cost.** A warm ordinary contact tick allocates only the named lifetime tokens of the explicit
  tick inventory plus the read scope. One cold and one warm local sample are recorded, not asserted. No hosted run.

## Tasks for 5a

1. Selector contract and validation: P5A-1, P5A-9, the direct dry dispatch, P5A-13 and P5A-14. A1, A9 and the tuning
   half of A3.
2. Composition value: P5A-3 through tick, placement, settling and continuation, with explicit suites unchanged.
3. Ordinary environment: P5A-4 to P5A-6 with the served domain and the Level field. A4 on Level, A5 and A7 at
   environment level.
4. TileWorld producer and registration. A6 and A4 on tiles.
5. Network heads: P5A-7, P5A-8 and P5A-11 for `WorldServer`, `ShardedWorldServer` cells, `PlayerMoveSimulator`,
   `PlayerMovementSystem` and the `WorldClient` read API. A2 for those heads, A3 and A8.
6. Local heads: `GroundMoveContext`, P5A-10, `CharacterController3D` and P5A-12. The rest of A2 and A3.
7. Documentation, changelog, A10 sample and final verification.

## Phase 5b binding inputs

5b gets its own specification and independent review before it starts. Settled direction and open obligations:

- **Compatibility (from round 1 F4).** v1 stays readable. An unchanged all-legacy configuration writes byte-identical
  v1 under the same engine version, and other-engine files stay `EngineChanged`. Any non-legacy profile makes the bake
  v2, decoded format-first, with `ProfilesChanged` across formats.
- **Capture by the footprint rule (F5, F19, F20).** Contact profiles capture columns with a footprint column probe
  built on `FootSupport`, so captured heights are resting heights by construction and footprint-only support such as
  the SG-23 sphere flank seeds nodes. Layers are extracted once from those raw seeds and never re-layered (raw layer
  identity). Headroom comes from certified upward clearance of the resting body, keeping non-walkable ceilings.
  Legacy capture is unchanged.
- **Float nodes (F21).** Solid and float slots are discriminated before certification. Aquatic is allowed only with
  SurfaceSwimmer.
- **Explicit profiles (F6, F22, F23).** One normalized effective profile per explicit bake profile, with mismatched
  geometry or areas refused. v2 stores geometric support and resting feet. A bake-specific explicit traversal adapter
  applies capsule-area admission and the shared 3D 1 mm arrival predicate, correcting `ExplicitMotionProbe`'s per-axis
  grounded rule where that predicate belongs.
- **Runtime graph compatibility (F7, F24).** Graphs are validated against the effective movement binding, including
  controller, foot fraction, offset, water policy, `SwimEnterDepthFraction` for non-aquatic water graphs and explicit
  identity, for fresh and loaded graphs.
- **#1299 (F25).** Deep nodes are classified by the runtime's own full-body placement proof under the profile's water
  policy, never by a scalar formula alone. Unknown refuses.
- **Traversal.** `GroundTraversalProbe` keeps its hold, slices, budget and 1 mm arrival rule and steps the selected
  controller through the P5A-7 binding.

## Review response

Round 1 (REVISE, F1 to F13), round 2 (REVISE, F1 to F27), round 3 (REVISE, F1 to F31) and round 4 (REVISE, F29
partial only) reports are in the phase 5 ledger.

Round 5 (REVISE) accepted the motion bounds but found the scope-miss retry unsound in three ways: an analytic miss
carries no height to enlarge to (F32), four retries of the first escaping substep need not converge (F33), and
prediction and imports have no atomic attempt boundary (F34). The review loop's fifth round ended there, so the
controller adjudicated: all three are load-bearing, and the proper fix removes their cause. P5A-5 now gives every
ordinary lease one constant scope, the declared served domain, because an ordinary world is fully resident and scope
checks are constant-time. There is no per-tick planner, no retry and no attempt boundary, and a query outside the
domain is a genuine absence of data. A7 keeps every motion counterexample, including F33's 264 m buoyancy rise, as
rows that must advance. This also closes F29, F1 and F17.

Round 4 resolved F28, F30, F31 and F14, with no new findings. For the remaining F29 (and with it F1 and F17),
revision 5 adds the summed slide bound and the buoyancy target to the first estimate, and makes a planner miss
non-fatal: P5A-5's scope-miss retry reruns a discarded step under an enlarged scope, which is exact because results
are scope-invariant. A7 adds the slide and deep buoyancy counterexamples and a scope-invariance row.

Round 3 resolved F15, F16, F18, F26 and F27 and confirmed every navigation finding carried. Revision 4 answers the
rest:

- F28 and F1: analytic facts are scoped to the witnessed region (P5A-4), with landing and barrier rows in A5.
- F29 and F17: P5A-5 bounds every drive, takes the replay batch and uses the footprint bound calculation. A7 adds
  the rows and A2 the signal row.
- F30: P5A-4 separates the served domain from the playable boundary and never clips. A7 pins an edge centre.
- F31 and F14: P5A-4 records view ownership and borrows caller views. A5 adds the lifetime rows.

Round 2 dispositions follow.

- Round 2 resolved F2, F3, F4, F8, F9, F10, F11, F12 and F13.
- F1 and F18: P5A-4 adds the registration, scoped analytic facts, truthful enumeration refusal, view ownership and
  the generation, and A5 tests them.
- F14: P5A-9 separates the three validation paths and adds certified sweeps and kernel requirements. A3 covers them.
- F15: P5A-3 carries the offset through every placement door, and A8 tests them at offset 0.
- F16: P5A-7 replaces public-configuration branches with one effective binding per head and cell, and A8 covers the
  lifecycle doors.
- F17: P5A-5 defines the scope planner and the replay horizon, and A7 tests it.
- F26: P5A-9 requires the whole default `Water` for Legacy.
- F27: Current state is corrected, and `CharacterController3D`'s new leased branch is assigned to task 6.
- F5, F6, F7 and F19 to F25 are navigation findings. Each is carried into the 5b binding inputs above with its
  settled direction, and 5b's specification answers it in full before its review.
