# Orbit Camera Look-Up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `FollowCamera3D` can orbit a pivot above its target, look up from below the horizon, and be stopped by any boom probe, with a tile-world probe that stops it at terrain and filtered objects.

**Architecture:** Opt-in members on `FollowCamera3D`, all defaulting to today's geometry. The boom logic moves into a new partial file behind an `ICameraBoomProbe` seam that the existing physics sweep is adapted into. `TileWorldCameraProbe` in `KhaozEngine.TileWorld.Render3D` answers the seam from `TileWorldView` picking.

**Tech Stack:** C# / .NET, System.Numerics, xUnit.

**Spec:** `docs/design/ORBIT-CAMERA-LOOK-UP-DESIGN-2026-09-27.md`

**Worktree:** `/Users/antonio/KhaozEngine/.worktrees/orbit-camera-look-up`, branch `feature/orbit-camera-look-up`. Every command below runs from that directory.

## Global Constraints

- Every new member defaults to today's behaviour. Existing `FollowCamera3D*Tests` pass unchanged.
- `PitchLimit` is 85 degrees, `85f * MathF.PI / 180f`.
- The camera speaks absolute world coordinates. A probe over a rebased world converts on its own side.
- Warnings are errors. No blanket suppressions.
- Test namespaces stay `KhaozEngine.Tests.*`. Camera and tile-world tests live in `KhaozEngine.Render.Tests`.
- New files stay under the 800-line cap (`scripts/check-file-size.sh`).
- No em dashes, en dashes, or prose semicolons in docs or comments (`scripts/check-dashes.sh`, `scripts/check-prose.sh`).
- Commit subjects use `area(scope): summary`. Stage explicit paths.
- This work rides the staged, untagged 20.13.0. No version bump, no tag.

## Review Focus

- A subject pressed against a wall: the pivot sits inside the wall's radius-inflated box and must not collapse its own boom. Pinned in Task 4.
- A zoom during recovery must neither invert the boom nor lurch it to the minimum distance. The `Distance` setter drops the held shortfall, and a real obstruction re-imposes itself on the next read. Pinned in Task 3.
- A teleport (`Warp`) mid-recovery must not ease out from the old site. Pinned in Task 3.
- Repeated reads in one frame with recovery on must cost one probe call, because the held shortfall mutates inside the computation. Pinned in Task 3.
- A roof on the plane above is hidden only by the view's rule. The probe must ask the view, not the roof mode alone. Pinned in Task 4.

---

### Task 1: Pivot height and signed pitch

**Files:**
- Modify: `KhaozEngine.Render3D/Camera/FollowCamera3D.cs` (class becomes `partial`, docs at 9, 15, 90-99 and 121, `EyeInputs` 204-207, `CurrentEyeInputs` 272-275, `ComputeEye` 278-310, `Pitch` setter 149, `Forward` 312, `View` 319, `AbsoluteViewProjection` 324-325)
- Test: `KhaozEngine.Render.Tests/Render3D/FollowCamera3DTests.cs`, `KhaozEngine.Render.Tests/Render3D/FollowCamera3DEyeCacheTests.cs`

**Interfaces:**
- Produces: `public float PivotHeight = 0f;`, `public Vector3 Pivot { get; }` (`EffectiveTarget + (0, PivotHeight, 0)`), `public const float PitchLimit = 85f * MathF.PI / 180f;`. `FollowCamera3D` is `public sealed partial class`.

- [ ] **Step 1: Bring the branch up to main**

Run: `git fetch origin && git merge --no-edit origin/main`
Expected: clean merge. `Directory.Build.props` reads `<KhaozEngineVersion>20.13.0</KhaozEngineVersion>` and `CHANGELOG.md` starts with `## 20.13.0`.

- [ ] **Step 2: Write the failing tests**

In `FollowCamera3DTests.cs`:

