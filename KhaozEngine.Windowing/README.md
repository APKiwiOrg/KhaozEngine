# KhaozEngine.Windowing

Windowing + input foundation for the custom MonoGame-free stack.

- `AppWindow` — owns the Silk.NET (GLFW) window, the GPU device + swapchain (via `KhaozEngine.Gpu`), and the
  frame loop. `Run(onFrame)` clears + presents around your callback; each `Frame` gives `Dt`, an engine-native
  `InputState`, framebuffer size, and the GPU command list to draw into. `AppWindow.Scaled(...)` fits a
  design-sized window to the display.
- `InputState` — per-frame keyboard + mouse + gamepad + touch snapshot (`IsDown`/`WasPressed` for
  `Key`/`MouseButton`, mouse position/delta/scroll, `Gamepad(i)`). Immutable; no MonoGame. `WasRepeated(Key)` /
  `WasTyped(Key)` surface OS key auto-repeat (`AppWindow` fills it from GLFW's `REPEAT` action; `WasPressed` stays
  press-edge only) so text fields hold-to-repeat.
- `InputManager` / `Pointer` — the higher-level read: unified pointer, edges, bounds helpers (`IsTapIn` etc.),
  region blocking, keyboard/gamepad/menu navigation.
- `GameClock` (pause/timescale), `DesignViewport` / `AdaptiveViewport` (letterbox/fill/stretch + responsive).

The 5.x renderers (`Render2D`, `Render3D`) build on this. Silk.NET windowing ships GLFW natives bundled per-RID,
so there is no SDL2/brew step. Touch is mobile-deferred (no 5.x mobile-windowing head yet).
