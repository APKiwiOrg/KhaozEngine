# Contact controller phase 5b: navigation

Design for [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438) phase 5b and the navigation half of
[#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299). Source baseline: `ee6016aae` on `main`, staged 20.37.0
with phase 5a. The binding inputs are the [phase 5 design](CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md#phase-5b-binding-inputs).
Written under the owner's instruction to continue the program to completion, so every choice below is a recorded
ruling for the owner's review.

Program row exit: legacy bakes still load, a new-controller bake round-trips, #1299 closes.

## Scope

In scope:

- Navigation profiles on the ordinary contact controller and on public explicit profiles, built through the
  profile's own effective movement binding.
- Contact column capture by the footprint rule, contact traversal proofs and contact water classification.
- KENB format 2, the v1 compatibility contract and load-time validation of every profile.
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

The code digest for this design is kept with the spec review ledger. The facts that drive the rulings:

1. `PhysicsNavBake.Capture` takes no tuning. It casts `PhysicsColumnProbe` axis rays per cell and keeps standable hits
   as surfaces. Non-standable hits only set the headroom of the surface below.
2. `BuildProfile` filters surfaces by headroom and keeps a surface only when its own Hold proof passes. Edge proofs step
   `GroundMoveContext.DryContext`, which is always constructed with the legacy constructor, so a contact context cannot
   prove an edge today.
3. `ValidateProfileController` refuses Contact profiles and non-default `FootRadiusFraction` or `Water` at `Create` and
   `BuildProfile`. `Load` runs only `CheckTuning` and the slope check. `Controller`, `FootRadiusFraction` and `Water`
   are outside the v1 identity, so a contact expectation compares equal to a legacy file and loads a legacy graph.
4. `GroundNavigation.ValidateTuning` compares radius, half height, slope, step height and, for aquatic graphs, the
   three swim fractions. Nothing compares the controller, foot fraction, water policy, placement offset or explicit
   identity.
5. Deep water in an aquatic legacy profile is a scalar test in `AquaticColumns.Derive`: depth from the highest captured
   surface below the water against `SwimEnterDepthFraction`. Non-aquatic graphs have no deep rule.
6. `ExplicitMotionProbe` proves grounded arrival with a 1 mm XZ circle plus a separate 1 mm vertical bound, not the
   shared 3D 1 mm sphere `GroundTraversalProbe.Near` uses.
7. Every contact binding has a non-Legacy water mode (`ValidateExplicit`). `EffectiveMovementBinding.SettlePlacement`
   is the runtime's own placement operation. It seats a grounded body on certified walkable support through
   `ContactPlacement`, validates the full shell, samples water and decides footing under the water policy.
8. The only v1 byte pin is the identity fingerprint golden in `NavBakeIdentityTests`. No full-file golden exists.

## Rulings

### P5B-1 Effective bake profiles

`NavBakeProfile` resolves to exactly one effective bake profile, of the same three kinds as
`EffectiveMovementBindingKind`:

- **Legacy** when `Tuning.Controller` is Legacy and no explicit profile is attached. Behaviour, identity and bytes are
  unchanged.
- **OrdinaryContact** when `Tuning.Controller` is Contact. `EffectiveMovementBinding.Preflight` runs for that kind.
- **PublicExplicit** when an `ExplicitMovementProfile` is attached through a new init property
  `NavBakeProfile.Explicit`. The explicit profile's tuning geometry (radius, half height, slope, step height, foot
  fraction) and areas must equal the bake profile's, otherwise construction refuses with `ArgumentException`. The bake
  profile's `Tuning` must not select Contact.

`Aquatic` is allowed on a non-legacy profile only when its water mode is SurfaceSwimmer. Every other combination refuses
at construction. Validation runs at `NavBakeProfile` construction, `BuildProfile`, `Create` and every `Load`
expectation, so no path admits a profile that another path refuses. `ValidateProfileController` and its "phase 5b"
refusal are removed.

**Why:** one normalized profile per kind mirrors the runtime's binding kinds, and validating on every door closes the
load gap in current state item 3.

### P5B-2 Capture runs per non-legacy profile through its own binding

Legacy profiles keep the shared column capture. A non-legacy profile captures its own columns during `BuildProfile`,
because its resting heights depend on its foot radius, composition offset and water policy.

- The bake context for a non-legacy profile must be a contact-constructed `GroundMoveContext` (registration, boundary
  and water field). A legacy-constructed context refuses a non-legacy profile with `NotSupportedException`.
- Every query goes through the profile's effective binding and a lease from its ordinary scope. No parallel query path
  exists for navigation.
- The shared capture options (bounds, cell size, probe height and range, slope, surface cap, edge budget) apply
  unchanged. `SampleWater` governs legacy water entries only. Non-legacy water facts come from the binding's water
  field.

### P5B-3 Footprint column probe

For each cell the contact capture takes every axis ray hit, standable or not, as a raw seed. Each seed keeps its raw
index in the column and its ownership band, which runs from the seed's height to the next raw hit above (unbounded for
the top hit).

