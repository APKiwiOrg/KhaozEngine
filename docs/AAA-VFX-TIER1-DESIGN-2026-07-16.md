# AAA VFX Program Tier 1 Design

Date: 2026-07-16. Branch: `feature/aaa-vfx-tier1`. Status: approved for implementation (owner-approved
program, `docs/ROADMAP.md` "AAA VFX program", autonomous session with delegated design authority).
Builds directly on the 10.126.0 particles modernization
(`docs/PARTICLES-VFX-DESIGN-2026-07-16.md`).

Three sub-features, shipped as three sequential releases from one worktree, HDR first because the
other two look dramatically better on top of it:

1. HDR pipeline + filmic tonemap + HDR bloom (this document, section "HDR").
2. Flipbook particles with motion-vector blending (section added with its release).
3. Screen-space distortion pass (section added with its release).

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
