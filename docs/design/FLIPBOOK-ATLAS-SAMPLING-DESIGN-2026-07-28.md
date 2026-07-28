# Flipbook atlas sampling: interpolated flip carrier and mip bleed

Date: 2026-07-28.

Two independent defects in the particle flipbook path, both surfaced by one Ruinborne playtest report
of loot-orb item icons shimmering between neighbouring atlas cells on Windows and never on macOS.

- Engine issues [#389](https://github.com/APKiwiOrg/KhaozEngine/issues/389) (the flip carrier) and
  [#390](https://github.com/APKiwiOrg/KhaozEngine/issues/390) (the mip chain).
- Consumer report [Ruinborne#365](https://github.com/APKiwiOrg/Ruinborne/issues/365), scoped upstream in
  that repo's `docs/design/2026-07-28-flipbook-atlas-sampling-engine-scope.md`.

The scoping doc is the report and the arithmetic. This doc is the engine's decisions.

## 1. Defect 1: a per-instance constant went through the interpolator

`ParticleRenderer.PackFlipGrid` packs `cols + rows * 128 + qstr * 16384 + flipU * 2^22 + flipV * 2^23`
into one float and documents itself as having no spare bit: the maximum is exactly `2^24 - 1`, which is
the last integer float32 represents exactly. The fragment shader decodes it with mirror `mod`/`floor`
math.

The carrier is `vFlip.w`, and **no varying in `ShaderSources.Effects.cs` carried a `flat` qualifier**.
There was no `flat` anywhere in the engine's Render3D shaders at all. So a value that is identical at
every vertex of the quad was still handed to perspective-correct interpolation.

That is exact in infinite precision and not guaranteed exact in float32. Hardware that evaluates a
plane equation (`v0 + dvdx*x + dvdy*y`, both gradients exactly zero for a constant) reproduces the
value bit-for-bit. Hardware that evaluates `sum(b_i * v_i / w_i) / sum(b_i / w_i)` rounds. Metal on
macOS and Direct3D11 on Windows land on opposite sides of that, which is the platform split
`GpuBackendSelector` explains.

`FlipV` alone contributes `2^23`, so any consumer that sets it lands in the binade where the float32
ULP is exactly `1.0`. One ULP moves the decoded `cols` by a whole column, the cell grid shifts, and the
quad samples across its neighbours. The error varies per fragment and per frame, which reads as
shimmer. A static single-frame icon makes it obvious, an animating sheet hides it.

### Decision 1a: qualify every per-instance-constant varying `flat`, not just `vFlip`

`vFlip` is the one that bites, but it is not the only constant going through the interpolator.
`vColor`, `vShape` and `vExtra` are all written once per instance in the vertex shader (`vExtra.x` is
computed there, but only from per-instance inputs, so it is still constant across the six vertices).
Only `vLocal` and `vWorld` genuinely vary across the quad.

Qualifying all four is semantically identical, strictly cheaper, and closes the class rather than the
one instance. The alternative, qualifying `vFlip` alone, leaves `vShape.x` decoded as
`int(vShape.x + 0.5)` relying on half a unit of slack that nothing enforces, and leaves the next
integer-coded varying to rediscover this.

Provoking-vertex convention is irrelevant here: every vertex of an instanced quad carries the same
value, so first-vertex and last-vertex conventions agree.

### Decision 1b: pack zero motion strength when no motion sheet is bound

`ParticleSprite.FlipbookDesc.MotionStrength` defaults to `1f` whether or not a motion texture is bound,
so `qstr * 16384` contributed `1,048,576` to every flipbook that never uses the warp. Sending `0` when
`MotionTexture` is invalid drops the common case three binades and gives the packing scheme real
headroom instead of relying on `flat` alone.

This is behaviour-neutral, not a compensating change: when no motion texture is bound the renderer
binds `_dummyMv`, a 1x1 `(128, 128)` texel, which decodes to exactly zero displacement. The strength
scales a zero. Verified at `ParticleRenderer.cs:102`.

Kept as defence in depth rather than as the fix. `flat` is the fix. This makes the scheme survive the
next carrier that is not flat-qualified, and it is the half of defect 1 that a headless test can prove.

### Decision 1c: no golden can prove this, and that is recorded rather than worked around

A headless test on `PackFlipGrid`/`ResolveFrames` cannot catch it, because the pack math was always
correct. A GPU golden cannot catch it either, on any backend the engine can bake on: Metal is the
exact path by hypothesis, and lavapipe and WARP are software rasterizers that do not reproduce a
specific IHV's interpolator rounding.

So this ships proven-by-construction, not proven-by-test. What the suite does cover is that the decode
still works with the qualifier applied, which the existing flipbook `GpuFact`s do on every backend. The
decisive confirmation is a Windows tester on a real drop, and it belongs to the consumer.

## 2. Defect 2: the mip chain averages across cell boundaries

`Scene3D.LoadTexture` generates a full mip chain unconditionally, the flipbook path binds the trilinear
`LinearSampler` with no LOD clamp, and the fragment shader samples with plain `texture()`, so the LOD
comes from the UV derivatives.

Mipping an atlas is correct for a tiled albedo and wrong for a grid of independent frames. For
Ruinborne's 1024x512 four-column atlas the whole texture is 4x2 texels by mip 8, one texel per cell, so
every cell has become the average of its own icon, and past that the cells have merged into each other.
The orb icon draws out to 60 m where it covers about 2 px, so it spends the far half of its range in
that region.

Note the generation step is not where the bleed starts. A box filter on a power-of-two cell stays
inside the cell until the cell is a single texel. What bleeds earlier is the **bilinear tap at the cell
edge**: at level L the fringe it reaches into the neighbour is one texel of that level, which is `2^L`
level-0 texels, so the contaminated fraction of a `C`-texel cell is `2^L / C`. That fraction, not the
generation, is what sets the safe cap.

### Decision 2: clamp the LOD in the shader, and separately give `LoadTexture` a mip policy

Four options were weighed. Scores are 1-10, higher is better.

| criterion | 1. shader LOD clamp | 2. upload-side grid cap | 3. `LoadTexture` opt-out (level 0 only) | 4. gutters in the packer |
|---|---|---|---|---|
| fixes the reported artifact | 9 | 9 | 7 | 4 |
| consumer adoption cost | 10 (pin bump only) | 4 (must declare the grid) | 4 (must opt out) | 2 (art pipeline change) |
| correct for an atlas it was not told about | 9 | 2 | 2 | 1 |
| keeps minification quality within a cell | 9 | 9 | 2 | 8 |
| leaves non-atlas albedo alone | 10 | 9 | 9 | 10 |
| memory | 5 (unusable levels still allocated) | 9 | 10 | 6 |
| **total** | **52** | **42** | **34** | **31** |

Option 1 wins on the criterion the scoping doc under-weighted: the shader already knows `cols` and
`rows` from the packed value and can read `textureSize(AtlasTex, 0)`, so it can derive the safe maximum
LOD per atlas with no consumer involvement at all. Ruinborne adopts by version bump, and any future
atlas consumer is correct without knowing this failure mode exists. Option 3 loses because it trades
cross-cell bleed for the minification sparkle the chain was added to fix, which is a real regression on
a moving camera. Option 4 loses because it pushes the problem onto every consumer's art pipeline and
still fails deep enough into the chain.

**Both option 1 and a narrow form of option 2 ship**, because they answer different questions. Option 1
is the bug fix. The API gap in #390's title is separate and real: no caller could express "do not mip
this" for a UI sheet, a gradient ramp, or a lookup texture either. That gap gets a policy type, not a
grid-aware special case, so the atlas knowledge stays in the one place that already has it.

Implementation of the clamp:

```glsl
vec2 tsz     = vec2(textureSize(sampler2D(AtlasTex, AtlasSamp), 0));
vec2 cellPx  = tsz * cell;                                   // cell is already 1/vec2(cols,rows)
float maxLod = max(log2(max(min(cellPx.x, cellPx.y), 1.0) / MinCellTexels), 0.0);
```

then both atlas taps go through `textureLod(..., min(lod, maxLod))` with `lod` computed from the UV
derivatives. `MinCellTexels` is 4: the fringe is then at most a quarter of a texel of contamination at
the cell edge and the cell still minifies properly on the way down. The procedural path packs grid 0
and discards these taps, so its output stays byte-identical.

`textureLod` rather than `textureGrad` with clamped gradients, because a manually computed LOD is
deterministic across backends, which is what the cross-backend goldens need. In magnification the
computed LOD is negative and clamps to 0, which is exactly what `texture()` already did, so the
existing goldens are expected to be unchanged rather than rebaked.

The motion-vector taps are clamped on the same LOD, since a warped tap that samples a blended motion
sheet would reintroduce the same cross-cell average one level removed.

### Decision 2b: the mip policy is a value type, not a bool

`LoadTexture` gains an optional `TextureMipPolicy`:

- `TextureMipPolicy.Full` (the default, and what every existing call keeps getting)
- `TextureMipPolicy.None`
- `TextureMipPolicy.AtlasGrid(cols, rows, minCellTexels = 4)`

A bare `bool generateMips` would have been smaller and would have forced the third case to be a lie.
The level count is pure arithmetic, so it lives in a static helper next to `MipLevelCount` and is
covered headless. `AtlasGrid` exists because it has a caller today (a consumer that wants to stop
paying for levels the clamp will never sample) and because the model pipeline has no shader-side clamp
of its own, so a model atlas has no other answer.

## 3. What is NOT in scope

- A texture-array flipbook path (one array layer per frame) is the fully correct answer: mips are
  per-layer, so there is no cross-cell fringe at any level. It also changes the resource layout, the
  sampling path and the consumer-facing handle, and the LOD clamp removes the visible artifact without
  any of that. Filed as [#392](https://github.com/APKiwiOrg/KhaozEngine/issues/392) rather than built.
- Anything on the Ruinborne side. Both affected sites pass `FlipV: true` and neither needs to change.

## 4. What shipped

Landed in 17.12.0, closing #389 and #390. Read decision 2 first if you are tempted to "simplify" the
clamp away: the interesting result is that the shader can derive the cap itself, which is what makes
the consumer's adoption a pin bump rather than a code change.
