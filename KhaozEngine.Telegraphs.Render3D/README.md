# KhaozEngine.Telegraphs.Render3D

The ground-plane arm of the KhaozEngine telegraph system: `Scene3D` extension methods that paint
animated danger zones flat on the ground/terrain under the meshes, via Render3D's generic
depth-sampling `DrawGroundDecal` pass. Kept separate from `KhaozEngine.Telegraphs` so a 2D-only
game never drags in Render3D. Presentation only, holds no sim state.

- Extensions on `Scene3D`: `GroundCircle`, `GroundRing`, `GroundBeam`, `GroundCone`, `GroundArc`.
  Each takes world-space shape parameters plus a 0..1 progress and a `TelegraphStyle`, resolves
  the style at that progress, and queues a `GroundDecal`. Immediate-mode, call per frame.
- They live in the `KhaozEngine.Telegraphs` namespace on purpose, so the `using` you already
  have for `TelegraphStyle` and the presets brings the extensions into scope too.
- `GroundTelegraphs.BuildCircle/BuildRing/BuildBeam/BuildCone/BuildArc` statics are the pure
  style-to-decal mapping (headless-testable), the extensions are thin wrappers over
  `scene.DrawGroundDecal`.
- Edge/outline width is derived in world units as a small fraction of the shape's size, so a big
  AoE gets a proportionally bigger rim. `TelegraphStyle.EdgeThickness` is authored in 2D pixels
  and is deliberately ignored on this path.
- Beams and cones aim with an XZ direction vector. Decals gate on terrain height with sane
  defaults, so a zone hugs the ground instead of smearing up cliffs.

```csharp
// inside the 3D pass, progress 0..1 from the sim:
scene.GroundCircle(bossPos, radius: 6f, progress, TelegraphStyle.Fire);
scene.GroundCone(bossPos, aimDirXZ, halfAngleRad: 0.6f, range: 12f, progress, TelegraphStyle.Poison);
```

Depends on `KhaozEngine.Telegraphs` + `KhaozEngine.Render3D`. In the `Game3D` umbrella.
