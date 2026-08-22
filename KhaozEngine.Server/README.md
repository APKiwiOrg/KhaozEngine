# KhaozEngine.Server

Umbrella metapackage for a headless / server-side build on the KhaozEngine stack. Contains no
code, it is a curated dependency group: one `<PackageReference>` gives an authoritative sim
server or tool the GPU-free foundation plus the full netcode and MMO server stack. Pulls in NO
graphics, windowing, audio, or GPU, so the server stays lean.

Pulls in:

- `KhaozEngine.Foundation` - the GPU-free foundation umbrella (ECS, serialization, content,
  persistence, diagnostics, collision, physics seam, terrain, determinism and friends),
  including its buildTransitive build defaults.
- `KhaozEngine.Netcode` + `KhaozEngine.Netcode.Abstractions` - transport-free netcode
  primitives, the `INetTransport` seam, `NetServer`/`NetClient` sessions, prediction, auth.
- `KhaozEngine.Netcode.LiteNetLib` - the LiteNetLib reliable-UDP transport binding IS bundled,
  so the server is wire-ready out of the box, not loopback-only.
- `KhaozEngine.Simulation` - `FixedTickHost`, the deterministic fixed-timestep accumulator.
- `KhaozEngine.Replication` - authoritative ECS replication (snapshots, deltas, interest).
- `KhaozEngine.WorldStore` - ONLY the durable-state seam: `IWorldStore` + `InMemoryWorldStore`.
- `KhaozEngine.Sharding` - the cell-grid world topology (`ShardHost`, ghosting, handoff).
- `KhaozEngine.NetWorld` - the authoritative movement server + client glue + `WorldPersistence`.
- `KhaozEngine.TileWorld.Netcode` - the tile movement stack (`TileWorldServer` / `TileWorldClient`,
  `TileMoveSimulator`, `TileWorldPersistence`), a SIBLING of `NetWorld` rather than a dependent, so a
  tile server carries none of the float locomotion stack and a float server carries none of this.
- `KhaozEngine.Physics` - the dependency-free physics seam (backend opt-in, see below).

```xml
<PackageReference Include="KhaozEngine.Server" Version="x.y.z" />
```

Deliberately NOT included (add these explicitly if you need them):

- `KhaozEngine.WorldStore.Sqlite` / `KhaozEngine.WorldStore.SqlServer` - the durable
  `IWorldStore` backends. Bundling them dragged Microsoft.Data.Sqlite and
  Microsoft.Data.SqlClient into every consumer, even ones using one backend or none.
- `KhaozEngine.Server.Admin` - the HTTPS admin endpoint, the only package that references
  ASP.NET Core.
- `KhaozEngine.Physics.Bepu` - the BepuPhysics backend behind the `IPhysicsWorld` seam.

A contracts-only project that just needs the wire types should reference
`KhaozEngine.Netcode.Abstractions` directly instead of this umbrella.

**NativeAOT.** The umbrella publishes clean under `PublishAot`, gated by the dev-only
`KhaozEngine.Server.AotProbe` project (not part of this umbrella, not packed). It covers the per-tick surface
(`Sharding` + `Replication` + `Ecs`), `Ecs`'s `WorldSerializer` world save/load (register the component set through
the generic `WorldSerializer.Create().Add<T>()` seam and pass source-generated JSON options), and `NetWorld`'s JSON
persistence (`PlayerRecord`, `WorldMetaRecord`, the ban store, each through a source-generated `JsonSerializerContext`).
