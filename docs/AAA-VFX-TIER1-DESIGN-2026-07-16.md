# AAA VFX Program Tier 1 Design

Date: 2026-07-16. Branch: `feature/aaa-vfx-tier1`. Status: approved for implementation (owner-approved
program, `docs/ROADMAP.md` "AAA VFX program", autonomous session with delegated design authority).
Builds directly on the 10.126.0 particles modernization
(`docs/PARTICLES-VFX-DESIGN-2026-07-16.md`).

Three sub-features, shipped as three sequential releases from one worktree, HDR first because the
other two look dramatically better on top of it:

1. HDR pipeline + filmic tonemap + HDR bloom (section "HDR"), shipped 10.128.0.
2. Flipbook particles with motion-vector blending (section "Flipbook particles"), shipped 10.129.0.
3. Screen-space distortion pass (section "Screen-space distortion"), shipped 10.130.0.

All three have shipped, so the AAA VFX program's Tier 1 is complete. Tiers 2 and 3 remain on
`docs/ROADMAP.md`, each pulled into its own worktree when a game calls for it.

## HDR

### Verified current state (exploration summary)

- Every color-bearing target in the pipeline is `R8G8B8A8UNorm`: `ColorTex`, `MsColor`, `PingA`,
  `PingB`, `BloomA`, `BloomB` (`RenderResources.cs`). The hardware clamp at that format boundary is
  the engine's ONLY systematic clip of over-1.0 values.
- No shader clamps its final rgb except `DecalFrag` (deliberate energy-lane bound, with a comment
  anticipating float targets). Model/splat/sky/water/particle/beam/trail all emit unclamped floats.
- `Color` is unclamped float storage with documented-unclamped scale/lerp operators, and every CPU
  upload path (`ToVector4`, UBO/vertex writes) passes values above 1.0 through untouched.
- Light intensity (`AddLight`, `PointColorIntensity.w`) multiplies through unclamped.
- Post chain (LDR order): Quantize -> Outline -> Bloom(bright/blurH/blurV/composite) -> FXAA ->
  Blit, ping-ponging ColorTex/PingA/PingB, with an even/odd vertical-flip parity rule counted over
  the passes that ran, and an alpha-lane background marker (clear a=0, geometry/sky a=1) that every
  pass preserves and the blit consumes for starfield/transparency.
- The MSAA sample-count change path (`RebuildMrtRenderers` + per-renderer `SetOutputs`) is the
  proven seam for "every MRT pipeline's output description changed", which an HDR format flip is.

### D-H2: Tonemap operator (weighted)

| Criterion (weight) | ACES Narkowicz fit | Hable/U2 | Ext. Reinhard | AgX |
|---|---|---|---|---|
| Filmic AAA look, hot-core desaturation (x3) | 9 | 8 | 4 | 9 |
| Cross-backend golden stability, pure ALU, no LUT (x2) | 9 | 9 | 9 | 5 |
| Recognizability / authoring predictability (x2) | 9 | 7 | 6 | 7 |
| Parameter surface (x1) | 9 | 5 | 8 | 7 |
| Runtime cost (x1) | 9 | 8 | 10 | 6 |
| **Total (weighted)** | **81** | **69** | **60** | **64** |

ACES filmic (Krzysztof Narkowicz 2015 fit) wins: the S-curve with highlight desaturation toward
white is exactly the hot-cores-saturate-and-halo behavior the program targets, it needs no curve
tuning (exposure is the only knob), and it is pure ALU so per-backend goldens stay stable. AgX has
the better hue skeleton but needs a LUT or a heavy fit, both golden hazards. `TonemapOperator`
additionally offers `Reinhard` and `Clamp` through the same pipeline (uniform-selected, quality-knob
precedent) as debugging and stylistic escapes.

### Decisions

- **D-H1 Default ON.** `PixelPostProcessSettings.Hdr` (`HdrSettings`) defaults `Enabled = true`.
  Games re-verify on repin, and the upgrade is the program's whole point. The escape hatch
  `Post.Hdr.Enabled = false` restores the legacy chain byte-identically (verified by a golden whose
  reference grids are literal copies of the pre-HDR `scene3d` grids).