Each seed becomes a candidate node only when all of these hold:

1. `SettlePlacement` from a grounded state with feet at the seed height returns `Advanced` with footing, through the
   profile's binding. The settled feet are the candidate's resting feet, and the certified support under them is its
   geometric support.
2. The resting feet lie inside the seed's ownership band, at or above the seed height less the certified support
   height error, and the resting body fits below the band's ceiling. A seed that settles into another seed's band
   produces no node. That seed's own capture owns the position.
3. The candidate passes its own Hold proof under P5B-5 (stationary within the unchanged 1 mm arrival).

Unresolved, invalid, refused or steep outcomes produce no node. Areas come from the classifier at the resting feet.
Headroom is the axis gap from the resting feet to the next raw hit above, walkable or not, so non-walkable ceilings
stay. The full-shell clearance of the resting body is certified by the placement proof itself.

Candidates are layered once by the existing `NavLayerBaker` in raw seed order. No second layering pass runs, and a
settled height never reassigns a node to another seed.

**Why:** settled heights are the runtime's resting heights by construction, so the Hold proof passes without widening
the 1 mm rule. Non-standable seeds let footprint-only support such as the SG-23 sphere flank seed nodes. The ownership
band is the raw layer identity rule from the low-lip resting design: a different deck or rooftop never becomes a seed's
node.

### P5B-4 Float slots and water classification (#1299)

Solid and float slots are discriminated before any certification runs on a column.

- The binding's water field reports the free surface over a column at the axis. A column with no water, or an
  `Unknown` water answer, has no float slot. `Unknown` refuses every node in that column.
- A float slot exists only for SurfaceSwimmer profiles marked `Aquatic`. Its candidate pose is the runtime float pose
  under the surface (`SwimSurfaceSubmersionFraction`). It becomes a node when `SettlePlacement` from a swimming state at
  that pose returns `Advanced` with the body swimming and its Hold proof passes.
- Solid seeds under water follow P5B-3. The water policy decides footing inside `SettlePlacement`, so a WadeOnly body
  past the deep line, a DryOnly body in any water and a SurfaceSwimmer body deep enough to swim all fail footing and
  produce no ground node.
- A deep column's solid seeds below the float slot produce no node for an aquatic profile, as the legacy aquatic view
  replaces sub-surface surfaces today.

No scalar depth formula decides a non-legacy node. Legacy aquatic derivation is unchanged.

**Why:** the runtime's own full-body placement proof under the profile's water policy is the binding input for #1299.
Using `SettlePlacement` means the navigation deep line and the movement deep line are the same computation.

### P5B-5 Traversal steps the selected binding

`GroundTraversalProbe` and `SwimTraversalProbe` keep their hold, slices, budget, unit-speed probe tuning and the 3D
1 mm `GroundArrived` and `SwimArrived` rules. For a non-legacy profile they step the profile's own binding:

- Edges step the wet binding with the profile's water policy, so the WadeOnly wall and swim transitions shape edges
  exactly as at runtime. A legacy profile still steps its dry context for ground edges.
- The contact `GroundMoveContext` gains a dry counterpart for the contact constructor (same registration and boundary,
  no water field). It is used only where a legacy profile would use `DryContext`, which for non-legacy profiles is
  nowhere in this phase.
- Footprint acceptance on every slice is unchanged.
- Stair links are proved with the same probe.

### P5B-6 Explicit profiles

A PublicExplicit bake profile captures like P5B-3 through its public explicit binding, so its resting feet sit 0.001 m
above geometric support. Its edges use a bake-specific explicit traversal adapter over `ExplicitMotionProbe` that adds
capsule-area admission on every slice and the shared 3D 1 mm arrival for grounded endpoints.

