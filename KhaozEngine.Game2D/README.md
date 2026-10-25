# KhaozEngine.Game2D

Start a desktop 2D game with one package reference for windowing, rendering, UI, audio, particles and the game
loop. This code-free metapackage bundles the runtime with the shared `KhaozEngine.Foundation` building blocks.

Pulls in:

- `KhaozEngine.Windowing` - `AppWindow` (Silk.NET/GLFW window + `Run` loop), the immutable
  `InputState` snapshot, `InputManager`/`Pointer`, and `GpuBackends`, the one boot registration.
- `KhaozEngine.Gpu.Metal`, `KhaozEngine.Gpu.D3D11`, `KhaozEngine.Gpu.Vulkan` - the three
  engine-owned graphics backends. All three ship so the bundle restores on every supported desktop platform.
  Each backend is platform-guarded. `AppWindow` and both snapshot hosts call
  `GpuBackends.RegisterResolvedIfUnregistered()` to register the resolved backend and this platform's fallback.
  A game needs no backend package reference or registration call of its own.
- `KhaozEngine.Render2D` - `SpriteBatch`, `SpriteFont`, `Texture2D`, `Camera2D`.
- `KhaozEngine.Gui` - immediate-mode `GuiSurface` + the `ScreenStack`/widget retained UI.
- `KhaozEngine.Audio` - OpenAL streaming music + SFX/3D positional one-shots.
- `KhaozEngine.Particles` - deterministic particle sim, emitter presets, `ScreenShake`.
- `KhaozEngine.Telegraphs` - animated attack-telegraph / danger-zone shapes (2D path).
- `KhaozEngine.Game` - the `GameApp` loop facade + `SceneManager`/`GameScene` state stack.
- `KhaozEngine.Foundation` - the GPU-free foundation umbrella (ECS, persistence, content,
  diagnostics, collision, physics seam, terrain, determinism and friends), including its
  buildTransitive game-head build defaults.
- `KhaozEngine.Localization.Analyzers` - the KELOC raw-text diagnostics and the opt-in neutral-resx to
  `StringId` source generator. Both run in the consuming project's build and add no runtime dependency.
- `KhaozEngine.CodeHealth.Analyzers` - the KESIZE file-size ratchet. The direct dependency uses
  `PrivateAssets="none"` so its analyzer assembly and `buildTransitive` baseline discovery props both flow
  through the packed umbrella and run in the consuming project's build.

```xml
<PackageReference Include="KhaozEngine.Game2D" Version="x.y.z" />
```

Typical entry point is subclassing `GameApp` (see the `KhaozEngine.Game` package README).

Deliberately NOT included:

- No 3D. `Render3D` and the 3D bridges live in `KhaozEngine.Game3D` (a strict superset of this
  package). Reference that instead if you want a 3D world pass.
- Multiplayer transport. Add `KhaozEngine.Netcode.LiteNetLib` for 2D multiplayer. The bundle already carries
  transport-free Netcode primitives through `Gui`.
- No physics backend. The `Physics` seam rides in via Foundation, the BepuPhysics backend
  (`KhaozEngine.Physics.Bepu`) is an opt-in sibling you add explicitly.

A custom host can register a graphics backend explicitly when it needs a backend this platform does not default to.
