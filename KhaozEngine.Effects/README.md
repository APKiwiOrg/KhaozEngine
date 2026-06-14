# KhaozEngine.Effects

Game-agnostic pooled particle system for MonoGame games.

- `ParticleSystem` - fixed-size, zero-allocation pool of rectangle particles.
- `ParticleEmitterConfig` - immutable, data-driven emitter preset (lifetime,
  speed, emission pattern, jitter, size curve, sway, acceleration, color).
- `ParticlePresets.Spark` / `.Ember` - built-in presets (outward sparks,
  upward-drifting embers).

```csharp
var particles = new ParticleSystem(rng);
particles.Emit(ParticlePresets.Spark, screenPos, oreColor, count: 4);
particles.Emit(ParticlePresets.Ember, screenPos, count: 3);

particles.Update(realDeltaSeconds);   // real (unscaled) delta
particles.Draw(spriteBatch, primitiveRenderer);
```

Derive custom presets with `with`:

```csharp
var fast = ParticlePresets.Spark with { MaxSpeed = 120f, Acceleration = new Vector2(0, 200) };
```