`ExplicitMotionProbe` itself takes the shared 3D 1 mm predicate for grounded arrival. Swimming arrival keeps the
explicit water surface-contact tolerance, which is a different predicate. The change only refuses arrivals the old
per-axis rule accepted outside the sphere, so it tightens and never widens.

### P5B-7 KENB format 2

- An all-legacy bake writes format 1, byte for byte as today under the same engine version. A full-file golden for a
  small all-legacy fixture under the golden engine string pins it.
- Any non-legacy profile makes the bake format 2. The format 2 identity adds per profile: effective kind, foot radius
  fraction, every `WaterTraversalPolicy` field, placement offset and, for explicit profiles, the explicit fingerprint.
- The format 2 payload keeps the format 1 shared columns, then for each non-legacy profile its own columns. Each node
  stores raw seed index, geometric support height, resting feet height, headroom, areas and a float flag, followed by
  the profile's layers, exits and links as format 1 writes them.
- Load decodes format first. Versions other than 1 and 2 stay `UnsupportedFormat`. An expectation with any non-legacy
  profile against a format 1 file, or an all-legacy expectation against a format 2 file, is `ProfilesChanged`. Inside
  one format the existing order applies: engine, options, sources, profiles. Another engine's file stays
  `EngineChanged` in both formats.
- A loaded format 2 graph is rebuilt from the stored nodes, never recaptured, and rewriting it reproduces its bytes.

### P5B-8 Runtime graph compatibility

A `GroundNavigation` carries the effective identity of the profile that built it, fresh or loaded. Its
`ValidateTuning` additionally compares:

- controller and effective kind,
- `FootRadiusFraction` and placement offset,
- every `WaterTraversalPolicy` field,
- `SwimEnterDepthFraction` for a non-aquatic graph built with water facts,
- the explicit fingerprint for an explicit graph, through an overload that takes the consumer's
  `ExplicitMovementProfile`.

Mismatch throws `ArgumentException` with a message naming the differing input, as the geometry checks do.

### P5B-9 Version and placement

The phase rides staged 20.37.0, because it is ahead of v20.36.0 and its entry is untagged. All new types stay in
`KhaozEngine.Movement` beside the bake code. Movement already sees the Locomotion internals it needs. Navigation stays
free of Locomotion.

## Acceptance

Compatibility:

- The all-legacy full-file golden matches, and every existing round-trip, refusal and identity golden passes unchanged.
- Every row of the P5B-7 load matrix returns its named status.
- A contact profile and an explicit profile each round-trip: write, load and rewrite are byte-identical, and the loaded
  graph is equivalent to the fresh one.
- `Load` refuses a contact expectation against a format 1 file.

Capture:

- A low-lip bank fixture (the #1270 geometry) yields contact nodes at resting height whose Hold proof passes at the
  unchanged 1 mm rule, where the raw axis height would have moved by more.
- The SG-23 sphere flank seeds a contact node where the legacy capture has none.
- A deck over ground keeps both nodes in their own raw layers, and a seed that settles onto the deck produces no
  duplicate.
- A low roof that the body does not fit under produces no node, and a non-walkable ceiling sets headroom.

Water and #1299:

- On a pool with a deep middle, a WadeOnly contact bake marks the deep cells unwalkable and a route plans around them,
  matching where the runtime walker stops.
- A DryOnly bake has no node in any water. A SurfaceSwimmer aquatic bake has float nodes over deep water and swim edges.
- Legacy bakes over the same pool, with and without `Aquatic`, are byte-identical to today's.

Traversal and runtime:

- Contact edges are proved by stepping the contact binding, and the explicit adapter refuses an arrival the old per-axis
  rule accepted outside the 1 mm sphere.
- `GroundNavigation.ValidateTuning` refuses each P5B-8 mismatch for fresh and loaded graphs.

Cost: one cold and one warm local measurement of contact capture and contact edge proofs on the round-trip fixtures,
recorded with per-node and per-edge figures and a Hollowmere-scale extrapolation for the owner's phase 6 decision. No
load or repeat run.

## Tasks

1. Effective bake profiles and validation on every door (P5B-1), including the load gap.
2. KENB format 2 and the compatibility matrix (P5B-7), with the v1 full-file golden.
3. Contact column capture (P5B-2, P5B-3).
4. Contact traversal and the contact dry counterpart (P5B-5).
5. Float slots, water classification and the #1299 rows (P5B-4).
6. Explicit bake profiles and the arrival correction (P5B-6).
7. Runtime graph compatibility (P5B-8), cost record, documentation, changelog and full verification.
