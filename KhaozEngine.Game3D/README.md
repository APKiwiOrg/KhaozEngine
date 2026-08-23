# KhaozEngine.Game3D

Umbrella metapackage for a desktop 3D game on the MonoGame-free KhaozEngine stack. Contains no
code, it is a curated dependency group and a strict superset of `KhaozEngine.Game2D`: one
`<PackageReference>` gives the full 2D + 3D + game-loop stack.

Pulls in:

- `KhaozEngine.Game2D` - everything in the 2D bundle: Windowing, Render2D, Gui, Audio,
  Particles, Telegraphs, the `Game` loop/scene framework, the three engine-owned graphics backends
  (`KhaozEngine.Gpu.Metal`, `KhaozEngine.Gpu.D3D11`, `KhaozEngine.Gpu.Vulkan`, carried since
  18.0.0), and the GPU-free `Foundation` (which also ships the buildTransitive game-head build
  defaults).
- `KhaozEngine.Render3D` - the stylized 3D renderer: `Scene3D`, glTF + procedural meshes,
  materials/lighting, skinning, billboards, ground decals.
- `KhaozEngine.Game.Render3D` - the Game framework's 3D bridge: `GameApp3D`, `IGameScene3D`,
  the `SceneManager.Draw3D` extension, and the animated-character layer.
- `KhaozEngine.Telegraphs.Render3D` - `Scene3D.GroundCircle/Ring/Beam/Cone/Arc` extensions that
  paint animated danger zones flat on the ground via the depth-sampling decal pass.
- `KhaozEngine.Terrain.Render3D` - chunked-LOD terrain meshing over the analytic `Terrain`
  field, plus the `TerrainStreamer` client world-streaming layer and PBR splat materials.
- `KhaozEngine.TileWorld.Render3D` - the 3D draw layer over the `TileWorld` authored-map model.
- `KhaozEngine.Particles.Render3D` - the 3D draw layer for the deterministic particle sim.
- `KhaozEngine.Physics` - the dependency-free physics seam (already in Foundation, referenced
  here so the seam is explicit for 3D character/world collision wiring).

No graphics backend is opt-in any more: the three above ride in through `Game2D`, so a repinned game
drops any `KhaozEngineMetal.Register()` / `KhaozEngineD3D11.Register()` / `KhaozEngineVulkan.Register()`
line of its own. `AppWindow` and both snapshot hosts make the one call
(`GpuBackends.RegisterResolvedIfUnregistered`), which seats the backend the process resolves to plus
this platform's own as the fallback target.

```xml
<PackageReference Include="KhaozEngine.Game3D" Version="x.y.z" />
```

Typical entry point is subclassing `GameApp3D` (see the `KhaozEngine.Game.Render3D` package
README).

Deliberately NOT included:

- No networking. Add `KhaozEngine.Netcode.LiteNetLib` for multiplayer, and see
  `KhaozEngine.NetWorld` for the authoritative client glue.
- No physics backend. The BepuPhysics backend (`KhaozEngine.Physics.Bepu`) is an opt-in sibling
  you add explicitly, same pattern as the `WorldStore.Sqlite`/`.SqlServer` backends.
