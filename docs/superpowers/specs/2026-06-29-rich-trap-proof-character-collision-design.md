# Rich + trap-proof character collision against detailed static meshes

Target release: **8.3.0** (one combined engine release for both parts).

Two independent but thematically-linked changes let a character collide against full-detail static
geometry (buildings, trees) richly and without ever getting trapped. Both belong in the engine (the
movement resolver and the prop-collision baker), so every game with detailed building/tree statics
benefits, not just Ruinborne.

- **Part A** - swept collide-and-slide in `KhaozEngine.Locomotion/CharacterMovement.cs`, so a detailed
  one-sided building triangle mesh is rich (pillars, stairs, eaves all collide) AND can never be
  tunneled into. Includes a step-up probe (stairs/curbs walkable).
- **Part B** - `BakeTrunkHull` in `KhaozEngine.Render3D/PropCollisionBake.cs` (trunk collision follows
  the real leaning trunk instead of a vertical cylinder) + `ke-propbake` always emits a tree `.coll`.

## Problem

### A. Buildings: trapping vs richness

Town buildings bake as full-detail **one-sided** triangle meshes (`.coll`, kind 2: walls, pillars,
stairs, eaves, ~6000 verts). The current `Step(in MoveState, …)` resolves horizontal motion by
**moving the capsule to the desired position, then depenetrating** (`ComputePenetration`, up to 6
iters). There is no swept collide-and-slide for the horizontal move, so the resolver's own documented
contract - *"the swept collide-and-slide always precedes this depenetration from a known-outside
position, so the capsule never begins a tick already through a wall"* - is **not actually enforced**.
A fast move (jump, run-brush) whose one-tick displacement exceeds ~the capsule radius (0.4 m) tunnels
through a thin wall. Once inside, the inner faces generate no contacts (one-sided, can't eject) and
the outer faces shove it back in → stuck forever.

Replacing each building with the convex hull of its vertices is trap-proof but destroys collision
detail (the hull bridges to the widest points/eaves, so you can't approach walls, walk between
pillars, or use stairs). Rejected in playtest. The game wants the **full mesh** to collide AND no
trapping; only robust swept mesh collision gives both.

### B. Trees: vertical cylinder vs the real leaning trunk

`ke-propbake` bakes a tree's collision as a single **vertical** `CylinderShape`
(`BakeTrunkCylinder`), and skips thin blockers entirely so trees ship **no** `.coll` at all (the game
hand-builds a cylinder in code). Real Quaternius trees **lean**: the trunk centreline drifts
0.3-0.9 m horizontally over its height. A base-pinned vertical cylinder is not where the trunk is by
mid-height → the player walks into the visible leaning trunk; widening it just makes a fat invisible
blocker.

## Investigation findings (drive the design)

1. **`MoveTuning.StepHeight` (0.4 m) is declared but never read by `CharacterMovement`.** It is dead
   today. Stairs do not currently "just work"; with a swept resolver, riser faces become walls. So
   the stated stairs goal requires a real step-up probe that finally wires `StepHeight`.
2. **`GltfMesh` exposes no per-material submeshes** (one flattened vertex+index buffer). So Part B's
   trunk-vertex selection must use the height-cap + radial-core filter, not material filtering.
3. **`.coll` shape kinds:** `ConvexHull=1`, `TriangleMesh=2`, `Cylinder=3` (`PropCollisionFormat`).
   A trunk hull bakes as kind 1.
4. **`IPhysicsWorld.SweepCapsule` already exists** (used today only for the downward floor probe) and
   `BepuPhysicsWorld.SweepCapsule` wraps Bepu's deterministic `Sweep`. No seam change needed.
5. **Test infra:** `KhaozEngine.Tests/Physics/ControllerOnPhysicsTests.cs` constructs a real
   `BepuPhysicsWorld`, adds statics, drives `CharacterMovement.Step` over many ticks, asserts
   positions / no-penetration. Part A's new tests follow this exact pattern.
6. **The existing `Capsule_SlidesAlongObliqueWall_CorrectlyAdvances` test pins exact settled
   coordinates** (`x≈-2.066, z≈2.422`). Swapping teleport-then-depenetrate for swept
   collide-and-slide is a behavior change, so those thresholds get re-derived from the new
   implementation (intent preserved: meaningful tangential slide, no penetration).

## Part A - swept collide-and-slide resolver

Replace the teleport-then-depenetrate horizontal/vertical resolution (step 3 of the current
`Step(in MoveState, …)`) with a swept collide-and-slide from the **current** pose to the target.
Keep step 1 (camera-relative horizontal + terrain slope gate), step 2 (vertical integrate), the
terrain floor clamp, the grounded/jump determination, and the NaN/Inf defense. The depenetration loop
survives as a **settle pass** after the swept move.

### Algorithm

All over `IPhysicsWorld.SweepCapsule` (Bepu `Sweep`, deterministic single-threaded). `world == null`
keeps the terrain-only path byte-identical (early-out before any sweep).

1. **Full-3D displacement.** Compute `target = (dx, desiredY, dz)` as today (horizontal after the
   slope gate; `desiredY` after vertical integrate). Sweep `target - start`, so a fast fall onto a
   roof or a jump into an eave is caught too - not only horizontal walls. (Subsumes the
   "vertical anti-tunnel sweep can be added later" note in the current code.)
2. **Substep.** Split the displacement into `n = max(1, ceil(|d| / (CapsuleRadius * SubstepFraction)))`
   substeps (`SubstepFraction ≈ 0.5` → ≤0.2 m each). Walk/run/jump = 1 substep; near-terminal fall ≈4.
   No substep ever advances more than a fraction of the radius → no tunnel.
3. **Collide-and-slide per substep.** For up to `SlideIterations ≈ 4`:
   - `dist = |delta|`; if `dist ≤ 1e-6` stop.
   - Sweep the capsule from `pos` along `delta/dist` for `dist`.
   - **Miss:** `pos += delta`; done with this substep.
   - **Hit at distance `h`, normal `n`:** advance `pos += dir * max(0, h - SkinWidth)` (`SkinWidth ≈ 0.01`);
     compute the remaining displacement `r = delta - dir * h`; **try step-up** (below) - if it
     consumed the move, continue; else **slide**: `delta = r - dot(r, n) * n` and iterate (resolves
     inner corners where two walls meet).
4. **Step-up (included).** Only when **grounded** and the contact is near-vertical (`|n.Y|` below a
   small threshold, i.e. a wall/riser not a floor/ceiling), attempt the classic up/forward/down probe
   on the **horizontal** remainder:
   - Sweep up by `StepHeight` (abort the step-up if blocked - no headroom).
   - From the raised pose, sweep forward along the horizontal remainder.
   - Sweep down by `StepHeight`; if it lands on a walkable-slope ledge (`hit.Normal.Y` above the
     slope-gate cosine) **higher than the pre-step pos**, accept the stepped pose; else revert and
     slide. Stairs climb one riser per substep; a vertical wall has no ledge within `StepHeight`, so
     it still blocks. Step-up is prop-only (terrain is analytic), so the terrain rim stays
     un-climbable. This is the first real use of `MoveTuning.StepHeight`.
5. **Settle pass.** Keep the existing `ComputePenetration` push-out loop (6 iters, `ResolveSlop`,
   `MaxCorrection`) **after** the swept move as a residual-overlap safety net. It now provably starts
   from a known-outside pose, so the resolver's contract is finally enforced and it rarely fires.
   Re-clamp XZ to `clampXz` after it, as today.
6. **Floor / grounded.** Terrain stays the analytic floor (the `groundHeight` clamp is unchanged).
   The downward support sweep (prop tops) is retained. `propGrounded` is re-sourced from the downward
   support sweep + the final swept contact's up-facing normal, instead of the depenetration MTV (which
   now rarely fires). Jump-after-contact logic unchanged.