```csharp
[Fact]
public void Pivot_height_zero_reproduces_the_look_at_target_geometry()
{
    // Old formula inline: eye = target + dirToEye * d + (0, h, 0), look-at = target.
    foreach (var (yaw, pitch, d, h) in new[] { (0f, 0.5f, 8f, 1f), (1.3f, 1.1f, 12f, 1.2f), (-2f, 0.2f, 4f, 0f) })
    {
        var cam = new FollowCamera3D { Target = new Vector3(3f, 0.5f, -7f), Yaw = yaw, HeightOffset = h };
        cam.Pitch = pitch; cam.Distance = d;
        Vector3 dir = Vector3.Normalize(new(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)));
        Vector3 eye = cam.Target + dir * d + new Vector3(0f, h, 0f);
        Assert.Equal(eye, cam.Eye);
        Assert.Equal(Matrix4x4.CreateLookAt(eye, cam.Target, Vector3.UnitY), cam.View);
    }
}

[Fact]
public void Pivot_height_lifts_the_orbit_centre_and_the_look_at_point_together()
{
    var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, PivotHeight = 1.5f };
    cam.Pitch = 0f; cam.Distance = 10f;
    Assert.Equal(new Vector3(0f, 1.5f, 0f), cam.Pivot);
    Assert.True(Vector3.Distance(cam.Eye, new Vector3(0f, 1.5f, 10f)) < 1e-4f, cam.Eye.ToString());
    Assert.True(Vector3.Distance(cam.Forward, new Vector3(0f, 0f, -1f)) < 1e-4f, cam.Forward.ToString());
}

[Fact]
public void Negative_pitch_puts_the_eye_under_the_pivot_looking_up()
{
    var cam = new FollowCamera3D { Target = Vector3.Zero, HeightOffset = 0f, PivotHeight = 1.5f, MinPitch = -1.4f };
    cam.Pitch = -0.5f; cam.Distance = 4f;
    Assert.Equal(1.5f - 4f * MathF.Sin(0.5f), cam.Eye.Y, 4);
    Assert.Equal(MathF.Sin(0.5f), cam.Forward.Y, 4);
}

[Fact]
public void The_pitch_limit_holds_whatever_the_stops_allow()
{
    var cam = new FollowCamera3D { MinPitch = -2f, MaxPitch = 2f };
    cam.Pitch = -2f;
    Assert.Equal(-FollowCamera3D.PitchLimit, cam.Pitch);
    cam.Pitch = 2f;
    Assert.Equal(FollowCamera3D.PitchLimit, cam.Pitch);
}
```

In `FollowCamera3DEyeCacheTests.cs`, add `("PivotHeight", c => c.PivotHeight += 0.5f)` to the knob list of the every-knob-invalidates test (158-193).

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~FollowCamera3D"`
Expected: compile failure on `PivotHeight`, `Pivot` and `PitchLimit`.

- [ ] **Step 4: Implement**

