# Point light shadows

Status: ships in 19.2.0. Issue #1002. Owner decision 2026-09-17.

## The problem

`Scene3D.AddLight` point lights have no occlusion of any kind. `computeLighting` in
`Internal/ShaderSources.Lighting.cs` adds `colour * max(N.L, 0) * falloff` to every fragment inside a
light's radius. Only the key light has a shadow map. In Grimhollow a door lantern outside a wall lights the
inner faces of the side walls through the wall, and its light pools radially around the flame instead of
fanning through the doorway. In Ruinborne a fireball lights the far side of a pillar.

## Alternatives weighed

- **Tile-world polar occlusion.** One 1D distance-to-wall map per light built from the tile collision.
  Cheapest and nearly exact for axis-aligned full-height walls, but it knows nothing about height, furniture
  or any world that is not a tile grid, and Ruinborne's effect lights move every frame. Rejected as the
  engine answer, kept as the note it was.
- **Spot lights.** A cone facing out of the mounting face stops the bleed behind a wall lantern and lets no
  light through the door either. Rejected: the door wedge is the thing being asked for.
- **Masking outdoor lights off `Indoors` tiles.** A hard edge at every threshold and no wedge. Rejected.
- **Omnidirectional shadow maps, cached for placed lights and per frame for effect lights.** Chosen. The
  correct physics, one API for both consumers, and the machinery is the cascade atlas's machinery one more
  time.

## Decisions

1. **One R32Float atlas, six face columns by N light rows, plus one D32FloatS8UInt depth buffer and one
   framebuffer.** The cascade atlas already proves this shape on Metal, D3D11 and Vulkan: a 2D colour target
   written by a depth-only pass, cell placement baked into the projection, scissor per cell. No cube map, no
   array target, no new GPU API.
2. **A cell stores LINEAR distance over radius, not projected depth.** The pass fragment writes
   `length(worldPos - lightPos) / radius`, cleared to 1.0. The receiver then needs no matrices: it selects the
   face from the light-to-fragment vector, reads the cell, and compares against its own normalized distance.
   Backend depth conventions never enter the compare.
3. **Face convention is the cube-map standard.** Face 0..5 is +X, -X, +Y, -Y, +Z, -Z. For a direction `d`
   with major axis magnitude `ma`, the in-face coordinates `(sc, tc)` are: +X `(-d.z, -d.y)`, -X `(d.z, -d.y)`,
   +Y `(d.x, d.z)`, -Y `(d.x, -d.z)`, +Z `(d.x, -d.y)`, -Z `(-d.x, -d.y)`, and `u = (sc / ma + 1) / 2`,
   `v = (tc / ma + 1) / 2`. Cell `(face f, slot s)` occupies pixels `x = f * res .. (f + 1) * res` and
   `y = s * res .. (s + 1) * res`. `PointShadowMath.FaceAndUv` is the one C# definition, the GLSL mirrors it
   verbatim, and a GPU test renders a box on each axis and reads the atlas back to pin the two together.
4. **Two modes on one call.** `AddLight(pos, colour, radius, intensity, LightShadow shadow)`.
   `LightShadow.None` is the default and renders byte-identically to today. `LightShadow.Static(key)` is
   rendered once into a cached slot and re-rendered only when the light moves, its radius changes, or the
   rigid caster signature inside its radius changes, under `MaxStaticRebuildsPerFrame`. `LightShadow.Dynamic`
   is re-rendered every frame under `MaxDynamicLightsPerFrame`. Requests past `MaxShadowedLights` fall back
   to unshadowed, nearest to the eye first.
5. **Rigid casters only in this round, shadow-only instances included.** Skinned casters in dynamic maps
   are a follow-up with their own issue. A static map that included a body would be wrong a frame later.
6. **Lazy allocation.** The atlas exists from the first frame that carries a request, so a game that never
   asks pays no memory. `ShadowSettings.PointShadows` rides the Low, Default and High profiles (off, 8 lights
   at 256, 16 lights at 512) and reconfigures at the frame boundary the way the cascade atlas does, keeping
   the previous layout on an allocation failure.
7. **Per-row clears by a depth-always quad under scissor**, never by `ClearColorTarget`, because whether a
   clear honours the scissor differs per backend and the quad does not.
8. **Culling is a sphere test per instance against the light sphere.** An instance that touches the light
   draws into all six faces. Per-face frustum culling is an optimisation for later, measured first.

## Contract between the halves

