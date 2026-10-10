# KhaozEngine.MapDoc.Physics

Physics for a native [KhaozEngine.MapDoc](../KhaozEngine.MapDoc) world. It reads the collision data a verified
asset closure carries as [KhaozEngine.Physics](../KhaozEngine.Physics) shapes.

It references exactly `KhaozEngine.MapDoc`, `KhaozEngine.Physics` and
[KhaozEngine.Movement](../KhaozEngine.Movement). It carries no renderer, no physics backend and no TileWorld, so the
caller picks the `IPhysicsWorld` (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the
shipped one).

The package is opt-in and in no umbrella. Add it explicitly.

## Asset shapes

```csharp
MapAssetClosure closure = MapAssetClosure.Load(roots, source);
MapAssetShapes shapes = MapAssetShapes.Read(closure, "doorway");
if (shapes.IsSolid)
    Install(shapes.Collider!, shapes.SourceUnitsToMetres);
```

`Collider` and `Selection` resources carry `PropCollisionFormat` version 1 bytes. `MapAssetShapes.Read` reads each
declared resource and keeps the shapes in asset source units. `SourceUnitsToMetres` is the scale the caller applies.
`ColliderSha256` and `SelectionSha256` are the verified resource digests.

An asset is solid exactly when it declares a collider. A selection volume alone makes it examinable without
blocking.

`Read` throws `MapDocumentException` when:

- a collision payload does not read as exactly one shape,
- a compound holds a triangle mesh, which the physics backend cannot install,
- the asset declares a support resource, because placement-local support surfaces arrive with R5.
