# Contact controller phase 5b: navigation

Design for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5b and the navigation half of
[#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Source baseline: `ee6016aae` on `main`, staged 20.37.0
with phase 5a. The binding inputs are the [phase 5 design](CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md#phase-5b-binding-inputs).
Written under the owner's instruction to continue the program to completion, so every choice below is a recorded
ruling for the owner's review. Revision 3 answers review rounds 1 and 2.

Program row exit: legacy bakes still load, a new-controller bake round-trips, #1299 closes.

## Scope

In scope: ground navigation profiles on the ordinary contact controller, built through that profile's own effective
movement binding. That covers raw seed discovery, resting admission, water policy at rest, contact traversal proofs,
KENB format 2, validation on every door, runtime graph compatibility, the #1299 navigation acceptance and the #1270
resting route on the engine side.

Out of scope:

- Non-legacy aquatic profiles, public explicit bake profiles and the legacy swim arrival defect. They move to phase 5c,
  [#1389](https://github.com/APKiwiOrg/KhaozEngine/issues/1389), under ruling P5B-11.
- Legacy capture, legacy traversal and legacy graph behaviour, which stay unchanged.
- Native MapDoc movement and `MapNavTiling` vertical layers (World Authoring G1b), #1355 certified sweep shapes, #1344
  small bodies, the SG-12 budget, any hosted or local load run, engine release, tag, game pin, Grimhollow edits and
  world edits.

## Owner constraints

- Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity. Never widen a tolerance.
- Do not restart completed proofs.
- A known proper fix is implemented rather than a workaround shipped, even outside a scope (owner ruling, 2026-10-08).

## Current state

Facts verified against `ee6016aae` that drive the rulings:

1. `PhysicsNavBake.Capture` takes no tuning. `PhysicsColumnProbe` descends the cell axis by raycasts, skips
   inside-solid samples, traces undersides and returns standable hits with headroom to the hit above. Steep hits and
   free-air intervals are not exposed. A support only the foot disc touches, such as SG-23 at X 0.35 with foot radius
   0.2, has no axis hit.
2. `BuildProfile` keeps a surface only when its own Hold proof passes. Ground edges step `DryContext`, which is legacy
   constructed. `GroundMoveContext.StepContact` drops the movement outcome, so a MoveState proof cannot see an
   unresolved step. Probe tuning is `tuning with { WalkSpeed = 1, RunSpeed = 1, AirMomentum = false }`, while
   `EffectiveMovementBinding` tuning is immutable.
3. `ValidateProfileController` refuses Contact at `Create` and `BuildProfile`. `Load` does not, and `Controller`,
   `FootRadiusFraction` and `Water` are outside the v1 identity, so a contact expectation loads a legacy file.
4. `GroundNavigation.ValidateTuning` is internal and compares geometry and aquatic swim fractions only. `MoveToRange.Tick`
   is its only caller.
5. `SettlePlacement` runs `ValidatePlacement`, including water admission, at the supplied pose before it seats. The seat
   search, `ContactPlacement.Find` and `TrySeat`, happens after.
6. `NavAreaFootprint` rejects a touched cell with no captured surface and selects the nearest captured surface by height
   midpoints before applying headroom, reach and areas. It reads captured columns, not graph nodes.
7. Every headroom consumer outside `AquaticColumns` compares headroom with the same profile's body height.
8. The only v1 byte pin is the identity fingerprint golden. No full-file golden exists.

## Rulings

### P5B-1 Effective bake profiles

A `NavBakeProfile` resolves to **Legacy** when `Tuning.Controller` is Legacy, or **OrdinaryContact** when it is
Contact. Legacy behaviour, identity and bytes are unchanged. OrdinaryContact runs
`EffectiveMovementBinding.Preflight` for its kind.

An OrdinaryContact profile with `Aquatic` set refuses with `NotSupportedException` naming #1389. No explicit property is
added to `NavBakeProfile` in this phase.

The constructor keeps only selection-independent checks. Complete normalization runs at `BuildProfile`, `Create` and
every `Load` expectation, so no door admits a profile another door refuses. `ValidateProfileController` and its
"phase 5b" refusal are removed.

### P5B-2 Bake bindings, stages and leases

- A contact profile needs a contact-constructed `GroundMoveContext` (registration, boundary and water field). A legacy
  constructed context refuses it with `NotSupportedException`.
- Each contact profile uses two bindings over that context's ordinary environment. The **source binding** is built from
  the profile tuning and carries the identity. The **proof binding** is built from the normalized proof tuning, the
  profile tuning with `WalkSpeed = 1`, `RunSpeed = 1` and `AirMomentum = false`, exactly as legacy proofs normalize.
  Every placement and step in this design runs through the proof binding. The two differ only in those three fields.
- `BuildProfile` preflights the capture bounds and the probe height and range against the ordinary served scope and
  refuses a range the scope does not contain.
- Work runs in stages, each reading only immutable output of earlier stages: raw seeds and footprint facts for every
  column (P5B-3, P5B-5), resting admission for every seed (P5B-4), layering (P5B-5), then edge proofs (P5B-7).
- Every query and proof acquires its own lease from the proof binding's ordinary scope in the context's local frame,
  and publishes its result only while that lease is current. The world generation recorded at the first stage must
  equal every later lease's generation, or the build throws. A lease acquisition other than Known throws
  `InvalidOperationException` naming the cell. A bake publishes no partial graph.
- `PhysicsNavBake` retains its area classifier for these stages. With `SampleWater` set, shared capture reads the
  context's medium delegate. A contact-constructed context with a water field but no medium delegate refuses
  `SampleWater` at `Capture` with a message naming the missing delegate. Contact profiles do not need `SampleWater`,
  because their water facts come from the field at placement.

### P5B-3 Raw seeds

`PhysicsColumnProbe` gains `SampleIntervals`, built on its existing descent. It reports the axis column's free-air
intervals in ascending order. Each interval has a floor (a genuine standable hit, a genuine steep hit, or the probe
floor) and a ceiling (the underside traced above it, or the probe top). Inside-solid samples stay excluded as floors,
and the underside tracing is the one `Sample` already uses. `Sample` and the legacy capture are unchanged.

For each column a contact profile records raw seeds, in ascending order:

- **Axis seeds.** Each interval's standable floor, which is exactly the legacy captured surface.
- **Footprint seeds.** In each interval, one downward sweep of the foot probe (radius `FootRadiusFraction *
  CapsuleRadius`, the shape `FootSupport` already sweeps) from just below the ceiling to the floor, through the proof
  binding's physics view and statics filter. Its first contact becomes a footprint seed when it lies strictly above
  the interval's floor and strictly below its ceiling. A sweep that starts in contact records no seed in that interval.

Raw seed heights in a column are strictly ascending by construction. Axis seeds keep the legacy cap and its cap plus one
overflow witness. Footprint seeds number at most one per interval, so a column holds at most the cap plus one interval
of them. A column above the cap throws, as capture does today.

The SG-23 flank has no axis hit, but the foot probe touches the sphere, so its interval yields a footprint seed.

### P5B-4 Resting admission

Each raw seed becomes a node only when all of these hold, judged in ascending raw order:

1. **Resting candidate.** A new internal Locomotion query on the binding runs placement's own support search and seat,
   `ContactPlacement.Find` and `TrySeat` with the composition and reach `SettlePlacement` uses, from a grounded body with
   feet at the seed height. It returns the seated feet and the certified support sample, and decides no water or
   footing. A refused or unresolved search admits nothing.
2. **Ownership.** The seated feet lie strictly above the next lower raw seed and strictly below the next higher raw seed
   in the column, and within 1 mm of the seed axis in XZ. A seed that seats onto a higher or lower seed's position
   yields no node, because that seed owns it. A deck or rooftop therefore never becomes another surface's node.
3. **Placement.** `SettlePlacement` at the seated pose returns `Advanced`, grounded with footing, within the unchanged
   3D 1 mm rule of the seated pose. This is where the full shell clearance, the water policy and Unknown water are
   decided, at the resting pose rather than at the raw seed.
4. **Hold.** One stationary step through the proof binding returns `Advanced`, grounded, within 3D 1 mm (P5B-7).
5. **Identity.** A seated pose within the 1 mm rule of a node already admitted in the column is that node. The lower
   raw seed keeps it and the later seed adds nothing. Admitted resting heights that are not strictly ascending in raw
   order refuse both seeds involved.

The node's height is its resting feet. Ordinary placement offset is 0, so resting feet equal the certified support.
Headroom is the axis gap from the resting feet to its interval's ceiling. It keeps non-walkable ceilings and undersides
as raw evidence, and admission requires it to be at least the body height in addition to the certified shell
clearance of step 3, which covers off-axis overhangs. A settled grounded pose that moves by more than 1 mm on its next
Hold is a placement defect fixed in the placement code, never by repeated settlement or a wider Hold.

### P5B-5 Layering and footprint facts

- **Footprint facts.** The contact profile's footprint columns are the shared captured surfaces plus its footprint
  seeds, each with its raw height, its axis headroom to the interval ceiling and its classifier areas at the seed. They
  are complete before any admission or proof reads them, so no node's admission depends on another node's.
  `NavAreaFootprint` itself is unchanged and reads these columns exactly as it reads legacy ones.
- **Layering.** Layer extraction runs once through the existing `NavLayerBaker` over the admitted seeds at their raw
  heights, ascending, with their node headroom. The baked layer cells then take each node's resting height. No second
  layering pass runs. This is the raw layer identity rule of the low-lip resting design: layers come from raw seeds and
  a settled height never re-layers a node.

### P5B-6 Water policy and #1299

Contact navigation in this phase is ground only. A node exists where the runtime would rest a grounded body with
footing, decided by `SettlePlacement` at the resting pose under the profile's water policy:

- WadeOnly refuses footing past the deep line, so deep cells have no node and routes plan around them.
- DryOnly refuses footing in water.
- SurfaceSwimmer refuses footing where a grounded body would start to swim. The ground node in the swim hysteresis band
  stays, because a grounded body keeps footing there.
- A water answer other than Known refuses the node.

No scalar depth formula decides a contact node. A submerged lip whose resting pose is admissible keeps its node even
where the raw ground beside it is past the line. Legacy aquatic derivation is unchanged.

### P5B-7 Traversal over movement results

Contact proofs step the proof binding with `StepTowards` and read every `MovementStepResult`.

- Each edge starts from the canonical rest state at its source node: grounded, zero velocities, the node's resting
  feet. The source node's Hold certifies that state is at rest. This is the contract legacy edges already use.
- The Hold is the first slice of the edge budget. The bake options own timing and budget.
- Any outcome other than `Advanced` at the Hold or at any slice refuses the edge. An unchanged pose is never evidence
  that a query succeeded. No prefix of a refused proof is published.
- Every slice passes the footprint admission of P5B-5 and stays grounded.
- Arrival is the shared 3D 1 mm sphere with the body grounded.
- Edges step the wet binding, so the WadeOnly wall and the swim threshold shape them as at runtime.
- Stair links use the same proof.

Legacy proofs are unchanged.

### P5B-8 Explicit grounded arrival

`ExplicitMotionProbe` takes the shared 3D 1 mm sphere for grounded arrival at both its Hold and its destination. Its
swimming rule keeps the surface-contact tolerance. This only refuses arrivals the old per-axis rule accepted outside
the sphere. The explicit route proof recipe version moves from 1 to 2, so explicit fingerprints change once, and the
pinned test moves with it. This is an independent proper fix of the public probe, and it needs nothing from P5B-11.

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
identity in a supported format stays `Corrupt`. An invalid expectation throws `ArgumentException` before the stream is
read. Inside one format the order stays engine, options, sources, profiles.

The format 2 identity writes the format 1 identity fields, then per profile, in profile order, the fields below.
`WaterTraversalPolicy` stores only its mode and surface jump speed. Its other members are engine constants that the
engine version already covers.

| Field | Width | Values |
| --- | --- | --- |
| Effective kind | u8 | 0 Legacy, 1 OrdinaryContact |
| Foot radius fraction | f32 bits | finite, in (0, 1] |
| Water mode | u8 | `WaterTraversalMode` value, 0 only for Legacy |
| Surface jump speed | f32 bits | finite, as `WaterTraversalPolicy` validates it |
| Placement offset | f32 bits | 0 for both kinds in this phase |

The format 2 payload writes the format 1 payload, with each profile section prefixed by its effective kind byte. A
contact profile section follows its format 1 graph body (layers carrying resting heights, exits and links) with a facts
section:

| Field | Width | Rule |
| --- | --- | --- |
| Footprint seed count | i32 | at most cells times (cap plus one), checked against remaining bytes before allocation |
| Per seed: cell | i32 | ascending, in range |
| Per seed: raw height | f32 | finite, strictly ascending within a cell, never equal to a shared surface height in that cell |
| Per seed: headroom | f32 | at least 0, not NaN |
| Per seed: areas | u32 | any |

Every count is checked against the remaining payload bytes at its minimum encoded size before allocation, and the sum
over all profiles is bounded by the payload length, so 256 profiles cannot allocate beyond the file. A loaded contact
graph is rebuilt from its stored layers and facts, never recaptured, and rewriting it reproduces its bytes. The loader
never runs `AquaticColumns.Derive` for a contact profile.

### P5B-10 Runtime graph compatibility

`GroundNavigation` carries the effective identity of the profile that built it. `BuildProfile` and the format 2 decoder
populate it, and format 1 graphs carry Legacy.

- A new public `GroundNavigation.Validate(in MoveTuning tuning)` first requires the tuning's effective kind to equal the
  graph's, for every graph. Legacy graphs then keep today's comparisons. Contact graphs compare every field their format
  2 identity encodes: the 27 retained tuning fields, controller, foot fraction, placement offset, water mode and surface
  jump speed. A mismatch throws `ArgumentException` naming the differing input.
- `MoveToRange` calls it before planning and before prediction, replacing the internal call.
- Callers that use `Planner` or `AllowsSegment` directly own the call, and the public documentation says so.
- `DirectMoveToRange` owns no graph and is unchanged.

### P5B-11 Deferred work and known defects

- Non-legacy aquatic navigation needs node identity with a ground or float mode independent of height, carried through
  facts, layers, planner, follower and segment admission, because ground and float slots can coincide exactly in the
  swim hysteresis band. That is a representation change for the creature consumers that #1344 gates, so it moves to
  #1389 with column water discovery.
- The legacy swim arrival defect #1388 needs the same mode-aware nodes. A plain mode check would refuse every swim to
  ground edge into the hysteresis band and disconnect gentle bank exits. It moves to #1389.
- `SettlePlacement` seats a swimmer onto a bed that grants no footing (review round 1 F3). Settle doors only are
  affected, and float placement is #1389's subject, so it moves there. Transfer and continuation keep their no-seat
  contract.
- Public explicit bake profiles need ordinary-backed explicit environments, a normalized proof binding and a public
  explicit follower. Their consumers also wait on #1344, so they move to #1389.

None of these ships a workaround. 5b adds no swim, float or explicit navigation path, and refuses those selections at
every door with a message naming #1389.

### P5B-12 Version, placement, determinism and test dispositions

The phase rides staged 20.37.0, because it is ahead of v20.36.0 and its entry is untagged. Navigation types stay in
`KhaozEngine.Movement` beside the bake code. The resting-candidate query lives in `KhaozEngine.Locomotion` beside
placement. `SampleIntervals` lives in `KhaozEngine.Physics` beside `Sample`. `KhaozEngine.Navigation` stays free of
Locomotion.

Every stage computes each column, seed and edge as a function of immutable earlier output, so results do not depend on
iteration order. A later partitioned parallel build changes no byte.

Intentional test dispositions:

- `ContactNavigationRefusalTests` rows that require the "phase 5b" refusal become capability and configuration
  refusals (legacy-constructed context, contact Aquatic), keeping their validation-order controls, plus valid contact
  construction rows.
- The `GroundNavigationBakeRefusalTests` row that declares version 2 unsupported moves to version 3.
- The explicit profile fingerprint pin moves with the recipe version.

Every other legacy behavioural, round-trip and byte-identity test passes unchanged.

## Acceptance

Compatibility:

- The all-legacy full-file golden matches.
- Every cell of the P5B-9 matrix, a combined engine and profile mismatch, and corruption rows (bad counts, unsorted or
  duplicate seeds, a seed equal to a shared surface, truncated facts, an unknown kind byte) return their named status.
- A contact profile round-trips. Write, load and rewrite are byte-identical, and the loaded graph and its footprint
  admission equal the fresh ones near forbidden upper surfaces and low roofs.
- A mixed legacy aquatic and contact bake with `SampleWater` round-trips. A water-field-only context with `SampleWater`
  refuses at `Capture`. Contact Aquatic refuses at every door naming #1389.

Capture and admission:

- The actual SG-23 geometry yields a contact node where the axis has no hit.
- The #1270 low-lip bank yields nodes at resting height whose Hold passes at the unchanged 1 mm rule.
- A deck over ground, a thick slab with clear ground below, stacked props, a compound gap, an off-axis overhang and a
  steep axis hit over a walkable footprint each keep their nodes in their own raw bands, with no duplicate and no
  wrong-layer node.
- A low roof the body does not fit under produces no node.
- A seed that seats sideways by more than 1 mm yields no node.
- Exactly the cap and cap plus one axis surfaces behave as today.
- Hold refusal after a successful placement, a late support refusal on a slice and a changed world generation each
  refuse without publishing a prefix.

Water and #1299:

- A WadeOnly contact bake over a pool with a deep middle marks the deep cells unwalkable and a route plans around them,
  matching where the runtime walker stops.
- A submerged lip whose resting pose is admissible keeps its node beside raw ground past the line, and the inverse
  control whose resting pose is past the line has none.
- Just below, at and just above the enter fraction, plus one row inside the hysteresis band, for SurfaceSwimmer and
  WadeOnly ground profiles.
- A DryOnly bake has no node in water.
- Legacy bakes over the same pool, with and without `Aquatic`, are byte-identical to today's.

Traversal and runtime:

- Contact edges step the proof binding. An edge whose Hold or slice returns a non-Advanced outcome is refused, and an
  edge into a WadeOnly deep cell stops at the wall and is refused.
- Public `ProbeExplicitMotion` accepts at the 1 mm sphere boundary and refuses the old corner case (0.8 mm in X and
  0.8 mm in Y), at the Hold and at the destination.
- `MoveToRange` with a mismatched tuning throws before planning, for fresh and loaded graphs, in both directions
  (legacy graph with contact tuning and the reverse), and `Validate` refuses each P5B-10 mismatch.

Cost record, from single local measurements only (no load or repeat run):

- calls and time per column, per interval sweep, per raw seed, per admitted node and per edge proof,
- refused seeds by reason, peak resident arrays and payload size per profile,
- a conservative static upper bound and aggregate storage limits beside a Hollowmere-scale extrapolation, for the
  owner's phase 6 decision.

## Tasks

1. Effective profiles, door validation, capture lifecycle and runtime compatibility (P5B-1, P5B-2, P5B-10).
2. Physics and Locomotion seams: `SampleIntervals`, the resting-candidate query and the explicit arrival correction
   (P5B-3, P5B-4, P5B-8).
3. Raw seeds, footprint facts, resting admission and layering (P5B-3 to P5B-6).
4. Contact traversal over movement results (P5B-7).
5. KENB format 2 and the compatibility matrix (P5B-9).
6. Cost record, documentation, changelog and full verification (P5B-12).

## Review response

Round 1 (`review-1.md`, Not ready, 2 Critical, 12 Important, 4 Minor) and round 2 (`review-2.md`, Not ready, 3 Critical,
10 Important):

- F1, G1, G4: seeds are the axis column's standable floors plus one foot probe contact per free-air interval, and
  certification comes from placement's own search. No band enumeration of `FootSupport` and no Refused height
  arithmetic remain (P5B-3, P5B-4).
- F2: water and placement admission run at the seated pose (P5B-4).
- F3, G7, G2, F6, G8, F7, G3: swimming navigation, coincident modes, column water discovery, the swimmer seat defect
  and #1388 move to #1389 under P5B-11. Ground rows in the hysteresis band stay (P5B-6).
- F4, G12 (headroom): headroom is raw axis evidence plus the certified shell (P5B-4).
- F5, G6, F10, G11, G13 (explicit): explicit bake profiles move to #1389. Contact proofs use a separate proof binding
  for unit speed (P5B-2).
- F8, G9: ground-only edges keep the canonical rest start contract certified by the Hold (P5B-7).
- F9: proofs over `MovementStepResult` (P5B-7).
- F11, G13: public `Validate` with kind equality first and `MoveToRange` wiring (P5B-10).
- F12: load matrix (P5B-9).
- F13, G10, G12: footprint facts are complete before admission, and format 2 has a layout table and bounds (P5B-5,
  P5B-9).
- F14: capture lifecycle (P5B-2).
- F15: door-time normalization (P5B-1).
- F16: public probe rows and recipe version (P5B-8).
- F17: test dispositions (P5B-12).
- F18: cost record (Acceptance).
- G5, U3: seeds are strictly ascending, ownership is the open band between neighbouring seeds plus 1 mm XZ, and
  identity is the 1 mm rule with the lower seed keeping a shared node (P5B-4).
- U1: Hold fixed point disposition (P5B-4). U2: layering on raw seed heights (P5B-5).
