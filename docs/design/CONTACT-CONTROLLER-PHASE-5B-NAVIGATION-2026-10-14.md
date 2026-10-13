# Contact controller phase 5b: navigation

Design for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5b and the navigation half of
[#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Source baseline: `ee6016aae` on `main`, staged 20.37.0
with phase 5a. The binding inputs are the [phase 5 design](CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md#phase-5b-binding-inputs).
Written under the owner's instruction to continue the program to completion, so every choice below is a recorded
ruling for the owner's review. Revision 2 answers review round 1 (F1 to F18, U1, U2).

Program row exit: legacy bakes still load, a new-controller bake round-trips, #1299 closes.

## Scope

In scope:

- Navigation profiles on the ordinary contact controller and on public explicit profiles, built through the
  profile's own effective movement binding.
- Footprint column enumeration, contact node admission, contact water classification and contact traversal proofs.
- The Locomotion placement and binding extensions those need.
- KENB format 2, the v1 compatibility contract and validation of every profile on every door.
- Runtime graph compatibility against the effective binding for fresh and loaded graphs.
- The #1299 navigation acceptance.

Out of scope: legacy capture, legacy traversal and legacy graph behaviour (all unchanged), native MapDoc movement and
`MapNavTiling` vertical layers (World Authoring G1b), #1355 certified sweep shapes, #1344 small bodies, the SG-12
budget, any hosted or local load run, engine release, tag, game pin, Grimhollow edits and world edits.

## Owner constraints

- Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity. Never widen a tolerance.
- Do not restart completed proofs.
- A known proper fix is implemented even when it falls outside this scope (owner ruling, 2026-10-08).

## Current state

The facts that drive the rulings, verified against `ee6016aae`:

1. `PhysicsNavBake.Capture` takes no tuning. It casts `PhysicsColumnProbe` axis rays and keeps standable hits as
   surfaces. Inside-solid descent samples and undersides only shape headroom. A support that only the foot disc
   touches, such as the SG-23 sphere at X 0.35 with foot radius 0.2, has no axis hit at all.
2. `BuildProfile` keeps a surface only when its own Hold proof passes. Ground edges step `DryContext`, which is always
   legacy constructed. `GroundMoveContext.StepContact` returns the held input on acquisition failure and drops the
   movement outcome, so a MoveState-only proof cannot see an unresolved step.
3. `ValidateProfileController` refuses Contact profiles at `Create` and `BuildProfile`. `Load` runs only `CheckTuning`
   and the slope check, and `Controller`, `FootRadiusFraction` and `Water` are outside the v1 identity, so a contact
   expectation loads a legacy file.
4. `GroundNavigation.ValidateTuning` is internal, takes only `MoveTuning` and compares geometry plus the aquatic swim
   fractions. `MoveToRange.Tick` is its only caller.
5. `SettlePlacement` runs `ValidatePlacement` (including the water placement refusal) at the supplied pose before it
   seats, and seats any non-rising body, a swimmer included, before deciding footing. A swimmer at its float line can
   be seated on a bed that grants no footing and then rise by more than 1 mm on its next Hold.
6. `PublicExplicit` bindings hold a `MovementEnvironmentContext` and no ordinary provider, so `CreateOrdinaryScope` and
   `CaptureOrdinaryBoundary` throw for them. `OrdinaryMovementEnvironment` is a public `IMovementEnvironmentProvider`.
7. The water field answers membership at a body's feet and certified body sweeps. It has no column surface query.
8. `SwimTraversalProbe.Arrived` picks its rule from the stepped body's mode rather than the target's. That legacy
   defect is filed as #1388 and stays out of 5b, because 5b guarantees unchanged legacy bytes.
9. `ExplicitMotionProbe` proves grounded arrival with a 1 mm XZ circle plus a separate 1 mm vertical bound, and takes
   its budget and timing from the explicit profile.
10. With `SampleWater` set, `PhysicsNavBake.Capture` reads `context.Medium`. On a contact-constructed context with a
    water field and no medium delegate that adapter needs validated tuning, so shared capture fails before any profile
    is built. The builder does not keep the area classifier.
11. The only v1 byte pin is the identity fingerprint golden. No full-file golden exists.

## Rulings

### P5B-1 Effective bake profiles

`NavBakeProfile` resolves to one effective bake profile of the three `EffectiveMovementBindingKind` kinds:

- **Legacy** when `Tuning.Controller` is Legacy and no explicit profile is attached. Behaviour, identity and bytes are
  unchanged.
- **OrdinaryContact** when `Tuning.Controller` is Contact. `EffectiveMovementBinding.Preflight` runs for that kind.
- **PublicExplicit** when an `ExplicitMovementProfile` is attached through a new init property `NavBakeProfile.Explicit`.
  The bake profile's `Tuning` and `Areas` must equal the explicit profile's exactly, and the explicit profile's step
  seconds and step budget must equal the bake options' `EdgeProbeSeconds` and `MaxEdgeProbeSteps`. The explicit
  profile is the single owner of every movement field. The bake options own nothing a mismatch could hide.

`Aquatic` on a non-legacy profile requires the SurfaceSwimmer water mode.

The constructor keeps only selection-independent checks. Complete normalization runs at `BuildProfile`, `Create` and
every `Load` expectation, so initializer order and `with` copies are judged where they are used. No door admits a
profile another door refuses. `ValidateProfileController` and its "phase 5b" refusal are removed.

### P5B-2 Bake bindings and acquisition

A non-legacy profile is captured and proved through a binding over the bake context's ordinary environment.

- The bake context must be contact constructed (registration, boundary, water field). A legacy-constructed context
  refuses a non-legacy profile with `NotSupportedException`.
- OrdinaryContact uses `EffectiveMovementBinding.Ordinary` over that environment.
- PublicExplicit uses `EffectiveMovementBinding.PublicExplicit` with a `MovementEnvironmentContext` that wraps the same
  `OrdinaryMovementEnvironment`. Locomotion gains the extension that lets a public explicit binding over an ordinary
  provider create ordinary scopes and capture the ordinary boundary. Both kinds then read one world through one
  provider, with the explicit composition and water policy.
- Work runs in the context's local frame. Each column acquires one lease from the binding's ordinary scope and holds it
  across the column's enumeration, admission and proofs. Results are published only while that lease is current.
- An acquisition or query availability other than Known (`Unresolved`, `Invalid`, `FrameMismatch` and the other
  non-Known `MovementAvailability` values) marks the affected facts Unknown. Unknown facts never admit a node or edge.
- Support evidence comes from the lease's `FindContactSupport`, which returns the certified `SupportSample` with its
  height error and binding. No navigation-only physics query path exists.

### P5B-3 Capture lifecycle

`PhysicsNavBake.Capture` stays the shared legacy capture and keeps its signature. It now retains the area classifier
for later non-legacy capture. With `SampleWater` set it reads the context's medium delegate. A contact-constructed
context supplied with a water field but no medium delegate refuses `SampleWater` at `Capture` with a message naming
the missing delegate, instead of failing inside the field adapter. Non-legacy profiles do not need `SampleWater`,
because their water facts come from the field. Non-legacy capture runs inside `BuildProfile`.

### P5B-4 Footprint column enumeration

Each column is enumerated with the profile's foot disc (radius `FootRadiusFraction * CapsuleRadius`) at the cell axis.

1. A bounded broad-phase pass finds the vertical intervals of the column, between probe floor and probe top, that
   hold static geometry overlapping the foot disc column, plus the analytic ground height at the axis. Empty intervals
   are skipped. If the physics view has no query that answers this, the physics layer gains one.
2. Inside each occupied interval, certified support queries run top down with a band of `StepHeight`, the kernel's own
   reach. Each returns the highest certified support in its band. A found support is recorded as a raw footprint seed
   with its status (Walkable, Steep or Refused), height, height error, owner and raw index (descending order in the
   column). The next query starts strictly below the found support's certified lower bound. A band with no support
   moves down one band.
3. Enumeration stops at the probe floor or at `MaxSurfacesPerColumn` recorded seeds. Exceeding the cap throws, as
   capture does today.

Each seed owns the half-open band from its height up to the next seed above (unbounded for the top seed). A tie in
height goes to the upper seed.

### P5B-5 Ground node admission

A Walkable seed becomes a ground node only when all of these hold:

1. The candidate pose is a grounded body with feet at the composition's resting feet for the seed support (support
   plus 0 for ordinary, plus 0.001 m for explicit). `SettlePlacement` from that pose, through the profile's binding,
   returns `Advanced` with footing.
2. The settled support is the seed's own support within its certified height error. A settle that moves to another
   seed refuses this seed. That other seed owns the position and is judged on its own.
3. The Hold proof under P5B-8 passes at the settled pose: one stationary step returns `Advanced`, grounded, within the
   unchanged 3D 1 mm rule.

The node's height is the seed's resting feet. Layering runs once through the existing `NavLayerBaker` on those heights,
in raw seed order, and no second layering pass runs. Because a node exists only where its seed is its own resting
support, raw identity and resting identity coincide and no node can be reassigned to a deck or rooftop above it.

Headroom for a non-legacy node is a certified lower bound. The placement proof certifies the full resting shell clear
of every static, walkable or not, so the stored headroom is the body height. Navigation only ever compares headroom
against the body height, so the bound answers every consumer exactly. Off-axis overhangs and undersides are covered by
the shell proof rather than by an axis gap. Areas come from the classifier at the resting feet.

Steep, Refused and refused-admission seeds produce no node and stay as recorded facts (P5B-10).

### P5B-6 Placement fixes

Two Locomotion fixes make placement a resting constructor rather than a guess:

- `SettlePlacement` seats a non-rising body only when the seated pose grants footing under the water policy.
  Otherwise the supplied pose stands and is classified by water. A swimmer at its float line therefore stays there.
  Spawn, restore, teleport, transfer and continuation all take this rule, with tests for each door that seats.
- A settled grounded pose must be a Hold fixed point. Any fixture where a settled pose moves by more than 1 mm on its
  next Hold is a placement defect and is fixed in the placement code, never by repeating settlement or widening the
  Hold.

### P5B-7 Water classification (#1299)

- **Ground nodes under water.** P5B-5 already runs the runtime placement and water admission at the resting candidate.
  A WadeOnly body past the deep line, a DryOnly body in water and a body deep enough to swim fail footing and produce
  no ground node. A submerged lip whose resting pose is admissible keeps its node even when the ground beside it is
  past the line.
- **Column water discovery.** For SurfaceSwimmer aquatic profiles, a bounded vertical water trace along the axis
  through the lease, from probe top to probe floor, reports each water interval the axis crosses with its free or
  closed upper boundary. Unknown or conflicting answers refuse float candidates in that column. The field is never read
  outside the lease.
- **Float nodes.** Each interval with a free surface yields one float candidate at the runtime float pose under it
  (`SwimSurfaceSubmersionFraction`). It becomes a node when `ValidatePlacement` from a swimming state at that pose
  returns `Advanced` with the body swimming and its swim Hold passes.
- **Both modes.** A column keeps its ground node and its float node when the runtime admits both, as in the swim
  hysteresis band. No node is removed because the other mode is admissible, and no scalar depth formula decides a
  non-legacy node.

Legacy aquatic derivation is unchanged.

### P5B-8 Traversal over movement results

Non-legacy proofs step the profile's binding with `StepTowards` and read every `MovementStepResult`.

- Any outcome other than `Advanced` at the Hold or any slice refuses the node or edge. An unchanged pose is never
  evidence that a query succeeded. No prefix of a refused proof is published.
- The bake options own timing and budget. Ground slices use unit-speed probe tuning. Swim slices keep the existing
  swim pace bound. Edges step the wet binding, so the WadeOnly wall and swim transitions shape them as at runtime.
- Every slice passes the per-slice footprint admission against the profile's recorded facts (P5B-10).
- Grounded arrival is the shared 3D 1 mm sphere with the body grounded. Float arrival is the body swimming, within 1 mm
  in XZ and within the policy's surface-contact tolerance vertically. The final mode must equal the target node's mode.
  An accepted end state must satisfy the start contract of every edge leaving the target node, so consecutive edges
  compose.
- Stair links use the same proof.

Legacy proofs, including `SwimArrived`, are unchanged.

### P5B-9 Explicit profiles

PublicExplicit profiles use the same enumeration, admission, water and traversal core as OrdinaryContact, through
their binding from P5B-2 and with the explicit composition. `ExplicitMotionProbe` is not used by the bake.

`ExplicitMotionProbe` takes the shared 3D 1 mm sphere for grounded arrival at both Hold and destination, while its
swimming rule keeps the surface-contact tolerance. This only refuses arrivals the old per-axis rule accepted outside
the sphere. The explicit route proof recipe version moves from 1 to 2, so explicit fingerprints change once, and its
pinned test moves with it.

### P5B-10 KENB format 2

An all-legacy bake writes format 1, byte for byte as today. A full-file golden for a small all-legacy fixture under the
golden engine string pins it. Any non-legacy profile makes the bake format 2.

The load matrix, with format-directed decoding first and refusal precedence after it:

| Stored file | Expected profiles | Same engine | Other engine |
| --- | --- | --- | --- |
| Format 1 | All legacy | Existing comparison, Loaded if equal | EngineChanged |
| Format 1 | Any non-legacy | ProfilesChanged | EngineChanged |
| Format 2 | All legacy | ProfilesChanged | EngineChanged |
| Format 2 | Any non-legacy | Format 2 comparison, Loaded if equal | EngineChanged |

Versions other than 1 and 2 and nonzero flags stay `UnsupportedFormat`. A malformed identity in a supported format
stays `Corrupt`. An invalid expectation throws `ArgumentException` before the stream is read, as today. Inside one
format the order stays engine, options, sources, profiles.

The format 2 identity adds per profile: effective kind, foot radius fraction, every `WaterTraversalPolicy` field,
placement offset and, for explicit profiles, the explicit fingerprint.

The format 2 payload has two stages per non-legacy profile, after the format 1 shared columns:

- **Facts.** Every recorded seed and float candidate per column with raw index, status, disposition (node, steep,
  refused, unknown, settled away, water refused), support height, height error, resting feet, headroom and areas.
  Per-slice footprint admission reads these facts, so a refused upper surface or an Unknown neighbour still blocks a
  footprint after load exactly as before it.
- **Graph.** Nodes reference facts by column and raw index. Layers, exits and links follow as format 1 writes them.

Field widths and order are fixed, every count is bounded before allocation (including the float slot and the aggregate
per-profile storage), and invalid raw references are `Corrupt`. A loaded format 2 graph is rebuilt from the stored
facts and nodes, never recaptured, and rewriting it reproduces its bytes.

### P5B-11 Runtime graph compatibility

`GroundNavigation` carries the effective identity of the profile that built it. `BuildProfile` and the format 2 decoder
both populate it, and format 1 graphs carry the Legacy identity.

- A new public `GroundNavigation.Validate(EffectiveMovementBinding binding)` and an overload adding the consumer's
  `ExplicitMovementProfile` check the graph against the binding. Legacy graphs keep today's comparisons. Non-legacy
  graphs compare every field their format 2 identity encodes: the 27 retained tuning fields, controller, kind, foot
  fraction, placement offset, every water policy field and, for explicit graphs, the explicit fingerprint. A mismatch
  throws `ArgumentException` naming the differing input.
- `MoveToRange` validates against its context's binding before planning and before prediction.
- Callers that use `Planner` or `AllowsSegment` directly own the call. The public documentation says so.
- `DirectMoveToRange` owns no graph and is unchanged.

### P5B-12 Version, placement and test dispositions

The phase rides staged 20.37.0, because it is ahead of v20.36.0 and its entry is untagged. Navigation types stay in
`KhaozEngine.Movement` beside the bake code. The binding and placement extensions live in `KhaozEngine.Locomotion`
beside the code they extend. `KhaozEngine.Navigation` stays free of Locomotion.

Intentional test dispositions:

- `ContactNavigationRefusalTests` rows that require the "phase 5b" refusal become capability and configuration refusals
  (legacy-constructed context, Aquatic without SurfaceSwimmer, mismatched explicit tuning or timing), keeping their
  validation-order controls, plus valid contact construction rows.
- The `GroundNavigationBakeRefusalTests` row that declares version 2 unsupported moves to version 3.
- The explicit profile fingerprint pin moves with the recipe version.

Every other legacy behavioural, round-trip and byte-identity test passes unchanged.

## Acceptance

Compatibility:

- The all-legacy full-file golden matches.
- Every cell of the P5B-10 matrix, a combined engine and profile mismatch, and the corruption rows (bad counts, invalid
  raw references, truncated facts) return their named status.
- A contact profile and an explicit profile each round-trip. Write, load and rewrite are byte-identical, and the loaded
  graph and its footprint admission equal the fresh ones, including near forbidden upper surfaces, low roofs and
  Unknown neighbouring cells.
- A mixed legacy aquatic and contact bake with `SampleWater` round-trips, a non-legacy aquatic profile works with
  `SampleWater` off, and a water-field-only context with `SampleWater` refuses at `Capture`.

Capture and admission:

- The actual SG-23 geometry yields a contact node where the axis has no hit.
- The #1270 low-lip bank yields nodes at resting height whose Hold passes at the unchanged 1 mm rule.
- A deck over ground, a thick slab, stacked props, a compound gap, two supports at a shared height and an off-axis
  overhang each keep their nodes in their own raw bands, with no duplicate and no wrong-layer node.
- A low roof the body does not fit under produces no node.
- Hold refusal after a successful placement, acquisition failure at the Hold and a late support refusal on a slice
  each refuse without publishing a prefix.

Water and #1299:

- A WadeOnly contact bake over a pool with a deep middle marks the deep cells unwalkable and a route plans around them,
  matching where the runtime walker stops.
- A submerged lip whose resting pose crosses the WadeOnly threshold keeps its node, and the inverse control whose
  certified resting pose is past the line has none.
- Just below, at and just above the enter and exit fractions, plus one row inside the hysteresis band where both a
  ground node and a float node exist.
- Empty-column water, a dry deck over water, a closed upper boundary, conflicting intervals and Unknown water each
  produce their stated float result.
- A DryOnly bake has no node in water. A SurfaceSwimmer aquatic bake has float nodes and swim edges in both bank
  directions, including a near-waterline ground target that refuses a still-swimming arrival.
- Legacy bakes over the same pool, with and without `Aquatic`, are byte-identical to today's.

Placement fixes:

- A swimmer settled at its float line over a bed that grants no footing keeps its pose and holds within 1 mm, on every
  placement door that seats.

Explicit and runtime:

- Mismatched explicit timing, budget, tuning or areas refuse. An excluded area reached only on an intermediate slice
  refuses the edge.
- Public `ProbeExplicitMotion` accepts at the 1 mm sphere boundary and refuses the old corner case (0.8 mm in X and
  0.8 mm in Y), at the Hold and at the destination.
- `MoveToRange` with a mismatched binding throws before planning, for fresh and loaded graphs, and `Validate` refuses
  each P5B-11 mismatch.

Cost record, from single local measurements only (no load or repeat run):

- calls and time per column, per raw seed and per admitted node, per float candidate and per edge proof,
- profile count, refused contributions, water traces, peak resident arrays and payload size per profile,
- a conservative static upper bound and aggregate storage limits beside a Hollowmere-scale extrapolation, for the
  owner's phase 6 decision.

## Tasks

1. Effective bake profiles, door validation and the capture lifecycle (P5B-1, P5B-3).
2. Locomotion extensions: explicit ordinary scopes and the placement fixes (P5B-2, P5B-6).
3. Footprint column enumeration and ground node admission (P5B-4, P5B-5).
4. Traversal over movement results (P5B-8).
5. Water classification, float nodes and the #1299 rows (P5B-7).
6. Explicit bake profiles and the arrival correction (P5B-9).
7. KENB format 2 and the compatibility matrix (P5B-10).
8. Runtime graph compatibility, cost record, documentation, changelog and full verification (P5B-11, P5B-12).

## Review response

Round 1 (`review-1.md`, Not ready, 2 Critical, 12 Important, 4 Minor):

- F1: axis seeds replaced by footprint column enumeration (P5B-4).
- F2: admission runs at the resting candidate, which is the seed itself (P5B-5, P5B-7).
- F3, U1: placement fixes (P5B-6).
- F4: seeds are certified supports, not ray samples, and headroom is a certified lower bound (P5B-4, P5B-5).
- F5: binding and acquisition contract for both kinds (P5B-2).
- F6: column water discovery by a bounded trace through the lease (P5B-7).
- F7: both modes kept where admissible (P5B-7).
- F8: separate predicates, target mode required, legacy unchanged and #1388 filed (P5B-8).
- F9: proofs over `MovementStepResult` (P5B-8).
- F10: one normalized explicit recipe and a shared core (P5B-1, P5B-9).
- F11: public validation door and consumer wiring (P5B-11).
- F12: explicit load matrix (P5B-10).
- F13: two-stage format 2 payload (P5B-10).
- F14: capture lifecycle (P5B-3).
- F15: door-time normalization (P5B-1).
- F16: public probe rows and recipe version (P5B-9).
- F17: named test dispositions (P5B-12).
- F18: expanded cost record (Acceptance).
- U2: layer heights are seed resting heights, so raw and resting identity coincide (P5B-5).
