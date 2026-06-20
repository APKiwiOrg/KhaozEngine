# KhaozEngine.Gui modern UI primitives + icon system (v7.7.0)

Status: approved design, ready for implementation plan.
Date: 2026-06-20.
Worktree: `feature/gui-modern-primitives`.

## Goal

Give every game (Hardpoint, Nullwake, SpaceGame, future) modern UI affordances:
rounded corners, vertical gradient fills, soft drop shadows, hover glow, and a
reusable tintable icon set. Centralize in `KhaozEngine.Gui` (and the shared
`KhaozEngine.Render2D` SpriteBatch path) so games only set palette + icons, never
draw code.

**Hard requirement:** with all new knobs at their defaults, every existing screen
renders **byte-identically** to today. `GuiStyle.Default` keeps hard corners, no
shadow, solid fill, no glow. The new look is strictly opt-in.

## Decisions locked during brainstorming

1. **Rendering mechanism: SDF in the shared SpriteBatch shader** (not a 9-slice
   texture mask). Crisper at any zoom/Retina scale, cheap soft shadow/glow, true
   gradient. Cost: touches the one shared Render2D shader set, so byte-identity is
   protected structurally (see below).
2. **Gradient model: light/dark scale of the active state colour.** One pair of
   scale factors derives the 2-tone from whichever state (resting/hover/press/
   selected) is active, so all states get a matching gradient from one knob.
3. **Ship `GuiStyle.Modern` preset** alongside the unchanged `GuiStyle.Default`.
4. **Vertex layout: clean explicit 64B** (option A). Separate `Local`/`Shape`/`Mode`
   attributes; supports rounded *textured* draws later. 2x the current 32B vertex on
   every 2D draw, accepted for clarity + extensibility.
5. **Icon source: procedural CPU-bake** following the `VfxTextures` pattern (pure
   `BakePixels` + upload overload, headless-testable, no shipped binary asset).
6. **Ship composed widgets** `StatChip` + `IconButton` (thin, immediate-mode) on top
   of the icon-draw + rounded-panel primitives.

## Architecture

### Layer 1 - Render2D SDF path (`KhaozEngine.Render2D/SpriteBatch.cs`)

Gradients need no shader change: the vertex already carries a per-vertex `Color`
interpolated by the fragment shader (`texture * vColor`). A vertical 2-tone is just
"top corners = top colour, bottom corners = bottom colour". The shader work is purely
for the rounded-rect SDF (which also produces soft shadow + glow as the same
primitive at different softness).

**Unified 64B vertex** (replaces the current 32B `V`):

| field | type | bytes | meaning |
|---|---|---|---|
| `Pos`   | Vector2 | 8  | clip-space position (unchanged) |
| `Uv`    | Vector2 | 8  | texture UV (unchanged) |
| `Color` | Vector4 | 16 | per-vertex tint (unchanged; now also carries gradient) |
| `Local` | Vector2 | 8  | rect-local position in draw units from rect centre |
| `Shape` | Vector4 | 16 | `(halfX, halfY, radius, softness)` |
| `Mode`  | Vector2 | 8  | `(strokeWidth, modeFlag)` |

`VertexSizeBytes` 32 -> 64. Vertex layout gains `Local` (Float2), `Shape` (Float4),
`Mode` (Float2) elements. The `QuadRunBuilder<V>` is generic and format-agnostic, so a
single unified vertex keeps painter's-order interleaving between normal quads and
rounded panels/text automatically correct - a second vertex format would have forced a
batching-core refactor or broken layering. One pipeline pair (alpha + additive) as
today.

**Fragment shader** - the disabled path is the *literal current expression*, gated so
byte-identity is structural:

```glsl
vec4 base = texture(sampler2D(Tex, Samp), vUv) * vColor;
if (vMode.y < 0.5) {
    oColor = base;                      // existing draws: identical output
} else {
    vec2  b = vShape.xy;                // half-extents
    float r = vShape.z;                 // corner radius
    float soft = vShape.w;              // AA band (0 -> use fwidth) or shadow/glow spread
    float stroke = vMode.x;             // 0 -> filled, >0 -> ring
    vec2 q = abs(vLocal) - b + r;
    float d = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;   // IQ rounded-box SDF
    if (stroke > 0.0) d = abs(d) - stroke * 0.5;                   // ring for borders
    float aa = soft > 0.0 ? soft : max(fwidth(d), 1e-4);
    float cov = clamp(0.5 - d / aa, 0.0, 1.0);
    base.a *= cov;
    oColor = base;
}
```

Vertex shader passes `Local`/`Shape`/`Mode` through to the fragment stage.

