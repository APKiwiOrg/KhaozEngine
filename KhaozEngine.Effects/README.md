# KhaozEngine.Effects

Game-feel visual effects for the MonoGame-free 5.x stack (System.Numerics + BCL only).

## ScreenShake

A trauma-based, deterministic screen-shake offset generator. Add trauma on impacts; the shake magnitude
falls off as trauma squared and decays over time. Seeded smooth noise keeps it reproducible and
headless-testable (no `System.Random` / wall-clock). It produces a positional `Offset` and a rotational
`Angle` the game composes onto its render camera:

```csharp
var shake = new ScreenShake();
shake.Add(0.6f);              // on an explosion / hit
// each frame:
shake.Update(dt);
renderCamera.Position = camera.Position + shake.Offset;
renderCamera.Rotation = camera.Rotation + shake.Angle;
```

The old 4.x rect-particle system that used to live here was retired; particle simulation now lives in
`KhaozEngine.Particles`.