- **D-H3 Chain order in HDR mode:** Bloom (pre-tonemap, over-range input) -> Tonemap -> Quantize ->
  Outline -> FXAA -> Blit. The retro passes and FXAA operate on tonemapped LDR values, the blit and
  everything after it (overlay renderers, Gui, 2D) is unchanged. Legacy mode keeps the historical
  order (Quantize -> Outline -> Bloom -> FXAA). Consequence documented: in HDR mode a
  bloom+quantize combination quantizes the halo (bloom composites before the palette pass), the
  historical anti-banding order is a property of legacy mode.
- **D-H4 Authoring surface = unclamped `Color`.** Weighed against per-vocabulary intensity fields:
  intensity fields would add N parallel knobs across Material/ParticleSprite/BeamStyle/Sky/Water,
  each needing plumbing, while every one of those paths ALREADY transports unclamped floats
  end-to-end. `new Color(4f, 2f, 1f)` or `color.ScaleRgb(3f)` is the idiom, documented in
  USING-KHAOZENGINE. Zero new API, works across every current and future color input.
- **D-H5 Display-referred tonemapping.** The engine's shading math stays in its existing
  display-referred space, the tonemap compresses that space's over-range. A full scene-linear
  conversion would re-grade every shipped scene and asset for a second time in one release and is
  not needed for the target look.
- **D-H6 Formats.** HDR mode: ColorTex/MsColor/PingA/PingB/BloomA/BloomB become
  `R16G16B16A16Float` (new `GpuPixelFormat` member). NormalTex (encoded normals) and DepthColorTex
  (R32Float linear depth) unchanged. The swapchain and everything post-blit stays LDR.
- **D-H7 Decal ceiling.** `DecalFrag`'s final-rgb clamp upper bound becomes a uniform (1.0 in LDR,
  float16-max in HDR) so telegraph energy lanes can exceed 1.0 and bloom. LDR stays bit-identical.
- **Bloom knobs unchanged.** `BloomSettings` keeps its fields and defaults. In HDR mode
  threshold/knee operate on pre-tonemap luma (can exceed 1), and the docs recommend a threshold at
  or above 1.0 there so only genuinely hot content halos. Bloom stays opt-in, so no silent
  default-behavior shift rides on this.

### Testing

- Headless: UBO layout size checks for the tonemap block, `ShaderValidation.ValidatePair` for
  (FullscreenVert, TonemapFrag), existing suite green in both modes.
- GpuFacts (behavior, Metal-local): emissive 6x brightens over 1x through ACES, bloom with
  threshold above 1 extracts only over-range content, HDR toggle round-trip renders byte-stable,
  MSAA 4x resolves float16, the alpha-lane starfield marker survives the tonemap pass.
- Goldens: full rebake of every 3D scene on all three backends (HDR default flips them all), plus
  three new pins: `scene3d_hdr_off` (grids pre-seeded as copies of the pre-HDR `scene3d` grids,
  proving the escape hatch is byte-identical), `scene3d_hdr_bloom` (the over-range emissive +
  pre-tonemap bloom payoff), `scene3d_hdr_msaa` (float16 MSAA resolve). The 0.20 cross-backend net
  and 0.06 grid tolerance are re-checked against the rebaked set.
- Showcase PNGs (no Golden in names): intensity ladder, operator comparison, HDR-vs-LDR composed
  scene, human-reviewed.

### Compatibility statement

- API purely additive (`HdrSettings`, `TonemapOperator`, `GpuPixelFormat.R16G16B16A16Float`,
  widened internal `RenderResources` signatures).
- Default behavior deliberately changes (HDR on): every consumer's rendered output shifts on repin,
  CHANGELOG carries the callout and the one-line escape hatch. With `Hdr.Enabled = false` output is
  byte-identical to 10.126.0, golden-proven.
- The alpha-lane contract, blit paths (MatchViewport mip/trilinear, FixedInternal single-tap),
  RenderScale semantics, and the post-post overlay/Gui/2D path are unchanged in both modes.

## Flipbook particles

