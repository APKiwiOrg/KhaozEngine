# KhaozEngine.Game2D

Umbrella metapackage for a desktop 2D game on the MonoGame-free KhaozEngine stack. Contains no
code, it is a curated dependency group: one `<PackageReference>` instead of a dozen.

Pulls in:

- `KhaozEngine.Windowing` - `AppWindow` (Silk.NET/GLFW window + `Run` loop), the immutable
  `InputState` snapshot, `InputManager`/`Pointer`.
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
