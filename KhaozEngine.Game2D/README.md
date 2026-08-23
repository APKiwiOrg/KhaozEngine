# KhaozEngine.Game2D

Umbrella metapackage for a desktop 2D game on the MonoGame-free KhaozEngine stack. Contains no
code, it is a curated dependency group: one `<PackageReference>` instead of a dozen.

Pulls in:

- `KhaozEngine.Windowing` - `AppWindow` (Silk.NET/GLFW window + `Run` loop), the immutable
  `InputState` snapshot, `InputManager`/`Pointer`, and `GpuBackends`, the one boot registration.
- `KhaozEngine.Gpu.Metal`, `KhaozEngine.Gpu.D3D11`, `KhaozEngine.Gpu.Vulkan` - the three
  engine-owned graphics backends, carried here since 18.0.0. `KhaozEngine.Gpu` builds no device of
  its own any more, so a graphics umbrella without them would ship a stack that throws at the first
  device. ALL THREE ship rather than the build machine's one, because a NuGet package restores on
  every platform its consumer builds on. A foreign one is inert: each is platform-guarded, its
  interop sits behind `NoInlining` bodies the JIT never compiles off its platform, and
  `GpuBackends.RegisterResolvedIfUnregistered()` registers only what this OS can run. A game needs
  no startup call of its own, because `AppWindow` and both snapshot hosts make that one.
- `KhaozEngine.Render2D` - `SpriteBatch`, `SpriteFont`, `Texture2D`, `Camera2D`.
- `KhaozEngine.Gui` - immediate-mode `GuiSurface` + the `ScreenStack`/widget retained UI.
- `KhaozEngine.Audio` - OpenAL streaming music + SFX/3D positional one-shots.
- `KhaozEngine.Particles` - deterministic particle sim, emitter presets, `ScreenShake`.
- `KhaozEngine.Telegraphs` - animated attack-telegraph / danger-zone shapes (2D path).
- `KhaozEngine.Game` - the `GameApp` loop facade + `SceneManager`/`GameScene` state stack.
- `KhaozEngine.Foundation` - the GPU-free foundation umbrella (ECS, persistence, content,
  diagnostics, collision, physics seam, terrain, determinism and friends), including its
  buildTransitive game-head build defaults.

The old Effects package was absorbed into `Particles` in 9.0.0, so it no longer appears as a
separate reference.

```xml
<PackageReference Include="KhaozEngine.Game2D" Version="x.y.z" />
```

Typical entry point is subclassing `GameApp` (see the `KhaozEngine.Game` package README).

Deliberately NOT included:

- No 3D. `Render3D` and the 3D bridges live in `KhaozEngine.Game3D` (a strict superset of this
  package). Reference that instead if you want a 3D world pass.
- No networking. Add `KhaozEngine.Netcode.LiteNetLib` for 2D multiplayer.
- No physics backend. The `Physics` seam rides in via Foundation, the BepuPhysics backend
  (`KhaozEngine.Physics.Bepu`) is an opt-in sibling you add explicitly.
- No graphics backend is opt-in any more. The three above were opt-in siblings until 18.0.0, and
  they are carried here now, so a repin drops any `KhaozEngineMetal.Register()` /
  `KhaozEngineD3D11.Register()` / `KhaozEngineVulkan.Register()` line the game was making itself.
  Keep the call only for a custom host that wants a backend this platform does not default to.