Authored-atlas playback in the modern particle pass: a sprite can name a flipbook sheet (a grid of frame
cells packed into one texture) and a continuous frame position, and the fragment shader samples that frame
instead of a procedural shape, with optional motion-vector frame interpolation. This is the offline-simmed
half of the effect vocabulary (EmberGen-class smoke, fire, and explosion sheets) sitting beside the
procedural shapes, not replacing them. Builds on the 10.126.0 procedural pass and rides the same one-UBO,
sorted, premultiplied, bloom-feeding stream.

### D-F1: Flipbook is presentation (look-level)

The simulation (`KhaozEngine.Particles`) learns nothing about textures. A flipbook is renderer vocabulary,
same class as shape, blend, stretch, and light links, so it lives on the presentation side: the spec sits
on `ParticleSprite` (Render3D) and on `ParticleLook` (the `Particles.Render3D` adapter), never on the
`Particle` or `EmitterConfig`. A headless server references the sim with no atlas concept in scope, and the
sim's determinism story is untouched. The sim's per-particle `Seed` and life fraction are the only inputs
the adapter reads to drive frame timing, both already present.

### D-F2: One shader, dummy-texture zero-neutral

No pipeline fork. The particle pass stays a single pipeline and the procedural-vs-flipbook choice is
per-sprite, decided by the packed grid value in the instance stream (0 = procedural). A 1x1 white dummy
atlas and a 1x1 neutral motion sheet (0.5, 0.5 encoded) are bound for procedural runs, sampled statically
up front in binding order (the Metal rule), then discarded by the shader branch. A frame with no flipbook
sprites therefore renders byte-identically to before flipbooks existed: the sample happens, the result is
thrown away, and the procedural output path is untouched. The proof is that the committed
`scene3d_particles_modern` goldens stay green with no rebake (see Testing).

### D-F3: Motion-vector two-tap warp

Frame interpolation is the classic two-tap motion-vector warp. Frame A is sampled warped forward along its
encoded motion vector scaled by the blend fraction, frame B is sampled warped backward by (1 - blend), and
the two mix by blend. A motion sheet reads fluid at low frame counts where a plain cross-fade ghosts. The
key design property: a neutral motion texture (the (0.5, 0.5) encode = zero displacement) degrades the warp
to a plain cross-fade automatically, so "no motion sheet authored" needs no flag and no shader variant. An
absent `MotionTexture` binds the neutral dummy and the same math cross-fades. `MotionStrength` scales the
displacement (0 = plain cross-fade even with a real sheet bound).

### D-F4: Frame timing lives in the adapter

Render3D receives only a resolved continuous `FlipbookFrame` plus the spec, so the render vocabulary stays
policy-free. The `ParticleLook` adapter owns the timing via `ParticleFlipbookMode`:

- `LifeOneShot` (default): frame = life fraction swept across the sheet once, clamping on the last cell. For
  one-shot sheets (an explosion, an impact burst) where the sheet is the particle's whole life.
- `TimeLoop`: frame = effect time * `FlipbookFps`, wrapping at the seam. For continuous sheets (looping fire
  and smoke). `FlipbookRandomStart` (default true) staggers each particle's start frame by its `Seed` so a
  burst of identical looping sprites does not play in lockstep.

`Loop` on the spec (not the mode) controls whether the renderer's frame resolve wraps frame B across the
seam or clamps it, keeping wrap policy in one place. The pure resolver `ResolveFlipbookFrame` and the pure
`ResolveFrames` split (adapter picks the continuous position, renderer turns it into two integer indices
plus a blend) are both headless-tested.

### D-F5: Run-splitting preserves the global sort

The pass keeps ONE globally back-to-front sorted stream. Sorting correctness is not negotiable (alpha smoke
and additive glow interleave in that one stream), so the atlas cannot be a sort key. Instead the sorted
stream is split, after the sort, into contiguous runs keyed by atlas pair (atlas texture, motion texture),
one instanced draw per run at an instance-start offset into one packed buffer. This is the same-blend-run
precedent the ground-decal pass established. Procedural sprites carry the dummy pair, so a run of adjacent
procedural sprites merges into one dummy-pair draw and an all-procedural frame is exactly one draw (today's
single draw). No sprite is ever reordered across runs, so the global depth order survives verbatim. The
split is a pure `BuildRuns` helper, headless-tested (all-procedural = 1 run, interleaved atlas/proc/atlas =
3 runs in order, adjacent same-atlas merge).