- Make the class `public sealed partial class FollowCamera3D`.
- Add `PivotHeight`, `Pivot` and `PitchLimit` as in the Interfaces block. `PivotHeight` joins `EyeInputs` and `CurrentEyeInputs`.
- `ComputeEye` builds the geometric eye from `Pivot` and sweeps from `Pivot` instead of `EffectiveTarget`. `Forward`, `View` and `AbsoluteViewProjection` look at `Pivot`.
- The `Pitch` setter becomes `Math.Clamp(Math.Clamp(value, MinPitch, MaxPitch), -PitchLimit, PitchLimit)`.
- Docs: the class summary says the camera looks at the pivot. `HeightOffset` says it raises the eye only and never moves the look-at point. `MinPitch` drops "kept > 0" and says a negative value looks up from below the pivot, bounded by `PitchLimit`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~FollowCamera3D|FullyQualifiedName~FollowCameraController"`
Expected: all pass, including every pre-existing test.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Camera/FollowCamera3D.cs KhaozEngine.Render.Tests/Render3D/FollowCamera3DTests.cs KhaozEngine.Render.Tests/Render3D/FollowCamera3DEyeCacheTests.cs
git commit -m "render3d(camera): orbit a pivot above the target and allow signed pitch"
```

---

### Task 2: Boom probe seam

**Files:**
- Create: `KhaozEngine.Render3D/Camera/ICameraBoomProbe.cs`
- Create: `KhaozEngine.Render3D/Camera/PhysicsBoomProbe.cs`
- Create: `KhaozEngine.Render3D/Camera/FollowCamera3D.Boom.cs`
- Modify: `KhaozEngine.Render3D/Camera/FollowCamera3D.cs` (`ComputeEye` delegates the boom, `EyeInputs` gains `BoomProbe`)
- Create: `KhaozEngine.Render.Tests/Render3D/CountingPhysicsWorld.cs` (moved out of `FollowCamera3DEyeCacheTests.cs:33-83`, `internal sealed`)
- Create: `KhaozEngine.Render.Tests/Render3D/FixedReachProbe.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/FollowCamera3DBoomProbeTests.cs`, `FollowCamera3DEyeCacheTests.cs`

**Interfaces:**
- Consumes: `Pivot` from Task 1.
- Produces:
  - `public interface ICameraBoomProbe { float Reach(Vector3 origin, Vector3 direction, float length, float radius); }` in namespace `KhaozEngine.Render3D`. `direction` is unit length. The return is the free length in `[0, length]`.
  - `internal sealed class PhysicsBoomProbe(IPhysicsWorld world) : ICameraBoomProbe`, which returns `hit.Distance` on a `SweepCapsule(new CapsuleShape(radius, 0f), Pose.At(origin - world.Origin), direction, length, out hit, QueryFilter.StaticsOnly)` hit, else `length`.
  - `public ICameraBoomProbe? BoomProbe;` and `public long BoomProbeCount { get; }` on `FollowCamera3D`.
  - A private `Vector3 ConstrainBoom(Vector3 pivot, Vector3 geometricEye)` in `FollowCamera3D.Boom.cs`, which Task 3 extends.
  - Test helper `internal sealed class FixedReachProbe : ICameraBoomProbe` with `public float? ReachAt` (null means full length), `public int Calls`, and `LastOrigin`, `LastDirection`, `LastLength`, `LastRadius`.

- [ ] **Step 1: Move `CountingPhysicsWorld` to its own test file and add `FixedReachProbe`**

`CountingPhysicsWorld` keeps its members and behaviour. `FollowCamera3DEyeCacheTests` uses the shared type.

- [ ] **Step 2: Write the failing tests** in `FollowCamera3DBoomProbeTests.cs`. Every test uses `Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f`, `Pitch = 0f` and `Distance = 10f` unless it says otherwise.

- `A_probe_reach_short_of_the_boom_pulls_the_eye_in_by_the_skin`: `ReachAt = 6f`, so `cam.Eye` is within `1e-4f` of `(0, 0, 6f - cam.OcclusionSkin)`.
- `The_probe_is_asked_from_the_pivot_along_the_boom_with_the_occlusion_radius`: with `PivotHeight = 1.5f`, after reading `Eye`, `LastOrigin == (0, 1.5f, 0)`, `LastDirection` is within `1e-5f` of `(0, 0, 1)`, `LastLength` is `10f` to 4 places, and `LastRadius == cam.OcclusionRadius`.
- `A_full_length_reach_leaves_the_eye_alone`: `ReachAt = null`, so `Eye == (0, 0, 10)`.
- `The_pull_in_is_floored_at_the_minimum_occlusion_distance`: `ReachAt = 0f`, so `Vector3.Distance(Eye, Pivot)` is `cam.MinOcclusionDistance` to 4 places.
- `A_blocked_boom_below_the_horizon_slides_in_and_keeps_looking_up`: `MinPitch = -1.4f`, `Pitch = -1.0f`, `PivotHeight = 1.5f`, `ReachAt = 1.2f`. The eye is within `1e-4f` of `Pivot + dir * (1.2f - OcclusionSkin)`, where `dir` is the unit boom direction, and `cam.Forward` is within `1e-4f` of `-dir`, with `Forward.Y > 0`.
- `Probe_and_physics_together_take_the_shorter_reach`: `CountingPhysicsWorld { WallDistance = 6.25f }` as `Occlusion` with `ReachAt = 4f`. The eye sits at `4f - OcclusionSkin`. Swapping to `ReachAt = 8f` puts it at `BoomAfterPullIn(cam, 6.25f)`.
- `Boom_probe_count_is_one_per_frame_across_repeated_reads`: after `BeginFrame` and 33 reads (the `ReadEveryEyePath` pattern), `probe.Calls == 1` and `cam.BoomProbeCount == 1L`. With no probe, `BoomProbeCount` stays `0L`.

In `FollowCamera3DEyeCacheTests.cs`, add `("BoomProbe", c => c.BoomProbe = new FixedReachProbe { ReachAt = 3f })` to the knob list.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~FollowCamera3D"`
Expected: compile failure on `ICameraBoomProbe` and `BoomProbe`.

