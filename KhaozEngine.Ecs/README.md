# KhaozEngine.Ecs

A minimal entity-component-system for MonoGame. `World` holds entities and components (stored by
type) and runs registered `ISystem`s each `Update`. Despawns are deferred to the end of the frame.
Independent of the input/screen packages.

```csharp
var world = new World();
var e = world.Spawn();
world.Set(e, new Position());
world.AddSystem(new MovementSystem());
world.Update(dt);                          // runs systems, flushes despawns
foreach (var ent in world.Query<Position, Velocity>()) { /* ... */ }
```

Full docs: [KhaozEngine README](https://github.com/APKiwi/KhaozEngine).