### D-F6: IFlip instance packing (2^24-exact)

A sixth instance vec4 `IFlip` carries the flipbook per-sprite data: x = frame A index, y = frame B index,
z = blend, w = the packed grid and motion strength. The pack is
`cols + rows * 256 + qstr * 65536` where `qstr = round(clamp(strength, 0, 4) * 64)`. The implementer's
correction over the original plan: `qstr` is capped at 255, not left free. `clamp(strength,0,4) * 64`
reaches 256 at strength 4, and 256 * 65536 = 2^24, which is the first integer float32 cannot represent
exactly alongside the low cols/rows bits. Capping `qstr` at 255 keeps the whole packed value at or below
2^24 - 1 so every field stays bit-exact in the float32 lane and the shader's mirror mod/floor decode is
exact. Strength 4 quantizes to 255/64 (about 3.98) rather than corrupting the grid, a benign clamp at the
very top of the authored range. One documented encode in `PackFlipGrid`, one decode in the shader, a
headless round-trip test pins it. w > 0.5 is the procedural-vs-flipbook flag the shader branches on.

### Testing

The zero-neutral proof is that the committed `scene3d_particles_modern` goldens stay green on all three
backends with NO rebake: the dummy-texture path renders procedural sprites byte-identically, so a golden
baked before flipbooks existed still matches. A new `scene3d_particles_flipbook` golden pins the feature
itself (a generated atlas plus motion sheet, sprites at fixed frames including one mid-blend and one
motion-warped, interleaved with procedural sprites so run-splitting is exercised, over the dim floor with
effect time and seeds frozen), baked on metal, direct3d11, and vulkan. Behaviour GpuFacts cover frame
selection (frame 0 vs frame 10 read the matching atlas cells), cross-fade (a mid-frame reads a mix of both
neighbours), the motion-vector warp (an offset-encoding motion sheet vs the neutral sheet reads measurably
different, proving the taps moved), and byte-identical zero-neutral (a procedural scene renders the same
before and after an atlas is loaded but not used). Every test sheet is generated procedurally in-test
(distinct-hue cells, a known-offset motion encode), so the suite ships no asset files.

### Compatibility statement

Purely additive. New API: `ParticleFlipbook`, `ParticleSprite.Flipbook` / `FlipbookFrame`,
`ParticleLook.Flipbook` / `FlipbookMode` / `FlipbookFps` / `FlipbookRandomStart`, `ParticleFlipbookMode`.
Every field zero-defaults to the procedural path, so a sprite or look that never touches a flipbook renders
exactly as before (golden-proven). The only non-additive detail is internal: the particle instance stride
grows from 80 to 96 bytes for the extra `IFlip` vec4, invisible across the public API. Textures ride the
existing `Scene3D.LoadTexture` / `TextureHandle` registry with per-atlas-pair cached resource sets (the
textured-billboard precedent), so no new resource-ownership surface. SemVer minor.

## Screen-space distortion

