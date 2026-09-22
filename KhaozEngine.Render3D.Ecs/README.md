# KhaozEngine.Render3D.Ecs

The ECS arm of `KhaozEngine.Render3D`. Three types, kept in their own package (it depends on
`KhaozEngine.Ecs`, which pulls `Simulation` and `Serialization`) so a consumer that only draws with
`Scene3D`, or only bakes from a kit manifest, never drags in the ECS. The `Game3D` umbrella carries it.

- `Transform3D` - an `IComponent` world transform: `Position`, `Scale`, `Rotation`. A zero `Scale` is
  treated as one and a zero `Rotation` as identity, so `new Transform3D { Position = p }` just works.
  `ToMatrix()` builds scale, then rotation, then translation. `ToMatrix(Vector3 renderOrigin)` builds the
  camera-relative matrix for a consumer that wants to keep one. `Scene3D` never needs it, because every
  `Scene3D` entry point takes an absolute matrix and reduces it itself. Reducing twice double-subtracts
  the origin.
- `MeshInstance` - an `IComponent` carrying the `MeshHandle`, a `Tint` (zero is white) and a `Material`
  (unset is matte).
- `Scene3DBinder.Submit(world, scene)` - draws every entity carrying both components into the scene,
  carrying the material through. Call once per frame between `Scene3D.Begin` and the surface render. The
  delegate overloads `Submit(world, draw)` are the pure core, headless-testable with a recording delegate.

```csharp
var e = world.Spawn();
world.Set(e, new Transform3D { Position = new Vector3(4f, 0f, 2f) });
world.Set(e, new MeshInstance { Mesh = tower, Material = Material.Shiny });

// per frame, inside OnDraw3D:
scene.Begin();
Scene3DBinder.Submit(world, scene);
```

The namespace is `KhaozEngine.Render3D`, so existing source compiles unchanged once the package is
referenced. Before 20.0.0 these types shipped inside `KhaozEngine.Render3D` itself.
