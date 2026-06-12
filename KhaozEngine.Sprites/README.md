# KhaozEngine.Sprites

Game-agnostic 2D sprite + directional-animation playback for MonoGame. Render directional, animated
sprites (8-way characters, projectiles, pickups) instead of flat primitives.

## Pieces

- **`Direction8`** — the 8 facings `S, SE, E, NE, N, NW, W, SW`. The enum value is the direction's
  row index in a PixelLab grid sheet. `Direction8Extensions.FromVector(facing)` maps a movement/aim
  vector to the nearest of 8 (screen space is y-down: +X east, +Y south; magnitude is irrelevant; a
  zero vector returns a fallback). `ToVector()` gives the unit facing back.
- **`SpriteSheetLayout`** — pure grid math (no `Texture2D`, headless-testable): from a sheet size plus
  either a per-frame size (`FromFrameSize`) or row/column counts (`FromGrid`), gives the source
  `Rectangle` for any `(row, column)`.
- **`SpriteSheet`** — a `Texture2D` paired with a `SpriteSheetLayout`. `FromFrameSize` / `FromGrid`
  factories; `GetFrame(row, col)` / `Frame(row, col)`.
- **`SpriteFrame`** — one drawable frame: a `Texture2D` + source `Rectangle`. Frames carry their own
  texture, so an animation can come from one packed sheet or a set of loose per-frame textures.
- **`SpriteAnimation`** — ordered frames + per-frame duration + loop flag. `FromFps(frames, fps, loop)`
  or the seconds-per-frame constructor.
- **`SpriteAnimationPlayer`** — advances an animation by a time delta and yields the current frame.
  Feed it a `float` seconds delta (e.g. `GameClock.ScaledDeltaSeconds`) or a `GameTime`. Loops, clamps
  + flags `IsFinished` for one-shots, and `Play(anim, preservePhase)` swaps animations.
- **`DirectionalAnimatedSprite`** — one `SpriteAnimation` per `Direction8`; plays the one matching the
  current facing and draws it via `SpriteBatch`. Centered origin by default; switching facing preserves
  the animation phase so a walk cycle stays smooth.
- **`PixelLabSpriteLoader`** — builds a `DirectionalAnimatedSprite` from a PixelLab export. PixelLab's
  row order (`S, SE, E, NE, N, NW, W, SW`) is isolated here; the core types stay generic.

## Usage

From an assembled grid sheet (8 direction rows x N frame columns):

    // load once
    var sheet = Content.Load<Texture2D>("hero_walk"); // 8 rows, 6 frame columns
    var hero = PixelLabSpriteLoader.FromGridSheet(sheet, frameCount: 6, fps: 12f);

    // per frame
    hero.Update(velocity, gameTime);                  // sets facing from velocity, advances
    // draw
    spriteBatch.Begin();
    hero.Draw(spriteBatch, worldPosition, scale: 2f, tint: Color.White);
    spriteBatch.End();

From loose per-frame textures (PixelLab's native export, once each PNG is a `Texture2D`):

    var framesByDir = new Dictionary<Direction8, IReadOnlyList<Texture2D>> { /* all 8 */ };
    var sprite = PixelLabSpriteLoader.FromFrames(framesByDir, fps: 12f);

Hand-built (full control, no PixelLab assumptions):

    var anims = new Dictionary<Direction8, SpriteAnimation>(); // one per direction
    var sprite = new DirectionalAnimatedSprite(anims);

## Notes

- The animation clock decouples from `KhaozEngine.Time` on purpose: it advances on a `float` seconds
  delta, so callers feed either `GameTime.ElapsedGameTime` or a scaled `GameClock` delta.
- Frame stepping uses a small relative tolerance so a delta that is an exact multiple of the frame
  duration advances predictably instead of dropping a frame to float-accumulation noise.
- PixelLab does not emit a canonical sheet (it exports loose per-frame PNGs). The `FromGridSheet`
  layout matches what an assembly step (Aseprite / TexturePacker / a pack script) produces; verify the
  row order against a real export the first time a game adopts this.
