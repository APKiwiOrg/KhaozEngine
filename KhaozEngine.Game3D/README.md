# KhaozEngine.Game3D

Umbrella metapackage for a desktop 3D game on the MonoGame-free KhaozEngine stack. Contains no
code, it is a curated dependency group and a strict superset of `KhaozEngine.Game2D`: one
`<PackageReference>` gives the full 2D + 3D + game-loop stack.

Pulls in:

- `KhaozEngine.Game2D` - everything in the 2D bundle: Windowing, Render2D, Gui, Audio,
  Particles, Telegraphs, the `Game` loop/scene framework, and the GPU-free `Foundation`
  (which also ships the buildTransitive game-head build defaults).
- `KhaozEngine.Render3D` - the stylized 3D renderer: `Scene3D`, glTF + procedural meshes,
  materials/lighting, skinning, billboards, ground decals.
- `KhaozEngine.Game.Render3D` - the Game framework's 3D bridge: `GameApp3D`, `IGameScene3D`,
  the `SceneManager.Draw3D` extension, and the animated-character layer.
- `KhaozEngine.Telegraphs.Render3D` - `Scene3D.GroundCircle/Ring/Beam/Cone/Arc` extensions that
  paint animated danger zones flat on the ground via the depth-sampling decal pass.
- `KhaozEngine.Terrain.Render3D` - chunked-LOD terrain meshing over the analytic `Terrain`
  field, plus the `TerrainStreamer` client world-streaming layer and PBR splat materials.
- `KhaozEngine.Physics` - the dependency-free physics seam (already in Foundation, referenced
  here so the seam is explicit for 3D character/world collision wiring).

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
