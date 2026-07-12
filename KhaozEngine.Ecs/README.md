# KhaozEngine.Ecs

A game-agnostic, struct-based **archetype** entity-component-system. Entities are versioned handles;
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

Opt-in data-parallel `World.ParallelForEach`/`Query.ParallelForEach` (fans an archetype's rows across an
`IJobScheduler`) is allocation-free in steady state. `World`'s buffered overload rents each worker chunk's
`EntityCommandBuffer` from an internal pool and returns it after playback. The lower-level
`Query.ParallelForEach(action, scheduler, sink)` overload hands you freshly allocated, caller-owned buffers in
your own sink instead, so external sink use never drains the World's pool.

A component struct with no fields is a **tag**: stored with no column, presence on the entity is its whole
state. `Get<T>` still throws for a tag, but `TryGet<T>` copies out `default` for a present one instead of
throwing. Tag detection on the generic component-access path is reflection-free, so it stays NativeAOT-safe.

Full docs: [KhaozEngine README](https://github.com/APKiwiOrg/KhaozEngine).