### Determinism

The substep count derives from a deterministic vector length; Bepu `Sweep` is deterministic
single-threaded (same guarantee `ComputePenetration` already relies on); the slide projection is plain
`float` math. Client prediction == server authority holds, so reconciliation stays a no-op
(cross-arch ULP drift remains tolerated by reconciliation as today).

### Cost

Typical 1 substep × ≤4 slide sweeps + a few step-up sweeps; worst case (terminal fall) ≈4 substeps.
A handful of broad-phase-bounded sweeps per tick - within the server tick budget.

### Tuning constants (private, drive via tests)

`SubstepFraction` (~0.5), `SlideIterations` (~4), `SkinWidth` (~0.01), step-up `|n.Y|` threshold.
`StepHeight` comes from `MoveTuning` (already 0.4 m). No public API/signature change to `Step`.

## Part B - trunk-hull baker + tool emits tree `.coll`

### `BakeTrunkHull(GltfMesh)` in `PropCollisionBake.cs`

Height-cap + running-centreline radial-core filter (no submeshes available):

1. Compute `minY/maxY`, `height`. `trunkCap = minY + min(TrunkHullMaxMeters, FoliageBaseFraction * height)`.
2. Keep verts with `y ≤ trunkCap` (drop the canopy).
3. Build a **running centreline**: bin the kept verts by height (e.g. 0.25 m bins), centroid XZ per
   bin - this tracks the lean.