- [ ] **Step 4: Implement**

- `ComputeEye` computes `pivot` and the geometric eye, then calls `ConstrainBoom`, then applies the `GroundHeight` lift unchanged.
- `ConstrainBoom`:
  - Returns the geometric eye when neither `Occlusion` nor `BoomProbe` is set, or when the boom length is at most `1e-6f`.
  - Otherwise `reach` is the minimum of the physics adapter's reach (counted in `OcclusionSweepCount`) and `BoomProbe.Reach` (counted in `BoomProbeCount`), each called with `pivot`, the unit direction, the full length and `OcclusionRadius`.
  - When `reach < full` the eye is `pivot + dir * MathF.Max(MinOcclusionDistance, reach - OcclusionSkin)`.
- The physics adapter is cached per `Occlusion` reference, so a frame allocates nothing.
- `BoomProbe` joins `EyeInputs`.
- Docs on `Occlusion` say it now runs through the same boom path as `BoomProbe`, and the shorter reach wins.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~FollowCamera3D|FullyQualifiedName~FollowCameraController"`
Expected: all pass. The pre-existing occlusion and eye-cache tests are unchanged and green.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Camera/ICameraBoomProbe.cs KhaozEngine.Render3D/Camera/PhysicsBoomProbe.cs KhaozEngine.Render3D/Camera/FollowCamera3D.Boom.cs KhaozEngine.Render3D/Camera/FollowCamera3D.cs KhaozEngine.Render.Tests/Render3D/CountingPhysicsWorld.cs KhaozEngine.Render.Tests/Render3D/FixedReachProbe.cs KhaozEngine.Render.Tests/Render3D/FollowCamera3DBoomProbeTests.cs KhaozEngine.Render.Tests/Render3D/FollowCamera3DEyeCacheTests.cs
git commit -m "render3d(camera): stop the boom through an ICameraBoomProbe seam"
```

---

### Task 3: Eased boom recovery

**Files:**
- Modify: `KhaozEngine.Render3D/Camera/FollowCamera3D.Boom.cs`, `KhaozEngine.Render3D/Camera/FollowCamera3D.cs` (`Warp` 76-81, the `Eye` getter's cache store, `EyeInputs`), `KhaozEngine.Render3D/Camera/FollowCameraController.cs:67`
- Test: `KhaozEngine.Render.Tests/Render3D/FollowCamera3DBoomRecoveryTests.cs`, `KhaozEngine.Render.Tests/Render3D/FollowCameraControllerTests.cs`

**Interfaces:**
- Consumes: `ConstrainBoom`, `FixedReachProbe` from Task 2.
- Produces: `public float BoomRecoveryRate = 0f;` and `public void AdvanceBoom(float dt)` on `FollowCamera3D`. `FollowCameraController.Update` calls `Camera.AdvanceBoom(dt)` after `AdvanceTarget`.

- [ ] **Step 1: Write the failing tests** in `FollowCamera3DBoomRecoveryTests.cs`. Use the Task 2 framing, with a `FixedReachProbe` as `BoomProbe` and `OcclusionSkin` at its default 0.05.

- `Rate_zero_follows_the_probe_both_ways_at_once`: `ReachAt = 4f` gives an eye length of 3.95. After `ReachAt = null`, `AdvanceBoom(1f / 60f)` and `BeginFrame()`, the length is 10.
- `A_pull_in_is_instant_with_recovery_on`: `BoomRecoveryRate = 4f` and `ReachAt = 4f`. The first read gives length 3.95.
- `Recovery_eases_out_after_the_obstruction_clears`: after that pull-in, set `ReachAt = null`, call `AdvanceBoom(0.1f)` and `BeginFrame()`. The length is `10f - 6.05f * MathF.Exp(-0.4f)` to 4 places.
- `Recovery_matches_across_frame_rates`: from the same pull-in on two cameras, one takes 60 steps of `AdvanceBoom(1f / 60f)` with a read each step, and the other takes 30 steps of `1f / 30f`. Final lengths agree within `1e-4f`.
- `Zoom_stays_instant_while_unobstructed`: `BoomRecoveryRate = 4f`, `ReachAt = null`. `Distance = 5f` gives length 5 on the next read, and `Distance = 15f` gives 15.
- `A_zoom_drops_the_held_shortfall`: pull in at `ReachAt = 2f` (held 8.05), clear the probe, then set `Distance = 2f`. The next read gives length 2. With the probe still at `ReachAt = 1f` instead, `Distance = 2f` gives the pull-in length 0.95 on the next read.
- `Warp_clears_the_held_shortfall`: after a pull-in, clear the probe and call `Warp(new Vector3(50f, 0f, 0f))`. The next read gives length 10.
- `Recovery_costs_one_probe_call_per_frame`: after `BeginFrame` with recovery on and a pull-in, 33 reads leave `probe.Calls == 1`.

In `FollowCameraControllerTests.cs`, add `Update_advances_boom_recovery`: with recovery on after a pull-in, clearing the probe and calling `ctl.Update(Frame(), 0.1f)` once moves the length strictly between 3.95 and 10.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~BoomRecovery|FullyQualifiedName~FollowCameraController"`
Expected: compile failure on `BoomRecoveryRate` and `AdvanceBoom`.

