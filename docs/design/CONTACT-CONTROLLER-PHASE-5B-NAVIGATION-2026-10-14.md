# Contact controller phase 5b: navigation

Design for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5b and the navigation half of
[#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Source baseline: `ee6016aae` on `main`, released as v20.37.0
with phase 5a. The binding inputs are the [phase 5 design](CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md#phase-5b-binding-inputs),
as amended by ruling P5B-1 below. Written under the owner's instruction to continue the program to completion, so every
choice below is a recorded ruling for the owner's review. Revision 6 answers review rounds 1 to 5.

Status: phase 5b ground navigation is released in 20.39.0. Contact aquatic navigation, footprint-only
seeds and public explicit bake profiles are deferred to [#1389](https://github.com/APKiwiOrg/KhaozEngine/issues/1389).
The [cost record](../verification/2026-10-14-contact-controller-phase5b-cost.md) records the local evidence.

Program row exit: legacy bakes still load, a new-controller bake round-trips, #1299 closes.

## Scope

In scope: ground navigation profiles on the ordinary contact controller. Capture, layering and footprint facts stay the
legacy ones. Every node and edge is proved at its contact resting anchor through the profile's own effective movement
binding, and the anchor is the node's physical pose for every runtime consumer. The phase also covers KENB format 2, validation on every door, runtime graph compatibility, the #1299
navigation acceptance, the #1270 resting route on the engine side and one independent placement fix.

Out of scope:

- Non-legacy aquatic profiles, footprint-only navigation seeds, public explicit bake profiles and the legacy swim
  arrival defect #1388. They move to phase 5c, [#1389](https://github.com/APKiwiOrg/KhaozEngine/issues/1389), under
  ruling P5B-11.
- Legacy capture, legacy traversal and legacy graph behaviour, which stay unchanged.
- KENB decode allocation amplification, which exists in format 1 today and is filed as #1390.
- Native MapDoc movement and `MapNavTiling` vertical layers (World Authoring G1b), #1355 certified sweep shapes, #1344
  small bodies, the SG-12 budget, any hosted or local load run, engine release, tag, game pin, Grimhollow edits and
  world edits.

## Owner constraints

- Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity. Never widen a tolerance.
- Do not restart completed proofs.
- A known proper fix is implemented rather than a workaround shipped, even outside a scope (owner ruling, 2026-10-08).

## Current state

Facts verified against `ee6016aae`:

1. Legacy capture keeps standable axis hits with axis headroom. `CandidateColumns` drops surfaces whose headroom is below
   the body height and keeps a surface only when its Hold proof at the raw feet passes. `NavLayerBaker` layers the raw
   heights, builds grids and generates Stair candidates from them. `NavAreaFootprint` reads the captured columns.
2. A contact body rests on the highest certified footprint support within its reach, which can sit above the raw axis
   hit. At the #1270 low-lip bank the legacy Hold at the raw point moves the body by about 15 mm, so the node fails the
   1 mm rule. The owner-approved low-lip design answered this with bake-local resting anchors: raw heights, layers,
   headroom and areas unchanged, anchors used only inside proofs, and an anchor that stays in its raw surface's
   ownership band and fits its captured headroom.
3. Ground edges step `DryContext`, which is legacy constructed. `GroundMoveContext.StepContact` drops the movement
   outcome. Probe tuning is `tuning with { WalkSpeed = 1, RunSpeed = 1, AirMomentum = false }`, while
   `EffectiveMovementBinding` tuning is immutable and `EffectiveMovementBinding.Ordinary` builds a new environment on
   every call.
4. `SettlePlacement` runs `ValidatePlacement`, including water admission, at the supplied pose, then seats.
   `TrySeat`'s motion route also runs water admission. `ContactPlacement.Find`, the certified support search placement
   uses, runs none.
5. `SettlePlacement` publishes a seated position before it decides footing, so a swimmer at its float line can be
   seated on a bed that grants no footing and then rise by more than 1 mm on its next Hold. `ReclassifyContinuation`
   never seats.
6. `ValidateProfileController` refuses Contact at `Create` and `BuildProfile`. `Load` does not, and `Controller`,
   `FootRadiusFraction` and `Water` are outside the v1 identity, so a contact expectation loads a legacy file.
7. `GroundNavigation.ValidateTuning` is internal and compares geometry and aquatic swim fractions only. A public
   explicit binding uses the Legacy selector with its water policy supplied separately, so no tuning-only check can
   tell it from Legacy.
8. `IPhysicsQueryLeaseSource.AcquireQueryReadLease` returns an `IPhysicsQueryLease` whose `GeometryGeneration` reports
   the static geometry generation. `PhysicsNavBake` records only the origin.
9. The only v1 byte pin is the identity fingerprint golden. No full-file golden exists.
10. Eleven production call sites read `NavGrid.SurfaceHeightAt`. Pose readers: `GroundNavigation.Resolve`,
    `RouteStraightener.TryFeet`, `PhysicsNavBake.Feet`, `GridPathPlanner.CellFeet` and `NavSpace.LayerAt`, which the
    path follower uses. Raw identity readers: `PhysicsNavBake.IsFloatNode`, `GroundNavigationBake.WriteProfile`,
    `NavLayerLinks.GenerateCore` and `NavHopLinks.Generate`. `StepMask`, layer extraction and layer bands read the raw
    sample arrays directly, and `NavAreaFootprint` reads captured columns.

## Rulings

### P5B-1 Resting anchors instead of footprint capture

The phase 5a binding input "capture by the footprint rule" is superseded. Contact profiles keep the legacy raw capture,
layering and footprint facts, prove every node and edge at its contact resting anchor, and publish the anchor as the
node's physical pose (P5B-13). Raw heights stay the topology identity.

**Why:**

- The owner constraint names raw layer identity, which is the low-lip resting design's rule.
- Three review rounds showed that footprint capture needs complete certified discovery of every resting support,
  certified headroom per discovered support, a new facts format, a new footprint predicate and a layering stage that
  does not reinterpret raw Stair candidates. Each was a new route to a wrong-area or wrong-layer admission.
- Anchors retain captured axis headroom, surface reach, area and layer protections and give #1270 its resting proof.
  Contact placement and swept shells decide clearance across the footprint, including beside low slab risers.

**Cost if wrong:** footprint-only supports with no axis hit (the SG-23 flank) get no node. Missing nodes are
conservative, and #1389 carries the seeds.

### P5B-2 Effective bake profiles and doors

A `NavBakeProfile` resolves to **Legacy** when `Tuning.Controller` is Legacy, or **OrdinaryContact** when it is Contact.
Legacy behaviour, identity and bytes are unchanged. OrdinaryContact runs `EffectiveMovementBinding.Preflight` for its
kind.

The constructor keeps only selection-independent checks. Complete normalization runs at `BuildProfile`, `Create` and
every `Load` expectation, before any stream read:

- A well-formed selection this phase does not support (OrdinaryContact with `Aquatic`) throws `NotSupportedException`
  naming #1389.
- A malformed configuration throws `ArgumentException`, as today.

`ValidateProfileController` and its "phase 5b" refusal are removed.

### P5B-3 Bake bindings, basis and leases

- A contact profile needs a contact-constructed `GroundMoveContext` (registration, boundary and water field). A legacy
  constructed context refuses it with `NotSupportedException`.
- A new internal Locomotion factory derives a **proof binding** from the profile's source binding. It shares the source
  binding's ordinary provider, environment context, selected view and composition, and changes only `WalkSpeed`,
  `RunSpeed` and `AirMomentum` to the legacy probe values. Those fields are outside the environment identity and the
  served domain, so sharing is sound. The bake owns both bindings for one `BuildProfile` call and disposes them at its
  end. They are separate from the binding `GroundMoveContext` caches for its own steps, and a borrowed view is never
  disposed by the bake.
- When the physics world implements `IPhysicsQueryLeaseSource`, `Capture` acquires a read lease before sampling and
  records its basis: the source, the lease's `GeometryGeneration` and the origin, then disposes that lease before
  sampling, because leases do not nest. After sampling and every classifier call it acquires a second lease and throws
  if the generation or origin changed, so a classifier that mutates geometry
  is refused. A contact `BuildProfile` refuses with `InvalidOperationException` when its own lease basis differs from
  the recorded one, or when the capture recorded no basis because the world lacks the capability. Legacy capture and
  legacy profiles on such worlds are unchanged.
- `BuildProfile` preflights, with outward bounds, the full envelope its queries need (capture bounds, the body at the
  highest and lowest captured surfaces, the support search reach and footprint bounds) against the ordinary served
  scope and refuses an envelope the scope does not contain.
- Every query and proof acquires its own lease from the proof binding's scope in the context's local frame. A lease
  whose basis (source, geometry generation, environment generation and frame epoch) differs from the recorded basis
  throws. Any other non-Known acquisition throws `InvalidOperationException` naming the cell. A bake publishes no
  partial graph.
- With `SampleWater` set, shared capture reads the context's medium delegate. A contact-constructed context with a water
  field but no medium delegate refuses `SampleWater` at `Capture` with a message naming the missing delegate.

### P5B-4 Contact candidate admission

For a contact profile, `CandidateColumns` keeps its headroom filter and replaces the raw Hold proof with this anchor
proof. A raw surface becomes a candidate only when all of these hold:

1. **Anchor.** A new internal Locomotion query rebuilds the selection, builds the placement queries and runs
   `ContactPlacement.Find` from a grounded body with feet at the raw height, with the reach `SettlePlacement` uses for a
   grounded body. It decides no water or footing and runs no motion route. A Walkable result gives the anchor: the
   composition's resting feet for that support (offset 0 for ordinary). Steep, None, Refused or a non-Known
   availability admits nothing. The anchor must lie inside the search band, from raw height minus `GroundedEpsilon` to
   raw height plus `StepHeight` inclusive. A certified midpoint published outside that band admits nothing, so the
   stored value and the file check in P5B-9 always agree.
2. **Ownership.** The anchor lies below the next raw surface above in the column and the raised body fits the captured
   headroom: anchor plus body height at most raw height plus headroom. A deck or rooftop never becomes another surface's
   anchor.
3. **Footprints.** The footprint predicate accepts captured surface reach and areas at both raw feet and anchor.
   Contact clearance comes from placement and swept-shell proofs. A neighbouring column can capture ground beneath
   a low slab, whose low headroom does not describe clearance for the body standing on the slab. Legacy profiles
   retain the captured headroom check across their footprints. The candidate's own axis headroom and ownership band
   remain required.
4. **Placement.** `SettlePlacement` at the anchor returns `Advanced`, grounded with footing, within the unchanged 3D 1 mm
   rule of the anchor. This is where the full shell clearance, the water policy and Unknown water are decided, at the
   resting pose rather than at the raw point.
5. **Hold.** One stationary step through the proof binding returns `Advanced`, grounded, within 3D 1 mm (P5B-7).

The candidate keeps its raw height. Layering, grids, Stair candidates, layer bands and footprint facts are exactly the
legacy ones built from raw heights, so nothing is re-layered. The admitted anchor is recorded on the node as its resting
height (P5B-13), and every proof reads that recorded value.

A placement that seats away from its anchor, or a settled pose that moves by more than 1 mm on its next Hold, refuses
the candidate. Where that happens on a reachable fixture it is a placement defect, fixed in the placement code, never
by repeating settlement or widening the Hold.

### P5B-5 Water policy and #1299

Contact navigation in this phase is ground only. `SettlePlacement` at the anchor decides footing under the profile's
water policy:

- WadeOnly refuses footing past the deep line, so deep cells have no node and routes plan around them.
- DryOnly refuses footing in water.
- SurfaceSwimmer refuses footing where a grounded body would start to swim. The ground node in the swim hysteresis band
  stays, because a grounded body keeps footing there.
- A water answer other than Known refuses the candidate.

No scalar depth formula decides a contact node. A submerged lip whose anchor is admissible keeps its node even where
the raw ground point is past the line, because water is judged at the anchor. Legacy aquatic derivation is unchanged.

### P5B-6 Settlement seats only with footing

`SettlePlacement` seats a non-rising body only when the seated pose grants footing under the water policy. When initial
placement validation succeeded and the provisional seat and its water decision are Known but footing is denied, it
keeps the supplied position and vertical velocity, classifies water there and returns `Advanced`. It publishes neither
the provisional seat nor its downward-velocity reset. A swimmer at its float line therefore stays at its float line.
Initial shell, boundary, water-policy, Unknown, stale and owner-binding failures keep their existing non-Advanced
outcomes, so the existing forbidden-initial-pose controls, including the three-door DryOnly row, are unchanged.

Door matrix:

- Spawn, cold restore and teleport go through `SettlePlacement` and take this rule.
- Live transfer, continuation and sharded handoff go through `ReclassifyContinuation`, which never seats. Its pose,
  timer and buffered-press contract is unchanged.

The intentional behaviour change is placement of a body denied footing, such as a swimmer carrying its history, above
a bed within seating reach: it keeps its supplied position rather than being moved onto the bed. Doors that clear
swimming history (spawn and teleport) keep the existing fresh classification and its hysteresis.

### P5B-7 Traversal over movement results

Contact proofs step the proof binding with `StepTowards` and read every `MovementStepResult`.

- Each edge starts from the canonical rest state at its source anchor: grounded, zero velocities, the anchor's feet. The
  source node's Hold certifies that state is at rest. Legacy edges use the same contract at the raw point.
- The Hold is the first slice of the edge budget. The bake options own timing and budget.
- Any outcome other than `Advanced` at the Hold or at any slice refuses the edge. An unchanged pose is never evidence
  that a query succeeded. No prefix of a refused proof is published.
- Every slice passes the footprint's surface reach and area predicate and stays grounded.
- Arrival is the shared 3D 1 mm sphere around the target anchor with the body grounded.
- Edges step the wet proof binding, so the WadeOnly wall and the swim threshold shape them as at runtime.
- Stair links use the same proof between anchors.

Legacy proofs are unchanged.

### P5B-8 Explicit grounded arrival

`ExplicitMotionProbe` takes the shared 3D 1 mm sphere for grounded arrival at both its Hold and its destination. Its
swimming rule keeps the surface-contact tolerance. This only refuses arrivals the old per-axis rule accepted outside the
sphere. The explicit route proof recipe version moves from 1 to 2, so explicit fingerprints change once, and the pinned
test moves with it.

### P5B-9 KENB format 2

An all-legacy bake writes format 1, byte for byte as today, pinned by a full-file golden for a small all-legacy fixture
under the golden engine string. Any contact profile makes the bake format 2.

| Stored file | Expected profiles | Same engine | Other engine |
| --- | --- | --- | --- |
| Format 1 | All legacy | Existing comparison, Loaded if equal | EngineChanged |
| Format 1 | Any contact | ProfilesChanged | EngineChanged |
| Format 2 | All legacy | ProfilesChanged | EngineChanged |
| Format 2 | Any contact | Format 2 comparison, Loaded if equal | EngineChanged |

Decoding is format directed. Versions other than 1 and 2 and nonzero flags stay `UnsupportedFormat`. A malformed
identity in a supported format stays `Corrupt`. Expectation refusals follow P5B-2 and happen before the stream is read.
Inside one format the order stays engine, options, sources, profiles.

The format 2 identity is the format 1 identity with these fields added to each profile, after its tuning block:

| Field | Width | Legal values |
| --- | --- | --- |
| Effective kind | u8 | 0 Legacy, 1 OrdinaryContact |
| Foot radius fraction | f32 bits | 0.5 for Legacy, finite in (0, 1] for OrdinaryContact |
| Water mode | u8 | 0 for Legacy, 1 to 3 for OrdinaryContact |
| Surface jump speed | f32 bits | 0 for Legacy, finite and valid for OrdinaryContact |
| Placement offset | f32 bits | 0 for both kinds |

`WaterTraversalPolicy` stores only its mode and surface jump speed. Its other members are engine constants that the
engine version covers. Any other combination is `Corrupt`.

The format 2 payload is the format 1 layout, with one addition for each contact profile. Each of its layers is written
as the layer header, blocked mask, raw open heights and open exit bytes, exactly as format 1 writes them, followed by one
f32 resting height per open cell in the same Z then X clear-bit order. The count is the already decoded open-cell count,
so no new length field exists. A resting height must be finite and inside the P5B-4 search band of its raw height
(`GroundedEpsilon` below to `StepHeight` above, inclusive), else the file is `Corrupt`. Raw heights, legacy layers,
Stair candidate counts and acceptance bits are stored as format 1 stores them.

Bounds: the format 1 payload bound is unchanged. Format 2 adds `4 * C * Lmax * Nc` to it, where `C` is the cell count,
`Lmax` the layer cap and `Nc` the number of contact profiles, with the existing saturating arithmetic and `Array.MaxLength`
applied after the total. Legacy profiles in a mixed bake add nothing. Before reading a contact layer's cell payload the
reader requires `9 * open` remaining bytes, where a legacy layer requires `5 * open`. A loaded contact graph is rebuilt
from its stored layers, resting heights and the shared columns, never recaptured, and rewriting it reproduces its
bytes.

### P5B-10 Runtime graph compatibility

`GroundNavigation` carries the effective identity of the profile that built it. `BuildProfile` and both decoders
populate it, and format 1 graphs carry Legacy.

- A new public `GroundNavigation.Validate(EffectiveMovementBinding binding)` is the authoritative door. It first
  requires the binding's kind to equal the graph's. A PublicExplicit binding throws `NotSupportedException` naming
  #1389 against every graph. Legacy graphs then keep today's comparisons. Contact graphs compare every field their
  format 2 identity encodes: the 27 retained tuning fields, controller, foot fraction, placement offset, water mode and
  surface jump speed, read from the binding. A mismatch throws `ArgumentException` naming the differing input.
- A public `Validate(in MoveTuning tuning)` convenience overload resolves the tuning to Legacy or OrdinaryContact and
  applies the same checks. Its documentation says it cannot see a public explicit binding.
- `MoveToRange` validates against its context's resolved binding before planning and before prediction, replacing the
  internal call.
- Callers that use `Planner` or `AllowsSegment` directly own the call, and the public documentation names the binding
  door.
- `DirectMoveToRange` owns no graph and is unchanged.

### P5B-11 Deferred work and known defects

- **Non-legacy aquatic navigation** needs node identity with a ground or float mode independent of height, because a
  ground node and a float node can coincide exactly in the swim hysteresis band (half height 0.75, surface 0,
  submersion 0.6, enter 0.65 and exit 0.55 put both feet at -0.9). Its consumers are creatures that #1344 gates. It moves
  to #1389 with column water discovery.
- **#1388**, the legacy swim proof that ignores the target's mode. A plain target-mode check refuses every swim to
  ground edge ending in the hysteresis band. The legacy aquatic view makes a float node only where depth reaches the
  enter fraction and a ground node elsewhere, and edges join only neighbouring cells. So where a bank's depth crosses
  the band over more than one cell, no float node neighbours a ground node a swimmer can ground on, and the bank exit
  disconnects. The proper fix is float presence in band cells, which is the mode-aware representation above. It moves
  to #1389. 5b adds nothing that relies on the current acceptance.
- **Footprint-only navigation seeds** move to #1389 under P5B-1.
- **Public explicit bake profiles** need ordinary-backed explicit environments, a normalized proof binding and a public
  explicit follower. Their consumers also wait on #1344, so they move to #1389.

None of these ships a workaround. 5b adds no swim, float, footprint-seed or explicit navigation path, and refuses those
selections at every door with a message naming #1389. The swimmer settlement defect is independent of all of them and
is fixed here under P5B-6.

### P5B-12 Version, placement, determinism and test dispositions

The phase cuts 20.39.0, a minor version for the new public navigation API and KENB format 2, because v20.37.0 (phase
5a) and v20.38.0 (rigid attachment sets) were both released while it ran. Navigation types stay in
`KhaozEngine.Movement` beside the bake code. The proof binding factory, the anchor query and the settlement rule live in
`KhaozEngine.Locomotion` beside the code they extend. `KhaozEngine.Navigation` stays free of Locomotion.

Each anchor, candidate and edge is a function of the recorded basis and the immutable capture, so results do not depend
on iteration order. A later partitioned parallel build changes no byte.

Intentional test dispositions:

- `ContactNavigationRefusalTests` rows that require the "phase 5b" refusal become capability and configuration
  refusals (legacy-constructed context, contact Aquatic), keeping their validation-order controls, plus valid contact
  construction rows.
- The `GroundNavigationBakeRefusalTests` row that declares version 2 unsupported moves to version 3.
- The explicit profile fingerprint pin moves with the recipe version.
- Placement tests that pinned a swimmer being seated onto a bed without footing move to the P5B-6 behaviour, named in
  the task report.

Every other legacy behavioural, round-trip and byte-identity test passes unchanged.

### P5B-13 Physical node pose

`NavGrid` gains optional per-cell resting heights, plain floats with no Locomotion dependency. `RestingHeightAt(x, z)`
returns the resting height where one is set and the surface height otherwise, so legacy grids behave and serialize
exactly as today.

- Pose readers move to `RestingHeightAt`: `GroundNavigation.Resolve`, `RouteStraightener.TryFeet`,
  `PhysicsNavBake.Feet` (proof endpoints), `GridPathPlanner.CellFeet` (goal membership and search progress) and
  `NavSpace.LayerAt` (used by the planners and the path follower).
- Raw identity readers keep `SurfaceHeightAt`: `PhysicsNavBake.IsFloatNode`, `GroundNavigationBake.WriteProfile`,
  `NavLayerLinks.GenerateCore` and `NavHopLinks.Generate`. `StepMask`, layer extraction and bands keep their raw sample
  arrays, `NavSpace.LayerOf` keeps its raw band fallback and `NavAreaFootprint` keeps the captured columns.
- The hop endpoint documentation in `PathFollower` names `RestingHeightAt` for physical Y.
- A test pins the classification per call site, so a new caller has to choose.

Fresh contact graphs set resting heights from admission. Loaded ones read them from format 2. Both therefore hand region
predicates, `Resolve` and the follower the pose the body actually holds.

## Acceptance

Compatibility:

- The all-legacy full-file golden matches.
- Every cell of the P5B-9 matrix, a combined engine and profile mismatch, and identity corruption rows (unknown kind,
  Legacy with non-default foot fraction or water, OrdinaryContact with water mode 0, nonzero offset) return their named
  status.
- A contact profile round-trips. Write, load and rewrite are byte-identical, and the loaded graph and its footprint
  admission equal the fresh ones.
- A mixed legacy aquatic and contact bake with `SampleWater` round-trips. A water-field-only context with `SampleWater`
  refuses at `Capture`.
- Contact Aquatic throws `NotSupportedException` naming #1389 at `BuildProfile`, `Create` and `Load`, and `Load` leaves
  the stream unread.
- A geometry change between `Capture` and `BuildProfile`, and a classifier that mutates geometry, each refuse.

Admission:

- The #1270 low-lip bank: the raw point fails the legacy Hold, its anchor passes the contact Hold at the unchanged 1 mm
  rule, and the bank route is Complete between anchors.
- A deck over ground and a rooftop above a bank never become each other's anchor. An anchor whose raised body does not
  fit the captured headroom is refused, and so is a low roof caught only by the shell.
- A stacked lift (a second support within reach above the anchor) refuses the candidate rather than iterating.
- A placement that pushes the anchor sideways by more than 1 mm refuses the candidate.
- A Hold refusal after a successful placement, a late support refusal on a slice and an out-of-scope envelope each
  refuse without publishing a prefix.

Water and #1299:

- A WadeOnly contact bake over a pool with a deep middle marks the deep cells unwalkable and a route plans around them,
  matching where the runtime walker stops.
- A submerged lip whose anchor is admissible keeps its node beside a raw ground point past the line, and the inverse
  control whose anchor is past the line has none.
- Just below, at and just above the enter fraction, plus one row inside the hysteresis band, for SurfaceSwimmer and
  WadeOnly profiles.
- A DryOnly bake has no node in water.
- Legacy bakes over the same pool, with and without `Aquatic`, are byte-identical to today's.

Placement fix:

- A body carrying swimming history, settled at its float line over a bed within seating reach that grants it no
  footing, keeps its pose and holds within 1 mm. Spawn and teleport clear swimming history by design, so a cold body
  there is classified fresh and wades on a bed shallower than the enter fraction, as today. Transfer and continuation
  rows keep their no-seat assertions.

Physical pose:

- A region goal restricted to the low-lip cell that accepts feet within 1 mm of the anchor finds the node, and the
  inverse goal at the raw height does not, for fresh and loaded graphs.
- `Resolve` picks the node's own layer for a body resting at its anchor, at the edge of the anchor search band.
- Contact `MoveToRange` traverses and stops across the low-lip waypoints in both directions.
- Legacy grids report resting height equal to surface height, and every legacy byte stays the same.
- A fully open sampled-water contact payload larger than the format 1 bound round-trips. Mixed legacy and contact profile
  order, a missing resting byte, a trailing byte and a resting height just outside the search band are `Corrupt` or
  refused as stated. Anchors exactly at the lower and upper band ends round-trip.

Traversal and runtime:

- Contact edges step the proof binding. An edge whose Hold or slice returns a non-Advanced outcome is refused, and an
  edge into a WadeOnly deep cell stops at the wall and is refused.
- A source profile with zero or differing walk and run speeds proves the same graph as one with unit speeds.
- Public `ProbeExplicitMotion` accepts at the 1 mm sphere boundary and refuses the old corner case (0.8 mm in X and
  0.8 mm in Y), at the Hold and at the destination.
- `MoveToRange` with a mismatched binding throws before planning, for fresh and loaded graphs, in both directions.
  `Validate` refuses each P5B-10 mismatch and a PublicExplicit binding whose tuning would pass the Legacy checks.

Cost record, from single local measurements only (no load or repeat run):

- calls and time per raw surface for the anchor, placement and Hold, and per edge proof,
- refused candidates by reason and peak resident arrays per profile,
- a conservative static upper bound beside a Hollowmere-scale extrapolation, for the owner's phase 6 decision.

## Tasks

1. Effective profiles, door validation, capture basis, runtime compatibility and the pose and topology split of
   `NavGrid` heights (P5B-2, P5B-3, P5B-10, P5B-13).
2. Locomotion seams: the proof binding factory, the anchor query, the settlement rule and the explicit arrival
   correction (P5B-3, P5B-4, P5B-6, P5B-8).
3. Contact candidate admission and water policy (P5B-4, P5B-5).
4. Contact traversal over movement results (P5B-7).
5. KENB format 2, resting heights and the compatibility matrix (P5B-9).
6. Cost record, documentation, changelog and full verification (P5B-12).

## Review response

Rounds 1 to 3 (`review-1.md`, `review-2.md`, `review-3.md`, all Not ready):

- F1, F4, F13, G1, G4, G5, G10, G12, H2, H3, H4, H6, U2: P5B-1 drops footprint capture for raw capture plus resting
  anchors, so discovery completeness, new facts, new headroom semantics and height substitution are no longer part of
  this phase. Captured axis headroom, surface reach, area and layer protections remain, with Contact clearance
  proved at physical resting poses and along swept shell routes.
- F2, H1: the anchor comes from `ContactPlacement.Find` with no water or motion route, and water is judged by
  `SettlePlacement` at the anchor (P5B-4, P5B-5).
- F3, H5 (settlement): fixed in 5b with a door matrix (P5B-6).
- G3, H5 (#1388): derivation of why a plain fix disconnects bank exits, moved to #1389 with the representation it needs
  (P5B-11).
- F5, G6, G11, H8: one internal proof binding factory sharing the source environment, with lifetimes and preflight
  (P5B-3). Explicit bake profiles move to #1389.
- F6, F7, F8, G2, G8, G9: aquatic and mode-aware work moves to #1389. Ground hysteresis rows stay (P5B-5, P5B-7).
- F9: proofs over `MovementStepResult` (P5B-7).
- F10, F16: explicit arrival correction and recipe version (P5B-8).
- F11, G13, H10: binding-taking public `Validate` with kind first, PublicExplicit refused (P5B-10).
- F12: load matrix (P5B-9).
- F14, G10, H7: capture basis recorded and checked, classifier mutation refused (P5B-3).
- F15, H11: door-time normalization with distinct exception types before stream reads (P5B-2).
- F17: test dispositions (P5B-12).
- F18: cost record (Acceptance).
- H9: format 2 adds no payload section. The existing format 1 decode amplification is filed as #1390.
- U1, U3: placement defects are fixed in placement, and sideways pushes refuse (P5B-4).

Round 4 (`review-4.md`, Not ready, 0 Critical, 1 Important, 3 Minor, every earlier finding resolved):

- J1: anchors become the node's physical pose for runtime consumers, stored in format 2 (P5B-13, P5B-9). This is the
  format change the low-lip design said must come back for review, recorded here as a ruling.
- J2: capture basis acquired from a read lease before and after sampling (P5B-3).
- J3: the settlement fallback applies only after successful validation and Known seat and water decisions (P5B-6).
- J4: #1389 no longer lists the settlement fix.

Round 5 (`review-5.md`, Not ready, 0 Critical, 1 Important, 1 Minor, J1 to J4 resolved):

- K1: format 2 bound and per-layer preflight count the resting bytes (P5B-9).
- K2: full call-site inventory, including `PhysicsNavBake.Feet`, `IsFloatNode` and the writer (P5B-13).
- Unverified items: the anchor band is inclusive and an out-of-band midpoint admits nothing (P5B-4), and the capture
  lease is disposed before sampling (P5B-3).

Both round 5 changes apply the reviewer's recommended text. The controller rules the design ready for planning at
revision 6 without a further design round, because no ruling changed.