4. Compute the `TrunkRadiusPercentile` percentile of each kept vert's XZ distance to its bin
   centreline → `coreRadius` (floored at `TrunkRadiusFloor`). Keep verts within
   `TrunkCoreRadiusFactor * coreRadius` of their bin centreline (drops spreading low branches).
5. **Degenerate guard:** `< 4` survivors or coplanar → `return BakeTrunkCylinder(mesh)` (no throw).
6. Otherwise `return HullFromPoints(survivors)`.

Refactor the dedup (5 mm bucket) + deterministic sort + `MaxHullPoints` cap + `ConvexHullShape`
construction out of `BakeConvexHull` into a shared `HullFromPoints(IEnumerable<Vector3>)` used by both
`BakeConvexHull` (whole mesh) and `BakeTrunkHull` (filtered verts), so streaming-consistent
determinism is single-sourced.

`Bake` dispatch: `IsTree → BakeTrunkHull` (was `BakeTrunkCylinder`). `IsBuilding`/rock paths unchanged.

New constants: `TrunkHullMaxMeters` (~3.0 m), `FoliageBaseFraction` (~0.5), `TrunkCoreRadiusFactor`
(~1.6). Reuse `TrunkRadiusPercentile`, `TrunkRadiusFloor`. `BakeTrunkCylinder` is kept as the
degenerate fallback.

### `PropBakePlan.For(GltfMesh)` (testable tool decision, in Render3D)

Extract the per-prop bake decision into a small pure helper so the "tree → coll, no surf" rule is unit-
tested without a glTF fixture and the tool stays thin:

```
readonly record struct PropBakePlan(PhysicsShape Coll, PropSurface? Surface);
static PropBakePlan For(GltfMesh mesh) =>
    new(PropCollisionBake.Bake(mesh),
        PropSurfaceBake.IsWalkableSolid(mesh) ? PropSurfaceBake.Bake(mesh) : null);
```

### Tool (`PropSurface.Tool/Program.cs`)

Decouple the bakes:
- **Always** write `<id>.coll` (via `PropBakePlan.For`) and stamp `node["collisionShape"]` for every prop.
- Only write `<id>.surf` + stamp `surface: true`/`heightmap` when `plan.Surface is not null`
  (`IsWalkableSolid`) - unchanged for buildings/rocks/logs.
- Accurate kind label: `cylinder` / `convex-hull` / `triangle-mesh`.
- A tree prints e.g. `+ pine_a: baked pine_a.coll (convex-hull) [thin blocker, no surface]`; a
  walkable-solid prints the surf + coll line as today. Final summary counts both.

## Tests (headless, xUnit - engine convention)

