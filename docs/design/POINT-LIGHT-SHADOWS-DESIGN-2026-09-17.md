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
6. **Nothing is allocated until something asks, and all of it happens at the FRAME BOUNDARY.** A game that
   never requests a point shadow allocates nothing at all. Once a frame has carried a request, the atlas comes
   up at the next frame boundary rather than inside that frame, beside the cascade atlas's own pending layout,
   so the first frame to ask renders unshadowed and the one after it carries the map (see what the build taught,
   item 7). `ShadowSettings.PointShadows` rides the Low, Default and High profiles (off, 8 lights at 256, 12
   lights at 384) and keeps the previous layout on an allocation failure.
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

Fourteen things the implementation settled or corrected, kept here because the decision is not readable off the
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
7. **Allocation and rebinding moved OUT of the frame and onto the frame boundary.** The first shape allocated
   the atlas inside the frame that first asked for one, which meant a GPU idle wait and a rebuild of every
   material set in the scene with a command list open, swapping the sets the model pass was about to bind out
   from under it. That is not how the proven cascade path works, and it is not something this machine can test:
   the two backends that would have shown the stall are the two the dev machine cannot run. So the frame that
   asks only RECORDS that it asked, and the reconfigure at the next boundary does every allocation, reshape,
   rebind and release, which is the path the cascade atlas already had tests and three backends behind. The
   visible cost is one unshadowed frame per new request.
8. **Slot acquisition is TWO PHASES, and a row requested on this frame is never a victim.** Acquisition runs
   nearest to the eye first, and under a plain one-pass least-recently-requested eviction that order let a
   newcomer at the head of the list evict an incumbent standing further back in the same list, which then
   acquired and evicted the next one, so one arrival cascaded through every light behind it: measured as 4
   rebuilds on a frame where 1 was needed. `AcquirePointShadowSlots` answers in two passes instead. Phase one
   runs `PointShadowSlots.TryTouch` over EVERY request, which re-seats a request that already owns a row (key
   AND mode matching) on this frame and returns it untouched with its map, and evicts nobody. Phase two calls
   `Acquire` for the rest, which takes a free row first and only then the least recently requested row, skipping
   every row this frame has already named. Phase one is what makes every incumbent's row "requested this frame",
   so phase two cannot reach it. With the request list already cut to the row count, those two rules are the
   guarantee that matters: no light in a frame can take a row away from another light in the same frame.
9. **A dynamic light has no identity of its own, so a row is owned by the (key, MODE) pair AND carries nothing
   across a frame.** Decision 4 gives a dynamic light no key, and the scene keys its row by the light's place in
   the light queue. That number lands inside a static caller's key space by construction, so keying on the number
   alone handed queue index 3 the map belonging to static key 3. The pair fixes that half. The other half is that
   the queue position is not stable either: one dynamic light expiring shifts the rest down, and a row kept across
   the frame boundary was then matched by its new namesake, reported as rendered, and sampled as a shadow cast
   from the old light's position, which is an index shift handing one dynamic light another light's map. So
   `PointShadowSlots.ReleaseDynamicRows` frees every dynamic row at the START of each frame's acquire, before
   phase one, and `PublishPointShadowUniforms` holds a dynamic request to the stricter test of its row having been
   rendered THIS frame rather than ever. The two halves agree by construction, and the queue-position key is then
   affordable for the reason the mode exists: a dynamic row is marked dirty on every acquire and redrawn on every
   frame it is drawn at all, so a shuffled queue costs a rebuild rather than a wrong picture. What the mode does
   NOT survive is the per-frame budget, and that is the honest limit on it: the dynamic lights redrawn each frame
   are the nearest to the eye up to `MaxDynamicLightsPerFrame`, and one whose map was not redrawn on a given frame
   is handed no slot and renders unshadowed for that frame. The budget is therefore a consumer-facing number
   rather than an internal one, and the guidance in USING says to keep the lights alight at once at or under it.
10. **The High profile is 384 by 12, not 512 by 16.** The atlas is nine bytes a texel (4 of `R32Float` colour
    plus 5 of `D32FloatS8UInt` depth), so 512 by 16 is 226,492,416 bytes, 216 MiB of resident video memory,
    which is not a defensible thing for a quality preset to help itself to on hardware the operator never
    chose. 384 by 12 is 95,551,488 bytes, about 91 MiB, and is still half again the face resolution and half
    again the light budget of Default's 256 by 8 at 28,311,552 bytes (27 MiB). `PointShadowSettings.AtlasBytes`
    is the arithmetic and a test pins all three profiles against it.