Distortion sprites (heat haze, refractive shockwave rings, splash lensing) accumulate a signed screen-space
offset field, and the resolved scene colour re-samples through that field as the FIRST post-chain pass, so
refraction reads as an in-scene phenomenon that warps the pixels behind it. A sibling `DistortionRenderer`
(instanced quads, the modern particle pass's exact vertex expansion + depth-occlusion recipe) writes the
field, a fullscreen apply pass consumes it. Zero-neutral when unused: a frame that queues no distortion sprite
allocates nothing, clears nothing, runs no extra pass, and renders byte-identically to 10.129.0. This is the
last of the three Tier 1 sub-features, and it stacks on the HDR pipeline (warped hot cores still bloom) and
the particle vocabulary (a distortion look emits offset sprites instead of visible ones).

### D-S1: Apply pass is the FIRST chain pass, in both modes

The apply pass runs before every other post pass in both HDR and legacy mode:

- HDR: `Distort -> Bloom -> Tonemap -> Quantize -> Outline -> FXAA -> Blit`.
- Legacy: `Distort -> Quantize -> Outline -> Bloom -> FXAA -> Blit`.

Refraction is an in-scene phenomenon, so it precedes every camera-response pass. Bloom halos then follow the
warped sources (a heat-warped hot core still blooms around its warped position), the tonemap sees the warped
float scene, and the retro path quantizes the warped image. Placing it after those passes would warp already
posterized or already bloomed pixels, which reads wrong. The apply pass re-samples `ColorTex` (the chain
always starts there) into `PingA` and `src` follows the ping-pong from there like any other pass. It counts in
both post-chain parities (the blit's even/odd vertical-flip parity and the edge pass's `passesBeforeOutline`)
in both modes, so a distortion frame flips and outlines correctly.

### D-S2: Lazy half-res `R16G16Float` offset target

The offset buffer is a new `GpuPixelFormat.R16G16Float` member (Veldrid `R16_G16_Float`), allocated at
HALF-res, on the first frame that queues a distortion sprite (the bloom-allocation precedent), and freed on
the next resize when unwanted. Distortion is a low-frequency field, so half-res with a bilinear upsample is the
classic budget, and two channels (signed x/y offset) is all it carries (no alpha lane, so the accumulation
blend's alpha factors are inert). The target is cleared to zero only on frames that use it. `EnsureDistortion`
bumps `RenderResources.Generation` when it (re)allocates or frees, so cached resource sets over `DistortTex`
rebind. The lazy allocation lives in `RenderInternal` (per-frame, unlike bloom's resize-time decision), because
whether any sprite is queued is only known after the queues fill between `Begin` and `Render`.

### D-S3: Sibling queue, reusing the particle pass recipes

Distortion sprites are a SIBLING queue (`Scene3D.DrawDistortion(in DistortionSprite)` /
`DrawDistortions(ReadOnlySpan<DistortionSprite>)`, cleared each `Begin`), not particle-pass entries, because
they write signed offsets, not colour. The `DistortionRenderer` reuses the particle pass's proven recipes: the
`gl_VertexIndex` quad expansion, the camera-facing / flat-ground orientation basis, the `texelFetch` depth
occlusion with the background-marker skip, and `SoftFadeScale`. It obeys the same one-UBO contract: a single
set-0 frame uniform (clip-corrected ViewProj, raw InvViewProj, camera basis + eye + effect time, the
soft-fade / quality / marker params, plus the half-to-full texel ratio in `Params.w`), with every per-sprite
value on an instanced vertex-attribute stream (three contiguous vec4s) and the depth texture + point sampler
at bindings 1 and 2. It is ONE instanced draw with a plain additive blend `(One, One)`, so overlapping fields
sum, order-independently. The pipeline runs with NO depth test (the half-res target has no depth attachment):
occlusion is a fragment-side offset fade (reconstruct scene distance, fade the offset toward zero over the
soft-fade world units) rather than a discard, so edges stay soft. The depth texel coords scale from the
half-res `gl_FragCoord` up to full-res via the ratio in `Params.w`.

### D-S4: Three shapes

All procedural in the fragment shader (SDF / value noise, no texture), each masked by a footprint fade so quads
never hard-edge:

- `Ripple = 0`: a radial ring of outward offsets for shockwaves. `ShapeParam` is the ring band thickness (0
  tight, 1 fat).
- `Heat = 1`: an upward-scrolling value-noise wobble over the sprite footprint (heat haze). `ShapeParam` is the
  noise frequency, and the `hash21` / `vnoise` idiom is used throughout (never a `sin`-hash). The second octave
  is a uniform shader branch dropped under the reduced quality tier.
- `Lens = 2`: a smooth radial bulge for splash lensing. A positive `Strength` magnifies (pulls pixels inward), a
  negative one pinches (pushes them outward), and `ShapeParam` softens the falloff shoulder.

### D-S5: Own-alpha preservation for the starfield marker

The apply pass preserves each destination pixel's OWN alpha: it emits `vec4(warpedColor.rgb, ownSample.a)`,
sampling the source at `vUv` for the alpha and at the warped `vUv + duv` for the colour. Warping the colour is
the effect. Warping the alpha-lane background marker (clear a=0, geometry/sky a=1) would corrupt the blit's
starfield / transparency semantics, so the marker is read straight and never displaced.

### D-S6: Quality knob

`Scene3D.DistortionQuality { Full = 0, Reduced = 1 }` is a host-set knob (the `ParticleQuality` precedent, not
cleared by `Begin`). `Reduced` drops the Heat shape's second noise octave and renders the offset buffer at
quarter resolution instead of half (the `EnsureDistortion` divisor is 4 rather than 2). `Full` is the default.

### D-S7: UV scale + max-excursion clamp constants, host tunes via `Strength`

The stored offsets are authored in world-ish units (each sprite's `Strength` baked in) and converted to a UV
excursion by the apply pass's fixed texel scale `DistortionUvScale` (0.04), then clamped to a max UV excursion
`DistortionMaxExcursion` (0.05) so a hot mess of stacked sprites cannot smear the whole screen. Those two are
fixed internal constants, not knobs: the host tunes magnitude per sprite via `DistortionSprite.Strength`, which
keeps one intuitive authoring dial and a hard safety ceiling. The warped sample UVs clamp to the viewport edge
(half a texel in) so the warp never reads outside the image.

### D-S8: `DistortionLook` phases (the adapter)

`Particles.Render3D` gains `DistortionLook { DistortionShape Shape; float ShapeParam; float Strength; float
SoftFadeScale; }` and `ParticleLook` gains a `DistortionLook Distortion` field. It is inactive by default
(all-zero, `Strength == 0`, `IsActive` false) and active when `Strength != 0`. An active-distortion phase emits
one `DistortionSprite` per live particle INSTEAD of a visible `ParticleSprite`, so the phase warps the scene
rather than drawing over it, with each sprite's strength scaled by the particle's current alpha so the field
fades with the particle's life. `VfxPresets.Shockwave` gains a refraction-ring phase (a flat-ground `Ripple`
that expands with the nova), and a new `VfxPresets.HeatHaze` preset pairs a `Heat` distortion column with a
faint additive warm shimmer so the effect reads even on a flat background where refraction alone is invisible.

### Testing

- Zero-neutral proof: the committed `scene3d`, `scene3d_particles_modern`, and `scene3d_hdr_bloom` goldens stay
  GREEN on all three backends with NO rebake. A frame that never queues a distortion sprite is byte-identical to
  before distortion existed, so a golden baked before this release still matches.
- Behaviour GpuFacts (Metal-local): a `Ripple` sprite displaces pixels while a control region far from it is
  identical, a wall between camera and sprite occludes the offsets to zero (the depth recipe), a `Heat` sprite
  over the starfield boundary leaves the background stars and the geometry cells intact (own-alpha preservation),
  a queued-then-cleared frame is byte-equal to a never-queued one (zero-neutral), and the `Reduced` tier renders
  without throwing.
- New golden `scene3d_distortion` (a textured checkerboard floor plus one `Ripple`, one `Lens`, and one `Heat`
  sprite at fixed positions, frozen effect time and seeds, HDR default on), baked on metal, direct3d11, and
  vulkan. Showcase PNGs (no `Golden` in the name) cover the shockwave, heat-over-a-bloomed-sphere, and lens
  trio, human-reviewed.

### Compatibility statement

Purely additive. New API: `DistortionShape`, `DistortionSprite`, `DistortionQuality`, `Scene3D.DrawDistortion`
/ `DrawDistortions` / `DistortionQuality`, `GpuPixelFormat.R16G16Float`, and the adapter's `DistortionLook` /
`ParticleLook.Distortion` / `VfxPresets.HeatHaze`. Nothing existing changes behaviour: with no `DrawDistortion`
call and an inactive look, output is byte-identical to 10.129.0 (golden-proven). The alpha-lane contract, the
blit paths, `RenderScale` semantics, and the post-post overlay / Gui / 2D path are untouched. SemVer minor.