- [ ] **Step 3: Implement**

`ConstrainBoom` keeps a `float _heldShortfall`. After computing `length` (full, or the floored pull-in), when `BoomRecoveryRate > 0f`:

```csharp
float shortfall = full - length;
if (shortfall > _heldShortfall) _heldShortfall = shortfall;
length = MathF.Max(MathF.Min(MinOcclusionDistance, full), full - _heldShortfall);
```

The held path runs whenever `_heldShortfall > 0f`, including frames where the probe reports full reach.

- `AdvanceBoom(dt)`:
  - Zeroes `_heldShortfall` when the rate is not finite and positive.
  - Returns when `dt <= 0f`.
  - Otherwise multiplies by `MathF.Exp(-BoomRecoveryRate * dt)` and zeroes values below `1e-4f`.
- `Warp` and the `Distance` setter zero `_heldShortfall`. `SnapToTarget` already routes through `Warp`. Zoom is always instant, and a real obstruction re-imposes itself on the next read.
- `_heldShortfall` and `BoomRecoveryRate` join `EyeInputs`.
- The `Eye` getter captures `_eyeInputs = CurrentEyeInputs()` after `ComputeEye`, not before. A shortfall raised inside the computation must not force a second computation in the same frame.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~FollowCamera3D|FullyQualifiedName~FollowCameraController"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Camera/FollowCamera3D.Boom.cs KhaozEngine.Render3D/Camera/FollowCamera3D.cs KhaozEngine.Render3D/Camera/FollowCameraController.cs KhaozEngine.Render.Tests/Render3D/FollowCamera3DBoomRecoveryTests.cs KhaozEngine.Render.Tests/Render3D/FollowCameraControllerTests.cs
git commit -m "render3d(camera): ease the boom back out after an obstruction clears"
```

---

### Task 4: Tile-world camera probe

**Files:**
- Create: `KhaozEngine.TileWorld.Render3D/TileWorldCameraProbe.cs` (namespace `KhaozEngine.TileWorld.Render3D`)
- Modify: `KhaozEngine.TileWorld.Render3D/TileWorldView.Picking.cs` (add `internal TileWorldDocument Document => _doc;` and `internal TileWorldCatalogs Catalogs => _catalogs;`)
- Test: `KhaozEngine.Render.Tests/TileWorld/TileWorldCameraProbeTests.cs` (namespace `KhaozEngine.Tests.TileWorld`)

**Interfaces:**
- Consumes: `ICameraBoomProbe` from Task 2. These existing view members:
  - `TileWorldView.PickSurface(int plane, Vector3 origin, Vector3 direction, float maxDistance)`
  - `TileWorldView.PickObjects(int plane, Vector3 origin, Vector3 direction, float maxDistance, TileObjectRaycast.BoundsSource bounds, List<TileObjectHit> hits)`
  - `TileWorldView.IsRoofHidden(TileRect footprint, int plane)`
  - `TileWorldView.Observer`
  - `TileFootprint.Of(archetype, x, z, rotation)`
- Produces: `public sealed class TileWorldCameraProbe : ICameraBoomProbe` with constructor `TileWorldCameraProbe(TileWorldView view, TileObjectRaycast.BoundsSource bounds, Func<TileObjectArchetype, bool> blocks)`. Grimhollow consumes it.

- [ ] **Step 1: Write the failing tests**

**Setup:**
- Build the view as `new TileWorldView(new RecordingTileWorldScene(), doc, TileRenderTestData.Catalogs, resolver)` with `resolver = new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight)`, then call `view.LoadRegion(TileRenderTestData.Region)`.
- `bounds = new TileObjectBoundsCache(resolver).TryGetBounds`, `radius = 0.25f`, `length = 10f`.
- Coordinates: world x = tile x, and world z = -tile z.
- Read `TileRenderTestData.AddHouse` (209-233) for the wall rows and the greybox wall slab (`TileMeshResolver.cs` 70-100, 190-191) before fixing positions.

**Tests:**
- `Open_ground_gives_the_full_length`: `HouseWorld()`, origin `(40.5f, 1.5f, -40.5f)`, direction `Normalize(0, 0.5, 1)`, `blocks = _ => true`. Reach is `length`.
- `A_boom_below_the_horizon_stops_short_of_the_ground_by_the_radius`: same origin, direction `(0, -MathF.Sin(1f), MathF.Cos(1f))`. Reach is `1.5f / MathF.Sin(1f) - radius` within `1e-3f`, over ground authored at height 0.
- `Rising_terrain_shortens_the_reach`: `HillWorld()`, an origin at 1 m on the flat and a direction into the raised corners 20..22. Reach equals `view.PickSurface(0, origin, dir, length)!.Value.Distance - radius` within `1e-4f`, and is `< length`.
- `A_walk_surface_stops_the_boom`: use the bridge archetype setup from `TileWorldViewSurfacePickTests.cs:306-319`, aimed down at the deck from above. Reach equals the `PickSurface` distance minus radius, and is shorter than the distance to the ground under the deck.
- `Water_stops_the_boom`: `RiverWorld()`, aimed down into the channel at x 30..32. Reach equals the `PickSurface` distance minus radius, and is shorter than the distance to the -0.7 m bed.
- `A_wall_the_filter_accepts_stops_the_boom`: `HouseWorld()`, origin `(11.5f, 1.5f, -10.5f)`, direction `(-1, 0, 0)`, `blocks = a => a.Id == "wall"`. Reach equals `origin.X - (westWallEastFace + radius)` within `1e-3f`.
- `A_wall_the_filter_rejects_does_not_stop_the_boom`: same ray with `blocks = _ => false`. Reach is `length`.
- `A_visible_roof_on_the_plane_above_stops_the_boom`: `RoofMode = RoofVisibility.AlwaysVisible`, origin `(11.5f, 1.5f, -10.5f)`, direction `(0, 1, 0)`, `blocks = a => a.IsRoof`. Reach is `doc.PlaneHeight - 1.5f - radius` within `1e-3f`.
- `A_hidden_roof_never_stops_the_boom`: the same ray gives `length` under two setups:
  - `RoofMode = Interior` with `Observer = new TileCoord(11, 10, 0)`, which is indoors.
  - `RoofMode = AlwaysHidden` with `Observer = new TileCoord(40, 40, 0)`.
- `A_box_containing_the_origin_is_skipped`: origin 0.1 m east of the west wall's east face at y 1.5, direction `(0, 1, 0)`, `blocks = a => a.Id == "wall"`. Reach is `length`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~TileWorldCameraProbe"`
Expected: compile failure on `TileWorldCameraProbe`.

