# KhaozEngine.Windowing — milestone plan

**Goal:** A shared windowing + engine-native input foundation for the custom 5.x stack, consolidating the
SDL2/Metal window the renderers each open today, and providing keyboard/mouse input without MonoGame.

**Why this milestone:** renderers (Render3D/Render2D) and audio are shipped; the next blocker for porting the
2D stack (Screens/UI) and migrating games is **input + a shared window**. The full `KhaozEngine.Input`
(414-line `InputManager`, ~100-value `Keys` enum, gamepad, touch) is too big to port at once; this milestone
delivers the tractable, high-value core.

**Name:** `KhaozEngine.Windowing` (`KhaozEngine.Platform` is taken by the 4.x Clipboard package).

## Scope (this milestone)

- `Key` enum (comprehensive keyboard) + `MouseButton` enum (engine-native; no Veldrid/MonoGame leak).
- `InputState` — per-frame snapshot: keys down/pressed/released, mouse position + delta, mouse buttons
  down/pressed, scroll delta, window size; with `IsDown`/`WasPressed` helpers.
- `AppWindow` — owns the SDL2/Metal window + Veldrid device + swapchain + frame loop. `Run(Action<Frame>)`
  where `Frame` exposes `Dt`, `Input` (engine `InputState`), and the render target (the Veldrid
  `CommandList` + swapchain `Framebuffer`, the explicit GPU boundary). AppWindow clears + presents around the
  callback. `Device`/`MainSwapchain` exposed as public Veldrid properties (advanced GPU boundary).
- Integrate **Render2D**: a `SpriteBatch(AppWindow)` ctor and a `Render2DAssets(AppWindow)` (texture/font
  loaders) so a consumer draws a 2D scene into an `AppWindow` frame. A `WindowingSample` proves an
  interactive scene (sprite/text responding to keyboard + mouse).
- Ship on the shared 5.x line (bump to `5.4.0-experimental`).

## Out of scope (follow-ups)

- Gamepad + touch input; the rich `InputManager` (gestures, click-through, bounds helpers) and
  `VirtualResolution`/coordinate transforms.
- Folding `Render2DHost`/`Render3DHost` away (kept for now; AppWindow is the new shared path).
- Render3D-on-AppWindow integration.

## Verification

- `Key`/`InputState` helpers: headless unit tests (CI-safe).
- Window + input + Render2D integration: eyeball via the sample (launch, screenshot); window plumbing
  verified by running.
