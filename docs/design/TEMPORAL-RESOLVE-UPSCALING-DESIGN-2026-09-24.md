# Temporal resolve and upscaling

Status: design, awaiting owner review.
Date: 2026-09-24.
Issue: [#1149](https://github.com/APKiwiOrg/KhaozEngine/issues/1149), rounds 2 and 3 of 3.
Builds on: [`TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md`](TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md), round 1, approved.
Consumer: Grimhollow. Its adoption is round 3, specified at the end of this document.

## Outcome

`AntiAliasing.Temporal` joins Off, FXAA, MSAA and SSAA. It renders the scene at an internal resolution chosen as a
fraction of the display, jitters each frame, and reconstructs a stable image at the display's full resolution from
the jittered frames and their history. Thin, moving, sub-pixel detail such as distant grass holds still while the
camera moves, edges are anti-aliased without MSAA's per-sample cost, and the output is sharper on high density
displays than today's fixed 1600x900 stretched to the window.

The owner's decisions from round 1 apply: upscaling included, stability over crispness with a light sharpen, and a
player quality setting. Rounds 1 and 2 ship as one engine release. Grimhollow adopts it in round 3.

## Why the budget matters

On the owner's Mac, `MSAA 4x` took Grimhollow from about 180 to about 120 frames per second, roughly 2.8 ms a
frame, five times the offscreen estimate. It removed most of the shimmer and left a trace under camera motion.
Temporal anti-aliasing replaces four samples per pixel with one resolve pass, and upscaling lowers the number of
shaded pixels, so the default preset has to beat MSAA 4x on both stability and frame time. That is an acceptance
line, not an aspiration.

## 1. Where the resolve sits in the frame

```
opaque passes (jittered, internal res)  -> colour, depth, motion (round 1)
  copy colour                           -> opaque-only colour, for the reactive estimate
transparents, decals, water, particles  -> colour (internal res)
temporal resolve (internal -> display)  -> display-res HDR colour, new history
distortion, bloom, tonemap              -> display res
sharpen                                 -> display res
target outlines, overlays, final blit   -> display res, 1:1
```

Everything before the resolve runs at the internal size. Everything after it runs at the display size. Distortion
moves from the start of the post chain to just after the resolve, so heat haze bends the stable image rather than
being accumulated into history. Target outlines and overlays already draw onto a target of a different size from the
internal one, so they keep their current sampling. Pixelated mode and temporal mode are exclusive, and anti-aliasing
resolution refuses the pair.

## 2. Render size and presets

A new `RenderScale.Temporal` sizing mode, forced while `AntiAliasing.Temporal` is active in the same way SSAA forces
`MatchViewport`, sizes the internal target as the display framebuffer times a ratio on each axis, clamped by
`MaxRenderWidth` and `MaxRenderHeight`.

| Preset | Ratio per axis | Pixels shaded against display |
| --- | --- | --- |
| `Native` | 1.0 | 100% |
| `Quality` | 1 / 1.5 | 44% |
| `Balanced` | 1 / 1.7 | 35% |
| `Performance` | 1 / 2.0 | 25% |

`Post.Temporal.Upscale` takes a preset or an explicit ratio from 0.5 to 1.0. A ratio change resets history, per round
1. Dynamic resolution, which would change the ratio every frame without a reset, is out of scope, and the resolve's
inputs are sized per frame so it can be added later.

## 3. The resolve

One fullscreen fragment pass at display resolution. Fragment rather than compute keeps to the engine's proven path,
since the GPU seam has no compute barrier and compute to graphics handoff has to stay inside one command list.

Inputs: the internal colour after transparents, the opaque-only colour copy, depth, motion vectors, the previous
frame's depth and the history. Outputs: the display-resolution colour and the new history.

1. **Motion with dilation.** For each display pixel, find the internal pixel under it and take the motion vector of
   the closest depth in its 3x3 neighbourhood. Edges of moving objects then carry the object's motion rather than the
   background's, which is the main cause of edge ghosting. Background pixels, marked by round 1's sentinel, reproject
   from camera rotation alone.
2. **History fetch.** Sample the history at the reprojected position with a 5-tap Catmull-Rom filter. Bilinear history
   sampling blurs a little more every frame, and a stability-first filter keeps history for many frames.
3. **Disocclusion.** Reproject the pixel's linear depth and compare it with the previous frame's depth at the
   reprojected position. Beyond a relative tolerance the pixel was hidden last frame, and its history weight drops to
   zero. A reprojected position off screen does the same.
4. **Current sample reconstruction.** Gather the 3x3 internal samples around the display pixel and weight each by a
   Lanczos 2 kernel on the distance from its jittered sample position to the display pixel centre, measured in
   internal pixels. That is what turns jittered low resolution frames into a higher resolution image, and at `Native`
   it reduces to plain temporal anti-aliasing.
5. **Neighbourhood clipping.** Convert to YCoCg and build a variance box, mean plus or minus gamma times the standard
   deviation, over the 3x3 internal neighbourhood. Clip the history towards the box centre, not a clamp to its
   corners, which keeps colour. Gamma widens where the pixel's motion is small and tightens as it grows, so a still or
   slowly moving view keeps more history. That is the stability-first choice.
6. **Thin feature retention.** A pixel whose luma has been stable in history but falls outside a box built from a
   single thin feature would be clipped away every frame, which is exactly distant grass. A per-pixel luma stability
   term, stored with the history confidence, holds such pixels against clipping while their reprojected history
   stays consistent, and releases them on disocclusion, reactive content or large motion.
7. **Reactive estimate.** The luma difference between the opaque-only copy and the final colour marks pixels that
   transparent content changed: particles, billboards, beams, trails, decals and water. Those pixels take a lower
   history weight, so a smoke puff or a splash does not leave a trail. An explicit reactive input can override the
   estimate later without changing the pass.
8. **Accumulation.** Blend the clipped history and the reconstructed current sample in a luma-weighted space, dividing
   by one plus luma before and restoring after, so a bright firefly cannot ghost. The current weight follows the
   accumulated sample count stored as history confidence, from one on a reset down to a floor of about one in
   sixteen, raised by the reconstruction weight of step 4, the reactive estimate and disocclusion.

History is a display-resolution colour target and a confidence and stability target, double-buffered in round 1's
`TemporalHistory`. Colour uses R11G11B10 float where all three backends can render and sample it, and RGBA16F
otherwise. The plan's first task records which. The previous frame's depth is an internal-resolution R32F copy kept
by the same owner.

## 4. Sharpening

A robust contrast adaptive sharpen runs after tonemapping at display resolution. It limits each pixel's sharpening
by its neighbourhood's local contrast, so it cannot ring or reintroduce the shimmer the resolve removed.
`Post.Temporal.Sharpness` runs from 0 to 1 with a default of 0.25, which is light, in keeping with stability first.

## 5. Texture detail under upscaling

A texture sampled at the internal resolution picks a blurrier mip level than the display needs. Every material
sampling site applies a mip bias of `log2(internal / display) + Post.Temporal.MipBiasOffset` through a frame uniform,
using the shader's own `bias` argument, because Metal samplers have no LOD bias. The offset defaults to minus 0.5,
tuned in the plan against a checkerboard golden, and the bias is zero when temporal is off, so today's output is
unchanged.

## 6. Public API

- `AntiAliasing.Temporal`, a new mode, and `AntiAliasingMode.Temporal`. Anti-aliasing resolution refuses it with
  Pixelated and never combines it with MSAA.
- `Post.Temporal.Upscale` (`TemporalUpscale.Native`, `Quality`, `Balanced`, `Performance`, or a ratio),
  `Post.Temporal.Sharpness` and `Post.Temporal.MipBiasOffset`, beside round 1's cut thresholds.
- `Scene3D.LastTemporalDiagnostics` gains the internal and display sizes, the preset, and per-frame counts of
  disoccluded, reactive and clipped pixels, sampled on a coarse grid so reading them costs nothing measurable.
- `Scene3D.DebugView` gains `History`, `Disocclusion` and `Reactive`.

## 7. Testing and acceptance

GPU tests run on all three backends through round 1's multi-frame fixture, with deterministic time and camera paths.

1. **Convergence.** A static scene of thin geometry converges to within tolerance of an 8x supersampled reference
   after 32 frames.
2. **Stability.** A slow camera pan over thin geometry flickers less, by the grass probe's flip metric, than MSAA 4x
   on the same path. This is the owner's complaint as a test.
3. **Ghosting.** A keyed object crossing the view leaves no trail longer than one display pixel two frames after it
   passes. A particle burst over a moving background does the same through the reactive estimate.
4. **Disocclusion.** A revealed background shows no history from the object that covered it.
5. **Cuts and resets.** The frame after `CameraCut`, a resize or a preset change matches a from-scratch render.
6. **Upscaling.** At `Quality` a converged still frame is closer to a native reference than the same frame bilinearly
   upscaled, on a resolution chart golden.
7. **Mip bias.** A textured checkerboard at `Performance` keeps the native reference's detail within tolerance.
8. **Byte identity off.** With temporal off, every committed golden is unchanged.
9. **Cost.** On the Grimhollow town and meadow paths on Apple silicon, the resolve and sharpen together cost under
   1.0 ms at 2560x1440 display. The default preset's whole frame beats MSAA 4x's frame time, measured in the
   Grimhollow probe and confirmed by the owner's windowed playtest.
10. **No steady allocation** with temporal active.

## Round 3: Grimhollow adoption

Round 3 runs in Grimhollow on the released engine and has its own implementation plan. Its design is set here so
rounds 1 and 2 serve it.

1. **Motion keys** on everything that moves. The avatar's rigid parts derive keys with `MotionKey.Combine` from the
   body's session id and the part index, for the local player and every remote body. Monsters, held items and any
   other body part do the same. Carcasses, ground drops and placed props are static and need none. Foliage and
   particles are handled by the engine.
2. **Settings.** `Anti-aliasing` gains `TAA`. A new `Render quality` row offers `Native`, `Quality`, `Balanced` and
   `Performance`, and applies only while `TAA` is selected. The grass wind fade keeps following the anti-aliasing
   choice, with its `TAA` value set by measurement.
3. **Default.** A moving-camera version of the shimmer probe measures `FXAA`, `MSAA 4x` and `TAA` at each preset on
   the town, forest and meadow paths, for flicker and for frame time. The recommended default is the cheapest `TAA`
   preset that beats `MSAA 4x` on flicker, and the owner's playtest confirms it before it ships.
4. **Ledger and changelog.** `docs/ENGINE-INTEGRATION.md` records the adoption and the measured table, and the staged
   player changelog entry describes the new options.

## Out of scope

- Dynamic resolution. The resolve takes per-frame input sizes so it can follow.
- Machine-learned upscalers, and vendor upscalers such as MetalFX, DLSS or FSR. A `ITemporalUpscaler` seam is not
  added until a second implementation exists.
- Motion blur, screen-space reflections and temporal ambient occlusion, which reuse round 1's parts later.

## Risks

1. **Thin feature retention is the hardest part to tune.** Too strong and moving grass smears, too weak and it
   shimmers. The stability test and the ghosting test pin both sides, and the owner's playtest is the last word.
2. **History memory at high display resolution.** A 3456x2234 display holds two history colour targets and two
   confidence targets. R11G11B10 keeps that near 80 MB.
3. **Moving distortion after the resolve** changes the look of heat haze slightly. The distortion goldens are rebaked
   with that cause named.
4. **Every material sampling site takes the mip bias.** A missed site shows as one blurrier material under upscaling.
   A test enumerates the material programs and requires the bias uniform in each.
5. **The shader list and hash table cost** of round 1 applies again for each new program.

## Plan amendments

The implementation plan, [`2026-09-24-temporal-aa.md`](../superpowers/plans/2026-09-24-temporal-aa.md), read the code
and changed these details. Each group's "Contract amendments" block carries the evidence.

1. A fifth preset, `UltraPerformance` at 1/3 per axis (72 jitter phases), and `UpscaleRatio` from 0.33 to 1.0. At a
   3456x2234 display even `Performance` shades more pixels than a fixed 1600x900 target, so the frame-time line needs a
   cheaper preset (group E).
2. History colour is RGBA16F with RG16F confidence and stability, not R11G11B10, which is not a seam format, is not a
   guaranteed Vulkan render target, and whose mantissa stalls a 1/16 blend. At a 3456x2234 display on `Quality` the
   history holds about 213 MB, plus about 155 MB of display-size post targets, against risk 2's estimate (group E).
3. Previous depth is two linear view depth targets written by `TemporalDepthStoreFrag`, not a copy of the depth target
   (group E).
4. `PixelPostProcess` takes an `IPostChainTargets` interface so the chain runs at display size (group E).
5. In temporal mode the background draws before the transparents that render into the model target. With temporal off
   the order is unchanged, and its existing sky-over-transparents bug is
   [#1153](https://github.com/APKiwiOrg/KhaozEngine/issues/1153) (group E).
6. History targets survive `Invalidate` and are released only when the resolve stops (group E).
7. The mip bias rides the free `Params.z` and `Params.w` lanes of the frame block, and the explicit-gradient ground taps
   scale their gradients by the bias instead of taking a bias argument (group F).
8. Temporal counts are sampled on request through `Scene3D.RequestTemporalCounts()`, because every backend's readback
   drains the device (group F).
9. A screen-fixed starfield background takes zero motion instead of the sky's rotation reprojection (Task F16a).
10. In temporal mode the toon edge outline runs on the internal images before the resolve, so its lines are
    accumulated rather than jittered. A non-black outline colour mixes before the tonemap there (Task F16b).
11. Round 3 keys carcasses, which move through their 0.9 s collapse (group I).
12. The release version is chosen at release time by the ride rule: an untagged staged minor is ridden (Task H5).