- [ ] **Step 3: Implement `Reach(Vector3 origin, Vector3 direction, float length, float radius)`**

**Terrain:**
- `plane = view.Observer.Plane`.
- `view.PickSurface(plane, origin, direction, length)` gives a candidate of `MathF.Max(0f, hit.Distance - radius)`.

**Objects:**
- Search the observer's plane and `plane + 1` when `plane + 1 < view.Document.PlaneCount`, using `view.PickObjects` with an inflating bounds delegate. That delegate is cached once and reads a `_radius` field set per call. It calls the wrapped source, then grows `min` and `max` by `radius` on every axis.
- Walk hits nearest first:
  - Skip `Distance <= 0f`, because the origin is inside the box.
  - Resolve `view.Document.FindObject(hit.ObjectId)` and `view.Catalogs.Archetype(o.ArchetypeId)`. Skip on null or `!blocks(archetype)`.
  - Skip `archetype.IsRoof && view.IsRoofHidden(TileFootprint.Of(archetype, o.X, o.Z, o.Rotation), o.Plane)`.
  - The first surviving hit is the object candidate for that plane.

**Result:**
- Return the minimum of `length` and every candidate.
- Reuse one `List<TileObjectHit>` field. Nothing allocates per call.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~TileWorldCameraProbe|FullyQualifiedName~TileWorldView|FullyQualifiedName~TileObjectRaycast"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.TileWorld.Render3D/TileWorldCameraProbe.cs KhaozEngine.TileWorld.Render3D/TileWorldView.Picking.cs KhaozEngine.Render.Tests/TileWorld/TileWorldCameraProbeTests.cs
git commit -m "tileworld(camera): stop the camera boom at terrain and filtered objects"
```

---

### Task 5: Docs sweep, changelog and full verification

**Files:**
- Modify: `CHANGELOG.md` (append to the existing `## 20.13.0` entry), `docs/USING-KHAOZENGINE.md` (follow camera section, find by the heading `## Third-person follow camera + character controller`), `KhaozEngine.Render3D/README.md` (camera bullets near 20-30), `KhaozEngine.TileWorld.Render3D/README.md` (new `### Camera boom probe (\`TileWorldCameraProbe\`)` after `### Picking the models`), `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileSteeringTests.cs:17-18` (the comment citing `FollowCamera3D.cs:319` now says the view looks at `Pivot`)

