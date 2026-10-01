# KhaozEngine.TileWorld.Physics

Turns an authored [KhaozEngine.TileWorld](../KhaozEngine.TileWorld) world into static colliders for any
[KhaozEngine.Physics](../KhaozEngine.Physics) world, plus a floor sampler and a water medium sampler for
[KhaozEngine.Locomotion](../KhaozEngine.Locomotion).

It references exactly those three packages. It carries no renderer and no physics backend, so the caller picks the
`IPhysicsWorld` (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the shipped one).

The package is opt-in and in no umbrella. Add it explicitly.

## Types

- **`TileColliderOptions`** sets `WallThickness` (0.1 m), `BlockedHeight` (null means the document's plane height)
  and `WalkSurfaceThickness` (0.1 m).

The collider builder, the samplers and their registration arrive in later commits of this release.