11. **A reshape binds the replacement BEFORE it retires the old atlas.** The boundary brought the new atlas up
    over the live one and only then rebound the receivers, which made the design's "keep the previous layout on
    a failure" promise true for an allocation refusal and false for a bind refusal: the bind fails because a
    resource set cannot be allocated, the fallback is another set allocation under that same pressure, and with
    the previous atlas already freed there was nothing to put back, so every receiver set and the renderer's own
    handle were left naming a disposed texture for the next cascade reconfigure to copy into a fresh generation
    of sets. The order is the cascade replacement's now: build the pair, bind the receivers to it, and commit
    (which retires the old one) only on a confirmed rebind. A refused bind disposes the new pair and the scene
    carries on shadowing at the layout it had, which is what an allocation refusal already did.

12. **EVERY PROOF LIT OPEN SPACE, AND THE FIRST CONSUMER'S LAMPS ALL SAT INSIDE THEIR OWN FIXTURES** (#1010,
    fixed in 19.3.0). Item 1 above records that the pass stores the nearest surface with `FaceCull.None`, and
    read alongside it this was predictable: a wall lantern's light is the exact centre of a closed body a few
    centimetres across, so the nearest surface in every direction is the lamp, every receiver past it compares
    as occluded, and the lit lantern goes dark the moment it is given a shadow map. Nothing caught it because
    every GPU case in this round stood its light in open space with a wall two metres away, which is the one
    arrangement in which a fixture cannot be the nearest thing. The standard answer is a per-light NEAR RADIUS,
    the counterpart of the far plane the light radius already is: `LightShadow.NearRadius` in metres, carried
    in the per-face slice, discarded in all three caster fragments and folded into the static cache signature
    beside the quantised position and radius, with an instance lying wholly inside it dropped on the CPU. The
    lasting lesson is about the PROOFS rather than the feature: a shadow case whose caster is convenient to
    place is a case that has chosen the easy geometry, and the consumer's own arrangement (a light inside a
    model, a caster touching the receiver, a light under a floor) is what has to be in the suite.
13. **A SPHERE CANNOT CLEAR A WALL-MOUNTED FIXTURE, SO THE CLEARANCE IS ALSO A BOX** (#1011, 19.4.0). The near
    radius of item 12 has one number to spend and a wall lantern needs two: its flame stands 0.18 to 0.21 m off
    the wall it hangs on, and its caps, rod, arm and back plate reach more than 0.3 m from the flame. Any radius
    short of the wall leaves those parts in the map, where they cast hard triangles over the floor and the wall
    beside the lamp, and any radius past them deletes the wall, which is the one surface the feature exists to
    respect. The first consumer shipped the short radius and its playtest showed the triangles. The answer is
    `LightShadow.ExclusionMin`/`ExclusionMax`, a world-space axis-aligned box discarded in the same three caster
    fragments, carried in the same per-face slice and part of the request's value, so the consumer passes the
    fixture's own world bounds and everything a centimetre outside them still casts. The sphere stays for the
    fixture that is round and free-standing. The lesson is that a clearance shaped for the light is the wrong
    shape: what has to be left out is the FIXTURE, and a fixture is a box someone authored.
14. **THE SOFT FILTER SAMPLES BY DIRECTION, AND ITS WIDTH IS A RATIO OF DISTANCES** (#1012, 19.4.0). The first
    receiver took four taps at half-texel offsets inside one face cell, which anti-aliases the edge and cannot
    widen it, and a kernel widened in TEXEL space stops at the cell border and draws a seam down every cube
    edge. The soft path offsets the lookup DIRECTION instead and lets each tap do its own face select, so a
    penumbra crosses a face boundary like any other part of the sphere. Width is the similar-triangles estimate
    `LightSizeMetres * (receiver - blocker) / blocker` from a six-tap blocker search, capped at
    `MaxPenumbraTexels`, which is why the edge stays tight where a door frame meets the floor and spreads with
    the distance into the room. The nine-tap disc is rotated per fragment off a hash of the ABSOLUTE world
    position (render origin added back), so the noise neither crawls with the camera nor jumps when the
    floating origin rebases. Review caught that the first form summed before hashing, which a world 100 km from
    zero turns into banding (an 8 mm float step, and a sin argument in the millions), so each half is folded
    into a 16 m cell first and summed after. `Hard` was frozen verbatim beside it rather than expressed as a zero-width soft
    kernel, because a consumer comparing releases needs one path that is byte-for-byte the old picture.

## Proof

Headless: face math, slot cache and eviction, budgets, settings clamps and atlas bytes, UBO layout.
GPU, relative pixels and no goldens: a wall between light and floor, a doorway gap, a static map that does
not re-render on a still frame, a dynamic light that moves its shadow, zero requests and disabled settings
both byte-identical to the pre-feature capture.