- [ ] **Step 1: Write the changelog bullets under `## 20.13.0`**

- `FollowCamera3D.PivotHeight` lifts the orbit centre and look-at point above the target. `HeightOffset` still raises only the eye. A negative `MinPitch` now looks up from below the pivot, bounded by `PitchLimit` (85 degrees).
- `ICameraBoomProbe` and `FollowCamera3D.BoomProbe` stop the boom through any probe. The `Occlusion` physics sweep runs through the same path, the shorter reach wins, and `BoomProbeCount` counts probe calls.
- `FollowCamera3D.BoomRecoveryRate` and `AdvanceBoom` pull the boom in at once and ease it back out, while zoom stays instant. `FollowCameraController.Update` advances it.
- `TileWorldCameraProbe` stops a camera boom at drawn terrain, water and walk surfaces, and at objects a consumer filter accepts on the observer's plane and the one above. It skips roofs the view hides.

Each bullet links the design doc. All defaults keep existing cameras unchanged.

- [ ] **Step 2: Sweep the living docs**

Add pivot, signed pitch, boom probe and recovery paragraphs to the USING follow camera section beside the occlusion spring-arm paragraph. Update the Render3D README camera bullets and add the TileWorld.Render3D README subsection.

Run: `git grep -nw -e "kept > 0" -e "looking at Target" -e "CreateLookAt(Eye, Target" -- '*.md' '*.cs'`
Expected: no stale hit remains.

- [ ] **Step 3: Run the full verification**

```bash
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh
sh scripts/check-file-size.sh
bash scripts/check-doc-versions.sh
```

Expected: 0 warnings, all tests pass, and every check passes.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md docs/USING-KHAOZENGINE.md KhaozEngine.Render3D/README.md KhaozEngine.TileWorld.Render3D/README.md KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileSteeringTests.cs
git commit -m "docs(camera): document the pivot, boom probe and tile-world probe"
```

## Integration (orchestrator only)

1. `git fetch origin`, merge `origin/main` into the branch again, and rerun Task 5 Step 3.
2. From the main checkout, fast-forward `main` to the branch and push `main`.
3. Once `origin/main` contains the commit, run `scripts/pack-local-feed.sh` from `main`, so the staged 20.13.0 in `local-feed` carries the camera API.
4. Stop. The `v20.13.0` tag is the owner's release decision. 20.13.0 also carries the skinned body foundation from another branch, so the owner decides when that release is due. Grimhollow's plan waits for the tag.
