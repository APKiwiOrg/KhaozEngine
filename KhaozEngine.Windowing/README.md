# KhaozEngine.Windowing

Windowing + input foundation for the custom MonoGame-free stack.

- `AppWindow` - owns the Silk.NET (GLFW) window, the GPU device + swapchain (via `KhaozEngine.Gpu`), and the
  frame loop. `Run(onFrame)` clears + presents around your callback; each `Frame` gives `Dt`, an engine-native
  `InputState`, framebuffer size, and the GPU command list to draw into. `AppWindow.Scaled(...)` fits a
  design-sized window to the display.
- `InputState` - per-frame keyboard + mouse + gamepad + touch snapshot (`IsDown`/`WasPressed` for
  `Key`/`MouseButton`, mouse position/delta/scroll, `Gamepad(i)`). Immutable; no MonoGame. `WasRepeated(Key)` /
  `WasTyped(Key)` surface OS key auto-repeat (`AppWindow` fills it from GLFW's `REPEAT` action; `WasPressed` stays
  press-edge only) so text fields hold-to-repeat.
- `AppWindow.SetIcon(params WindowIcon[])` sets the runtime window/taskbar icon. `WindowIcon` is one already-decoded,
  tightly-packed RGBA8 image (top-left origin); pass several sizes (16/32/48...) and GLFW picks per DPI. Windows and
  Linux/X11 apply it to the title bar + taskbar; macOS is a no-op (GLFW ignores window icons there) and never
  throws. Decode-free on purpose: this package takes pixels, not a PNG path, so it pulls in no image-decode
  dependency (the Game layer decodes via `Render2D.ImageRgba`). The Windows `.exe` icon shown when the app is not
  running is a separate per-game `<ApplicationIcon>`, independent of this API.
- `AppWindow.SetMacDockIcon(byte[] pngBytes)` sets the macOS **Dock / Cmd-Tab** icon from PNG bytes (the Cocoa
  counterpart to `SetIcon`, delegating to `Platform.ApplicationIcon`). GLFW cannot set the Dock icon and an
  unbundled `dotnet run` app has no `.app` icns, so without this such a run shows the generic document icon.
  Returns `false` off macOS / on empty input; never throws. `GameApp` calls it automatically from
  `GameAppOptions.WindowIconPath`.
- `InputManager` / `Pointer` - the higher-level read: unified pointer, edges, bounds helpers (`IsTapIn` etc.),
  region blocking, keyboard/gamepad/menu navigation.
- `GameClock` (pause/timescale), `DesignViewport` / `AdaptiveViewport` (letterbox/fill/stretch + responsive).

The 5.x renderers (`Render2D`, `Render3D`) build on this. Silk.NET windowing ships GLFW natives bundled per-RID,
so there is no SDL2/brew step. Touch is mobile-deferred (no 5.x mobile-windowing head yet).