`fwidth`-based AA makes the edge ~1px in *screen* space regardless of design->screen
scale (the crispness win). A `softness > 0` band gives a resolution-independent soft
falloff in draw units for shadows/glow.

**New public API on SpriteBatch:**

- `void DrawRounded(Texture2D tex, Vector4 destRect, Vector4 srcUV, Color top, Color bottom, float cornerRadius, float softness, float strokeWidth)`
  - emits one quad: `Local` = corner offsets from centre, `Shape` = `(w/2, h/2, radius, softness)`, `Mode` = `(strokeWidth, 1)`, top verts get `top`, bottom verts get `bottom`.
- `void Draw(Texture2D tex, Vector4 destRect, Color top, Color bottom)` - per-vertex vertical 2-tone, `Mode = 0` (works with or without rounding).
- Every existing `Draw(...)`/`DrawString(...)` overload emits `Local = 0`, `Shape = 0`, `Mode = 0` -> disabled branch -> unchanged output.

`EmitQuad` gains the extra per-vertex fields (defaulted for the plain overloads); a
new internal emit carries the SDF fields and the two corner colours.

### Layer 2 - GuiStyle (`KhaozEngine.Gui/GuiStyle.cs`)

Additive fields, all defaulted to today's look:

```csharp
public float   CornerRadius;        // default 0  -> hard corners
public float   ShadowSize;          // default 0  -> no shadow (softness spread, draw units)
public Vector4 ShadowColor;         // default transparent
public Vector2 ShadowOffset;        // default (0,0)
public GuiFill FillMode;            // default Solid
public float   GradientTopScale;    // default 1  (RGB multiply of state colour, top)
public float   GradientBottomScale; // default 1  (RGB multiply of state colour, bottom)
public Vector4 GlowColor;           // default transparent
public float   GlowSize;            // default 0  -> no hover glow (softness spread)
```

`enum GuiFill { Solid, VerticalGradient }`.

`GuiStyle.Modern` static preset: same default palette, but `CornerRadius` ~6-8,
`ShadowSize` + translucent dark `ShadowColor` + small downward `ShadowOffset`,
`FillMode = VerticalGradient` with `GradientTopScale` ~1.12 / `GradientBottomScale`
~0.85, and a subtle `GlowColor`/`GlowSize` for buttons.

### Layer 3 - GuiDraw centralization (`KhaozEngine.Gui/GuiDraw.cs`)

`Fill` / `Border` / `DrawButton` / `DrawSlider` branch on the style:

- **Plain path** when `CornerRadius == 0 && ShadowSize == 0 && FillMode == Solid && GlowSize == 0`:
  call the **exact existing** single-quad `Draw` / 4-edge `Border` - guarantees
  byte-identity for `GuiStyle.Default` and every current screen.
- **Modern path** otherwise, draw order: soft shadow (rounded, `softness = ShadowSize`,
  `ShadowColor`, offset by `ShadowOffset`) -> rounded fill (`radius = CornerRadius`,
  top/bottom colours from the state colour scaled by the gradient factors when
  `VerticalGradient`, else flat top==bottom) -> rounded border ring
  (`strokeWidth = BorderThickness`) -> hover glow (additive rounded, `softness = GlowSize`,
  `GlowColor`) when hovering and `GlowSize > 0`.

The state-colour selection logic in `DrawButton`/`DrawSlider` is unchanged; only the
emit primitive changes. Retained widgets (Button/Toggle/Slider/Dropdown/TextInput/...)
inherit the modern look for free since they all route through `GuiDraw`.

A small internal helper bakes/holds the 1x1 white texture reference already passed in;
no new texture is needed for rounded fills (the SDF shapes the white quad).

### Layer 4 - Icon system (`KhaozEngine.Gui/IconAtlas.cs`, `Icons.cs`)

Procedural alpha-mask atlas, following `VfxTextures`:

- `IconAtlas` builds a single RGBA8 atlas (white RGB, per-icon alpha mask) packed as a
  grid of cells. Pure `static (byte[] pixels, int w, int h) BakeAtlasPixels(int cell = 64)`
  is headless-testable; `BakeAtlas(Render2DSurface|Render2DContext, int cell)` uploads
  to a sampleable `Texture2D`.
- Each core icon is rasterized by a small per-icon routine into its cell's alpha
  (geometric outline style, tintable). Organic icons (skull) simplified but
  recognizable.
- **Core set** (constants in `static class Icons`): `Coin`, `Heart`, `Skull`,
  `Crosshair`, `Gear`, `Play`, `Pause`, `Close`, `Check`, `Plus`, `Minus`,
  `ChevronLeft`, `ChevronRight`, `ChevronUp`, `ChevronDown`.
