# Orbit camera look-up and boom collision

Status: approved by the owner on 2026-09-27. Ships in 20.13.0. Consumer: Grimhollow camera look-up
([`CAMERA-LOOK-UP-DESIGN-2026-09-27.md`](https://github.com/APKiwiOrg/Grimhollow/blob/main/docs/design/CAMERA-LOOK-UP-DESIGN-2026-09-27.md)
in that repository).

## Problem

`FollowCamera3D` always looks down at its target. Three things stop a consumer from bringing the camera down to
the ground and looking up at the sky.

1. The look-at point is the target itself. `HeightOffset` raises only the eye, so a close camera stares at the
   subject's feet.
2. The only terrain response, `GroundHeight`, lifts the eye straight up. Below the horizon that flattens the view
   instead of tilting it, so the sky can never fill the frame.
3. The only occluder seam, `Occlusion`, is an `IPhysicsWorld` sweep. A tile-world client has no physics world, so
   its walls and roofs cannot stop the boom and the camera passes through them.

## Decision

Extend `FollowCamera3D` with opt-in members and add a tile-world implementation of a new boom probe seam. Every
new member defaults to today's behaviour. A camera that never touches them produces the same `Eye`, `View` and
`Forward` bit for bit, so Ruinborne and the showcase are unaffected.

### Pivot

`PivotHeight` (float, default 0) lifts the orbit centre above the target. The pivot is
`EffectiveTarget + (0, PivotHeight, 0)`. The eye is `pivot + dirToEye * Distance + (0, HeightOffset, 0)`, and
`View` and `Forward` look at the pivot. `HeightOffset` keeps its meaning and its docs say so plainly: it raises the
eye only and never moves the look-at point. A consumer that wants the camera to orbit a head sets `PivotHeight`
and leaves `HeightOffset` at zero.

### Signed pitch

A negative `MinPitch` is supported. Below zero the eye sits under the pivot and the view looks up. The `Pitch`
setter keeps its clamp to `[MinPitch, MaxPitch]` and adds a clamp to `[-PitchLimit, PitchLimit]`, where the
new public constant `PitchLimit` is 85 degrees, so `LookAt` against world up never degenerates. The engine
default `MaxPitch` is 81 degrees, so a camera at the default never reaches the new limit, and a `MaxPitch` above
85 degrees is capped there. The `MinPitch` doc stops claiming the value is kept above zero.

### Boom probe seam

```csharp
public interface ICameraBoomProbe
{
    /// Free length of the boom from origin along a unit direction, at most length, for a sphere of radius.
    float Reach(Vector3 origin, Vector3 direction, float length, float radius);
}
```

The interface lives in `KhaozEngine.Render3D` beside the camera. Coordinates are absolute world, as everywhere on
the camera. A probe over a rebased world converts on its own side, as the physics path already does with
`IPhysicsWorld.Origin`.

`FollowCamera3D.BoomProbe` (default null) takes one. The existing `Occlusion` sweep becomes an internal
`PhysicsBoomProbe` adapter so both run through one code path. When both are set, the shorter reach wins. The
probe runs from the pivot toward the geometric eye. A reach short of the full boom places the eye at
`pivot + dir * max(MinOcclusionDistance, reach - OcclusionSkin)`, using the existing `OcclusionRadius`,
`OcclusionSkin` and `MinOcclusionDistance` for both paths. `GroundHeight` clearance still runs last as the final
guard.

The eye cache adds `BoomProbe` to its key. `BeginFrame` invalidation already covers world contents changing
under an unchanged camera. `OcclusionSweepCount` keeps counting physics sweeps. A new cumulative
`BoomProbeCount` counts calls through `BoomProbe`, so a consumer can prove one call per rendered frame.

Because a blocked boom now shortens along its own line instead of lifting, a negative pitch that meets the
ground slides the eye in toward the pivot while the view keeps tilting up. That is the look-up behaviour.

### Eased recovery

`BoomRecoveryRate` (per second, default 0) eases the boom back out after an obstruction clears. Zero is today's
instant behaviour. A pull-in is always instant, because an eased pull-in would put the eye inside the occluder.

The camera keeps a shortfall, the metres the boom is currently held short of its full length.

- In the `Eye` computation, the probe's shortfall is `full - reach`. If it exceeds the held shortfall, the held
  shortfall takes it at once. The used length is `full - held`.
- `AdvanceBoom(dt)` decays the held shortfall by `exp(-BoomRecoveryRate * dt)`, frame-rate independent.
  `FollowCameraController.Update` calls it beside `AdvanceTarget`.
- `Warp` and `SnapToTarget` clear the held shortfall, so a teleport never eases out from the old site.
- The `Distance` setter shifts it by the change in distance, floored at zero:
  `held = held > 0 ? max(0, held + (new - old)) : 0`. Clearing it instead made a zoom in during recovery pop the
  eye outward, against the gesture, while the shift holds the eye still until the new distance fits and keeps a
  zoom in the open instant.
- The held shortfall joins the eye cache key.

Tracking a shortfall instead of an eased length keeps zoom instant. Scrolling out in the open changes the full
length, the shortfall stays zero, and the eye moves at once.

### Tile-world probe

`TileWorldCameraProbe` in `KhaozEngine.TileWorld.Render3D` implements `ICameraBoomProbe` over a `TileWorldView`.
It lives in the view's assembly and reads the view's document, catalogs, observer and roof rule directly.

```csharp
public sealed class TileWorldCameraProbe : ICameraBoomProbe
{
    public TileWorldCameraProbe(TileWorldView view, TileObjectRaycast.BoundsSource bounds,
        Func<TileObjectArchetype, bool> blocks);
}
```

- **Terrain.** `view.PickSurface` on the observer's plane, so drawn terrain, water and walk surfaces stop the boom
  and undrawn tiles do not. The hit distance is reduced by the radius. The camera's `GroundHeight` clearance is
  the guarantee against grazing hits. Planes above the observer are not tested for terrain.
- **Objects.** `TileObjectRaycast.Pick` on the observer's plane and the plane above it, where roofs stand. The
  bounds source is wrapped so every model box grows by the radius, which gives the boom a sphere's clearance
  instead of a ray's. Hits are walked nearest first and the first one that passes the filter stops the boom. A
  box that already contains the origin is skipped, so a subject pressed against a wall does not collapse its own
  boom.
- **Filter.** `blocks` is the consumer's rule for which archetypes stop the camera. Tags are game content, so the
  engine names none. Separately, the probe always skips a roof that `view.IsRoofHidden` reports hidden. An
  invisible ceiling must never stop a camera, and every tile world that hides roofs needs that rule.
- **Result.** The nearer of the terrain and object distances, capped at the requested length.

One terrain ray and one object pick over a segment the length of the boom, once per frame through the eye
cache.

## Not changing

`IsoCamera3D`, `FollowCameraController` gestures, object and surface picking, `TileWorldView` drawing and roof
selection, and every consumer that leaves the new members at their defaults.

## Alternatives rejected

- **A new orbit camera type beside `FollowCamera3D`.** A cleaner model from scratch, but two orbit cameras would
  duplicate the projection, render-origin and eye-cache code the engine then maintains twice, unless
  `FollowCamera3D` is retired and Ruinborne migrated. That is a larger decision than this feature.
- **Game-side camera in Grimhollow.** Violates the engine-first boundary, and no other game would get pivot,
  look-up or tile-world collision.

## Tests

`FollowCamera3D`:

- Defaults reproduce the pre-change `Eye`, `View` and `Forward` bit for bit.
- `PivotHeight` moves the look-at point and the orbit centre together.
- Negative pitch looks up, and `PitchLimit` holds against a setter past it.
- A probe shortens the boom with skin and minimum distance applied. Probe and physics together take the shorter.
- Recovery pulls in instantly, eases out, matches across frame rates, and rate zero equals today.
- `Warp` clears the held shortfall. Zoom stays instant while unobstructed, and a zoom in during recovery never
  moves the eye outward.
- `BoomProbeCount` advances once per frame across repeated reads.

`TileWorldCameraProbe`:

- Terrain rising behind the pivot shortens the boom. A negative-pitch boom stops at the ground.
- Water and a walk surface stop it.
- An archetype that passes the filter stops it and one that fails does not.
- A visible roof on the plane above stops it. A hidden roof and every roof under always-hidden do not.
- A box containing the origin is skipped. With nothing in the way the reach is the full length.

## Docs sweep

`docs/USING-KHAOZENGINE.md` camera section, the `KhaozEngine.Render3D` and `KhaozEngine.TileWorld.Render3D`
package READMEs, the 20.13.0 `CHANGELOG.md` entry, and a row in `docs/INDEX.md`.