### Part A (real `BepuPhysicsWorld`, `ControllerOnPhysicsTests` pattern)
- **No tunnel through a thin one-sided wall:** capsule driven at jump/run speed straight at a 0.1 m
  one-sided quad (`TriangleMeshShape`) ends OUTSIDE, never past it.
- **No trap in a closed shell:** drive hard at a closed one-sided box (triangle mesh, stand-in
  building); never ends inside. Seeded-inside (legacy) variant asserts the weaker "not hard-locked"
  property (one-sided inner faces give no contacts, so full ejection isn't guaranteed; the swept move
  guarantees you can never get in going forward).
- **Collide-and-slide:** diagonal into a wall keeps the tangential component, no penetration.
- **Inner corner:** two walls at 90° stop the capsule in the corner, no jitter/penetration.
- **Stairs walkable:** a stepped triangle mesh with risers `< StepHeight` → capsule Y climbs each step.
- **Determinism:** two independent worlds resolve an identical fast path to a byte-identical end pose.
- **Regressions stay green / re-derived:** dome rest/mount/base, doorway, wall-block, null-world keep
  passing; `Capsule_SlidesAlongObliqueWall_CorrectlyAdvances` thresholds re-derived from the swept
  implementation (intent unchanged).

### Part B (headless)
- Synthetic leaning-trunk+canopy mesh → assert a `ConvexHullShape`; hull points at low height offset
  toward the lean (track the trunk, not a vertical axis); NO hull point above the height cap (canopy
  excluded).
- Wide low branches excluded: hull XZ extent at branch height stays near the trunk core radius.
- Trunk hull is solid (no trap): a capsule overlapping the hull is pushed out radially
  (`ComputePenetration` via Bepu).
- Degenerate input → falls back to `BakeTrunkCylinder` without throwing.
- `PropBakePlan.For`: a thin-blocker tree mesh → `Coll` is a hull and `Surface` is null; a
  walkable-solid (rock) mesh → `Coll` set and `Surface` non-null.

## Release & doc sweep (8.3.0)

One worktree `feature/swept-character-collision`; one bump of `<KhaozEngineVersion>` to **8.3.0** with
a `CHANGELOG.md` entry (newest-first, one-line digest first sentence; note the **behavior change** for
all NetWorld/Simulation consumers in Part A and the additive tree `.coll` in Part B). Update the three
guard-checked declarations (`docs/CONSUMERS.md` engine version, `docs/ROADMAP.md` current version,
`README.md` `<PackageReference>` example). Full doc sweep:

- `CLAUDE.md` package map - Locomotion (swept resolver + step-up, `StepHeight` now wired), Render3D
  `PropCollisionBake` (trunk hull + `PropBakePlan`), `PropSurface.Tool` (emits tree `.coll`).
- Per-package READMEs that ship in the nupkg: `KhaozEngine.Locomotion/README.md`,
  `KhaozEngine.Render3D/README.md`.
- `docs/USING-KHAOZENGINE.md` (movement + baker sections).
- Mechanical check: grep the new names (`BakeTrunkHull`, `PropBakePlan`, swept / step-up wording)
  across **all** `*.md` + `CLAUDE.md`; confirm nothing still describes the old vertical-cylinder /
  teleport-then-depenetrate behavior.

`dotnet pack -c Release -o ./local-feed`; commit; `git tag v8.3.0`. Push + tag are **held/batched** -
confirm with the user before pushing (engine policy).

## Out of scope / downstream

- **Ruinborne adoption** (separate game chat): drop `RuinbornePhysics.Solidify` (buildings load raw
  triangle-mesh `.coll` again - full detail, now trap-proof), drop `RuinbornePhysics.TreeCollisionShape`
  (trees load the trunk-hull `.coll` like rocks), re-bake the 5 trees with `ke-propbake`, move both
  items to "Adopted" in `docs/ENGINE-ADOPTION.md`. The engine chat can hand back the 5 baked tree
  `.coll` if running the tool from Ruinborne is awkward.
- Dynamic bodies, ledge-grab, and animation are unaffected.
