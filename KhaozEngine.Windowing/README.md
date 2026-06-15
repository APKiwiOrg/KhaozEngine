# KhaozEngine.Windowing (experimental, 5.x)

Windowing + input foundation for the custom MonoGame-free stack.

- `AppWindow` — owns the SDL2/Metal window, Veldrid device + swapchain, and the frame loop. `Run(onFrame)`
  clears + presents around your callback; each `Frame` gives `Dt`, an engine-native `InputState`, and the
  GPU command list to draw into. `Device`/`MainSwapchain` are exposed as the advanced GPU boundary.
- `InputState` — per-frame keyboard + mouse snapshot (`IsDown`/`WasPressed` for `Key`/`MouseButton`,
  mouse position/delta, scroll). No MonoGame.

The 5.x renderers (`Render2D`, ...) build on this. Metal-only for now; needs SDL2 at runtime
(`brew install sdl2`). Gamepad/touch and the rich gesture/`InputManager` layer are follow-ups.