- **Registry** keyed by string id: core ids pre-registered to atlas cells; games call
  `Register(string id, Texture2D tex, Vector4 srcUV)` to add their own (a game icon may
  point at the game's own texture, not the core atlas). Lookup returns `(Texture2D, srcUV)`.
- An `IconAtlas` instance owns the registry + the baked core `Texture2D`.

### Layer 5 - Surface API + composed widgets (`KhaozEngine.Gui/GuiSurface.cs`)

- `void Icon(Rect rect, string id, Vector4 tint)` -> looks up `(tex, srcUV)` and
  `_batch.Draw(tex, destRect, srcUV, tint)`. No rect reserved (icons are decoration);
  reserved when part of a chip/button.
- `bool IconButton(Rect rect, string iconId, GuiStyle style, bool enabled = true, bool selected = false)`
  - icon-only button: rounded panel via `DrawButton` path + centred icon tinted by the
  text colour; hover glow from the style. Returns true on a valid press-origin tap.
- `void StatChip(Rect rect, string iconId, string label, string value, SpriteFont font, GuiStyle style)`
  - rounded panel (style) + icon at left + label/value text. Decoration (reserves its
  rect for click-through like `Panel`).

The surface needs an `IconAtlas` reference; supplied via constructor (optional) or a
`SetIconAtlas(IconAtlas)` setter, so `Icon`/`IconButton`/`StatChip` resolve ids. When
no atlas is set those calls are no-ops in headless mode / draw nothing.

## Testing

Headless (no GPU) in `KhaozEngine.Tests/Gui` and `.../Render2D`:

- **Gradient emission:** `Draw(tex, rect, top, bottom)` puts `top` on the upper verts,
  `bottom` on the lower verts (inspect emitted run vertices).
- **SDF param emission:** `DrawRounded(...)` sets `Local`/`Shape`/`Mode` as specified;
  `strokeWidth > 0` and `softness` propagate.
- **Backward-compat vertex equality:** a plain `Draw(...)` and a `DrawRounded(...)` with
  `radius == 0 && softness == 0 && stroke == 0` *via the disabled mode flag* both yield
  the expected `Mode.y` so the plain path is provably the old quad. Assert
  `GuiStyle.Default` routes `DrawButton`/`Fill` through the plain single-quad path
  (e.g. emitted vertex count + `Mode == 0`).
- **GuiStyle.Modern wiring:** the preset has rounded/shadow/gradient/glow set; the
  modern path emits shadow + fill + border + (on hover) glow quads.
- **Icon bake:** `BakeAtlasPixels` returns the expected dimensions; each core cell has
  non-trivial alpha coverage (not all-zero, not all-opaque); registry lookup returns the
  right cell `srcUV`; game `Register` round-trips.
- **Composed widgets:** `IconButton` returns true on a tap-in and reserves its rect;
  `StatChip` reserves its rect; both no-op cleanly with a null batch.

Gated GPU goldens (`KE_GPU_TESTS=1`, baked on Metal/D3D11/Vulkan):

- **New `scene2d_modern`:** rounded gradient panel + soft shadow + a hover-glow button +
  a couple of icons + a StatChip. Locks the modern path visually across backends.
- **Regression:** existing `scene2d` + `scene2d_primitives` goldens must **not move**
  (the disabled SDF path is visually identical). The harness is tolerance-based
  (32x18 grid, 0.06/channel), so any real shift fails; confirm green on all 3 backends.

## Release (additive -> minor: 7.3.0 -> 7.4.0)

Per `KhaozEngine/CLAUDE.md` ritual, in order:

1. Bump `<KhaozEngine5xVersion>` to `7.4.0` in `Directory.Build.props`.
2. `CHANGELOG.md` newest-first detailed entry (new SDF SpriteBatch path + Gui modern
   primitives + icon system + StatChip/IconButton; byte-identical defaults).
3. `CHANGENOTES.md` one-line digest.
4. Update the three guarded declarations: `docs/CONSUMERS.md` "Engine current version",
   `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example.
5. `dotnet pack -c Release -o ./local-feed`.
6. Commit (subject `gui(7.4.0): ...`), `git tag v7.4.0`, push `main` + tag.
7. Update `docs/CONSUMERS.md` matrix note that Hardpoint should adopt 7.4.0.

Report **7.4.0** back so Hardpoint can pin and adopt (rounded gradient panels + soft
shadow, gradient top bar + buttons, hover glow, and tower-type icons registered into the
shared atlas).

## Out of scope

- Per-game art/themes beyond the `Modern` preset (games tune their own palette).
- Rounded *textured* draws (thumbnails) - the 64B vertex supports it later, not built now.
- Blur-quality shadows beyond the single SDF softness band.
- Hardpoint-side adoption (separate game-side work after 7.4.0 ships).