- UBO tail after the shadow tail: `vec4 PointShadowParams[16]` as `(slot or -1, bias, slopeBias, 0)`
  index-aligned with the point light arrays, and `vec4 PointShadowAtlas` as
  `(1 / (6 * res), 1 / (rows * res), rows, 6)`.
- Receivers bind the atlas as `PointShadowMap` directly after `ShadowSamp` in every shader family that
  binds `ShadowMap` (model, skinned, foliage, terrain, tile ground) and sample it with `ShadowSamp`, the
  existing clamp/point sampler, after the material maps per the Metal sample-order rule. A 1x1 white R32F
  default is bound when the atlas does not exist.
- Compare: `lit = step(d, stored + bias + slopeBias * (1 - ndl))`, averaged over a 2x2 tap pattern at half
  texel offsets clamped inside the cell. `att *= lit`. A slot below zero skips the whole thing, which is the
  byte-identical path.

## What the build taught

Six things the implementation settled or corrected, kept here because the decision is not readable off the
shipped code.

1. **The pass stores the NEAREST surface with no face culling, so the whole acne budget sits on the bias
   defaults.** Decision 2 stores linear distance and nothing about it says which side of a caster is stored.
   Rasterizing with `GpuFaceCull.None` means a thin wall's near face wins and a receiver behind it compares
   against a distance that is genuinely closer than its own. That is the correct answer for open geometry, which
   a front-face or back-face cull would either leak through or double-count, but it removes the usual dodge of
   pushing acne onto the caster's back faces. So `Bias` and `SlopeBias` are the only defence and they are
   defaults rather than knobs a consumer is expected to reach for, which is why they are clamped rather than
   trusted and why a floor of zero is enforced.
2. **The face uniforms are packed for every dirty light and uploaded ONCE, outside the pass.** The first shape
   packed a light's faces and uploaded the ring from inside the pass, so a frame with N shadowed lights would
   have done N whole-buffer updates with the atlas framebuffer bound. The frame is four steps instead: size the
   ring, pack each dirty light's six faces at its own packed index, upload the whole ring before the pass opens,
   then open the pass once and clear and draw every packed row inside it. The caster cull stays per light and is
   rebuilt per row inside the pass, because it records nothing.
3. **A failed atlas rebind is its own answer and it is LATCHED.** A rebind that returned the same false for "the
   binding already stands" and "the rebuild threw" left a host with no way to tell the ordinary per-frame case
   from a permanent failure, and a failure leaves the renderer still wanting the atlas it failed on, so a caller
   asking every frame re-entered the whole transaction and its GPU idle wait once a frame forever. The result is
   three-way, and a failure latches against the texture that caused it: the same atlas asked for again is
   refused without touching the device, and any other texture (including the 1x1 default a null asks for) clears
   the latch and is attempted for real.
4. **The untracked splat material set builder was DELETED rather than guarded.** It built a set carrying both
   shadow atlases directly, bypassing the tracked path, so the result never entered the shadow sampling
   bindings, and such a set reaching the live material sets would make every later layout change fail
   permanently rather than for one frame. Neither overload had a caller, since the one splat material site
   already used the tracked builder, so the fix was removal plus a note in its place saying why there is no
   untracked variant. A guard would have left the trap in the API for the next caller to find.
5. **The foliage VERTEX stage shares the frame block and had to grow with it.** The frame block is shared
   between the stages of one program, and the foliage vertex stage is paired with the model fragment, so it
   declares the point shadow tail even though it reads none of it. The layout tests now find that stage by
   SCANNING for the block rather than by a list, so the next stage in this position is not missed the same way.
6. **There are FOUR pinned tables, not three.** The three cross-compile hash tables (Metal MSL, Vulkan SPIR-V,
   D3D11 HLSL) are the ones everyone remembers. The shader corpus is a fourth artefact pinned the same way, and
   it drifts independently: the rebuild that took the pass's four programs also refreshed 35 rows across six
   older programs whose committed corpus values had fallen out of step with the hash tables. Both halves of this
   work re-baked, so the merged tree took one final re-bake after every shader change was in.

## Proof

Headless: face math, slot cache and eviction, budgets, settings clamps and atlas bytes, UBO layout.
GPU, relative pixels and no goldens: a wall between light and floor, a doorway gap, a static map that does
not re-render on a still frame, a dynamic light that moves its shadow, zero requests and disabled settings
both byte-identical to the pre-feature capture.
