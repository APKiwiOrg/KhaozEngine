# KhaozEngine.Ecs

A struct-based **archetype** entity-component-system for MonoGame. Entities are versioned handles;
components are `struct`s implementing `IComponent`, stored in contiguous archetype columns. Provides
`ref` access, `With`/`Without` queries with `ForEach` (arities 1-8), an `EntityCommandBuffer` for
deferred structural changes, typed `Resources`, and ordered `ISystem`s. Independent of the
input/screen packages, versioned on its own cadence (`1.0.0`).

```csharp
public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float Dx, Dy; }

var world = new World();
var e = world.Spawn();
world.Set(e, new Position());
world.Set(e, new Velocity { Dx = 1 });

world.ForEach((Entity id, ref Position p, ref Velocity v) => { p.X += v.Dx; });   // live refs
ref var pos = ref world.Get<Position>(e);                                          // ref access

// Defer structural changes during iteration:
world.ForEach((Entity id, ref Position p) => { if (p.X > 100) world.Commands.Despawn(id); });
world.Commands.Playback(world);   // (World.Update flushes Commands after each system automatically)
```

Full docs: [KhaozEngine README](https://github.com/APKiwi/KhaozEngine) and the design spec under
`docs/superpowers/specs/`.
