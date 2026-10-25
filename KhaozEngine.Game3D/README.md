# KhaozEngine.Game3D

Start a desktop 3D game with one package reference for the 3D renderer, scene integration and the complete
`KhaozEngine.Game2D` runtime. This code-free metapackage includes the 2D drawing and UI a 3D game also needs.

Format-9 MapDoc and GPU-free prefab/interior preparation arrive through `Foundation`. Add `MapDoc.Physics`
and a physics backend explicitly for headless native builds. The editor and `ke-mapedit` are separate
authoring tools.

Pulls in:

- `KhaozEngine.Game2D` - everything in the 2D bundle: Windowing, Render2D, Gui, Audio,
  Particles, Telegraphs, the `Game` loop/scene framework, the three engine-owned graphics backends
  (`KhaozEngine.Gpu.Metal`, `KhaozEngine.Gpu.D3D11`, `KhaozEngine.Gpu.Vulkan`), and the GPU-free
  `Foundation` (which also ships the buildTransitive game-head build defaults). It also carries
  `KhaozEngine.Localization.Analyzers`, including the `StringId` generator, and the
  `KhaozEngine.CodeHealth.Analyzers` file-size ratchet. Both apply in the consumer's build.
- `KhaozEngine.Render3D` - the stylized 3D renderer: `Scene3D`, glTF + procedural meshes,
  materials/lighting, skinning, billboards, ground decals.
- `KhaozEngine.Attachments.Render3D` - rigid part definitions bound to the body's posed skeleton,
  with contract-joint resolution, body alignment, canonical world-transform output and a per-scene
  cache of verified snapshots and transactional upload leases.
- `KhaozEngine.Render3D.Ecs` - the ECS arm of the renderer: the `Transform3D` and `MeshInstance`
  components and `Scene3DBinder.Submit`, which draws every entity carrying both into a `Scene3D`.
- `KhaozEngine.Game.Render3D` - the Game framework's 3D bridge: `GameApp3D`, `IGameScene3D`,
  the `SceneManager.Draw3D` extension, and the animated-character layer.
- `KhaozEngine.Telegraphs.Render3D` - `Scene3D.GroundCircle/Ring/Beam/Cone/Arc` extensions that
  paint animated danger zones flat on the ground via the depth-sampling decal pass.
- `KhaozEngine.Terrain.Render3D` - chunked-LOD terrain meshing over the analytic `Terrain`
  field, plus the `TerrainStreamer` client world-streaming layer and PBR splat materials.
- `KhaozEngine.TileWorld.Render3D` - tile-world ground meshing, textured ground materials, props, water planes,
  region streaming and snapshot capture over the `TileWorld` authored-map model.
- `KhaozEngine.Particles.Render3D` - the 3D draw layer for the deterministic particle sim.
- `KhaozEngine.Physics` - the dependency-free physics seam (already in Foundation, referenced
  here so the seam is explicit for 3D character/world collision wiring).
- `KhaozEngine.CodeHealth.Analyzers` - the KESIZE file-size ratchet. The direct dependency uses
  `PrivateAssets="none"` so its analyzer assembly and `buildTransitive` baseline discovery props both flow
  through the packed umbrella and run in the consuming project's build.

The three graphics backends arrive through `Game2D`. `AppWindow` and both snapshot hosts call
`GpuBackends.RegisterResolvedIfUnregistered`, which registers the resolved backend and this platform's fallback.
A game needs no backend package reference or registration call of its own.

```xml
<PackageReference Include="KhaozEngine.Game3D" Version="x.y.z" />
```

Typical entry point is subclassing `GameApp3D` (see the `KhaozEngine.Game.Render3D` package
README).

Deliberately NOT included:

- Multiplayer transport. Add `KhaozEngine.Netcode.LiteNetLib` for multiplayer, and see
  `KhaozEngine.NetWorld` for the authoritative client glue. Transport-free Netcode primitives already arrive
  through `Game2D`.
- No physics backend. The BepuPhysics backend (`KhaozEngine.Physics.Bepu`) is an opt-in sibling
  you add explicitly, same pattern as the `WorldStore.Sqlite`/`.SqlServer` backends.
