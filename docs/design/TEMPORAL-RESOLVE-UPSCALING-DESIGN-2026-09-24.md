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
| `UltraPerformance` | 1 / 3.0 | 11% |

`Post.Temporal.Upscale` takes a preset. `Post.Temporal.UpscaleRatio`, when set, overrides it with an explicit ratio
from 0.33 to 1.0. A change of the ratio in effect resets history, per round 1. Dynamic resolution, which would change
the ratio every frame without a reset, is out of scope, and the resolve's inputs are sized per frame so it can be added
later.

## 3. The resolve

One fullscreen fragment pass at display resolution. Fragment rather than compute keeps to the engine's proven path,
since the GPU seam has no compute barrier and compute to graphics handoff has to stay inside one command list.

Inputs: the internal colour after transparents, the opaque-only colour copy, depth, motion vectors, the previous
frame's depth and the history. Outputs: the display-resolution colour and the new history.

1. **Motion with dilation.** For each display pixel, find the internal pixel under it and take the motion vector of
   the closest depth in its 3x3 neighbourhood. Edges of moving objects then carry the object's motion rather than the
   background's, which is the main cause of edge ghosting. Background pixels, marked by round 1's sentinel, reproject
   from camera rotation alone. Beside a fast edge, where the nearer surface itself moved, a pixel on the farther
   surface keeps its own motion. Under that reach, beside a wide nearer surface that moved while the farther one moves
   more on screen, as under a camera following an avatar, the pixel keeps the dilated motion and marks its history
   as the edge's (amendment 23).
2. **History fetch.** Sample the history at the reprojected position with a 5-tap Catmull-Rom filter. Bilinear history
   sampling blurs a little more every frame, and a stability-first filter keeps history for many frames.
3. **Disocclusion.** Reproject the pixel's linear depth and compare it with the previous frame's depth at the
   reprojected position. Beyond a relative tolerance the pixel was hidden last frame, and its history weight drops to
   zero. A reprojected position off screen does the same. A moving surface skips the test (amendment 17). A mostly
   covered footprint counts as hidden unless a still narrow feature covered it or the pixel carries a lock a ridge
   refreshed on the last frame, and one a moving surface showed at counts as hidden unless it is whole. A still
   pixel clear of any edge drops a history marked as an edge's (amendment 23).
4. **Current sample reconstruction.** Gather the 3x3 internal samples around the display pixel and weight each by a
   Lanczos 2 kernel on the distance from its jittered sample position to the display pixel centre, measured in
   internal pixels. That is what turns jittered low resolution frames into a higher resolution image, and at `Native`
   it reduces to plain temporal anti-aliasing. The kernel is separable (amendment 18).
5. **Neighbourhood clipping.** Convert to YCoCg and build a variance box, mean plus or minus gamma times the standard
   deviation, over the 3x3 internal neighbourhood. Clip the history towards the box centre, not a clamp to its
   corners, which keeps colour. Gamma widens where the pixel's motion is small and tightens as it grows, so a still or
   slowly moving view keeps more history. That is the stability-first choice. Beside a fast narrow moving feature,
   the feature's share of a pixel's own history takes its current colour, save the share a held lock keeps
   (amendment 23).
6. **Thin feature retention.** A pixel whose luma has been stable in history but falls outside a box built from a
   single thin feature would be clipped away every frame, which is exactly distant grass. A per-pixel luma stability
   term, stored with the history confidence, holds such pixels against clipping while their reprojected history
   stays consistent, and releases them on disocclusion, reactive content or large motion, and at moving edges
   (amendment 19) unless the pixel reprojected by its own motion and kept its history (amendment 23).
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
- `Post.Temporal.Upscale` (`TemporalUpscale.Native`, `Quality`, `Balanced`, `Performance` or `UltraPerformance`), the
  nullable `Post.Temporal.UpscaleRatio` that overrides it, `Post.Temporal.Sharpness` and
  `Post.Temporal.MipBiasOffset`, beside round 1's cut thresholds.
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
13. The distortion offset field keeps its internal-relative size. Only its apply pass moves to the display resolution,
    after the resolve (group E).
14. A frame resolves on its first render only. A later render inside the frame, such as an offscreen capture, may use
    another camera or size while its motion pairs with the first render's previous view, so it is never resolved. It
    renders unjittered and runs the internal post chain, bloom included, on a post chain of its own, so it looks like a
    render without temporal anti-aliasing at the internal size. It leaves the history targets, their pair and their
    contents untouched, and neither post chain is rebound per frame (group E).
15. Under the resolve the internal targets carry no bloom or ping pair, because the display chain has its own. The first
    later render at the same internal size adds both in place, never by recreating targets an earlier render in the
    frame still reads, and they stay until temporal anti-aliasing turns off, so a host that captures every frame at that
    size reallocates nothing per frame. A later render at another size resizes the internal targets, which recreates
    them ([#1167](https://github.com/APKiwiOrg/KhaozEngine/issues/1167)) (group E).
16. The frozen layer of the screen dissolve (`TransitionRenderer`) is captured from the internal colour before the post
    chain in every mode, so under the resolve it holds an unresolved internal frame
    ([#1166](https://github.com/APKiwiOrg/KhaozEngine/issues/1166)).
17. Step 3's depth test is one-sided and its expected depth assumes a static point, so it runs only where the dilated
    texel's motion carries its own sample within `MovingSurfaceInternalPixels` (half an internal pixel), plus
    `MovingSurfaceMotionFraction` (1/1024) of that motion, of the UV the camera alone gives that point. A surface that
    moved skips the test. Under a perspective camera so does a static point on or behind last frame's camera plane. Under
    an orthographic camera that point keeps the test and is rejected as disoccluded, because its expected depth is not in
    front. A surface moving out from behind a static occluder therefore keeps no disocclusion test and relies on
    neighbourhood clipping (step 5).
18. Step 4's kernel is the product of two 1D Lanczos 2 weights, one per axis, not a radial Lanczos 2 on the distance.
    The same product at display scale gives the current sample's weight.
19. Step 6's lock also releases at a moving edge, where the centre texel moves otherwise than the dilated nearest
    surface: it is scaled down by `LockEdgeRelease` per internal pixel of that difference. Where the difference passes
    `LockEdgeMotionFraction` of the dilated motion and `LockEdgeFloorInternalPixels`, and all four stored previous
    depths lie farther than the moving surface's expected depth by more than the disocclusion tolerance, the history
    there is a farther surface's, and the lock read with it is dropped before a ridge can refresh it.
20. The sharpen runs only on the display chain of a resolving frame. A later render inside the frame runs the internal
    chain and is never sharpened, and neither is a frame with temporal anti-aliasing off or with a sharpness of 0, so
    their output is byte-identical to a chain without the pass. `Sharpness` above 1 counts as 1, and a negative or NaN
    value as 0. The pass limits its lobe so that input in 0 to 1 stays there, and clamps its output to 0 to 1. In the
    HDR order it runs directly after the tonemap, whose three operators all leave colour in 0 to 1, so it reads the
    display-referred image RCAS expects and its clamp changes nothing. The legacy order has no tonemap, so there it runs
    first, after the distortion apply. Its scene colour is 8-bit and already display-referred, but the resolve writes
    half float floored at 0 and not clamped at 1, so it can leave a channel slightly above 1. The limiter gives no lobe
    to a pixel with a tap above 1 in its cross, and the clamp cuts that overshoot to 1, which the 8-bit ping the pass
    writes would cut anyway. In both orders it precedes palette quantize and the edge outline, so it never sharpens a
    palette step or an outline, and it counts in both flip parities (group F).
21. Acceptance 1 is measured with HDR off, on the legacy chain, which has no tonemap. The resolve, MSAA and the 8x
    supersampled reference then all average the same display values, so the order of averaging and tonemapping cannot
    matter. The resolve blends in the luma-weighted space `c / (1 + luma)` as its firefly protection, so a high-contrast
    thin feature converges to the luma-weighted average of its samples, which is darker than their box average. A white
    bar a third of a pixel wide on black reads about 0.2 where the box average gives 0.33. That is by design. So the
    mid-contrast thin geometry gate compares with the plain box-filtered reference, and the high-contrast gate compares
    with a reference averaged the same way the resolve averages. Both gates hold on every frame from 32 to 48, not on
    one frame alone (group F).
22. Acceptance 2 is not measured by the grass probe's flip metric, because raw flips reward blur: a thin feature
    crossing a pixel flips it under every mode, an ideal filter included, and only an image smoothed over time flips
    less. Stability is measured with HDR off against a per-frame supersampled reference sequence of the same camera
    path. The slow pan over thin geometry is asserted as added change, the frame-to-frame change the output makes and
    the reference does not, compared with MSAA 4x's. Sharpness floors guard the blur that added change cannot see, and
    a shimmer guard keeps the temporal error within 1.15 times a frozen image's. The default isometric camera's zoom is
    asserted as its temporal error after a 5 by 5 low-pass, against a one-frame-lag control (the reference sequence one
    frame late) and against no anti-aliasing.
23. Step 1's dilation and step 3's depth test give way beside a fast moving edge and around a revealed place. Where the
    dilated nearest surface moved (its depth test is skipped), the centre texel lies farther than it by more than the
    disocclusion tolerance, and the two move more than `DilationReachInternalPixels` (1.25 internal pixels) apart, the
    dilated motion carries the pixel onto another texel of the farther surface, so the pixel reprojects by its centre
    texel's own motion and depth. A still nearer surface keeps dilation, under a camera's translation and against the
    sky alike. Step 3 also disoccludes a pixel whose expected surface shows under `DisocclusionVisibleShare` (half) of
    the bilinear weight of its four stored depths, unless the nearest of them is narrow, its run of texels along a row
    or a column at most two long, as sub-texel blades alone or side by side are, and no moving surface showed there last
    frame, or the lock the pixel carries lies within half of `LockDecay` of whole, which a ridge refreshed on the last
    frame, as a still blade's is on the frame after the jitter showed it. The stored lock records that fact: where the
    surface the pixel reprojected by moved, the dilated nearest or, beside a fast edge, the centre texel's own, the
    confidence and stability target holds minus one minus the lock, and minus three minus the lock where the pixel
    followed a nearer surface's edge (the band, below), and both read back unchanged. Any lock
    whose hold is whole also kept the history where a ridged, textured keyed box crossing the textured wall at 2 display
    pixels a frame left, since its motion releases only a third of its locks: 10 and 39 of 420 trail pixels at Native
    and Quality, against 3 and 4 now. Before, a keyed box crossing a textured wall at 4 display pixels a frame left the
    column behind its trailing edge holding a wall texel 4 pixels away at full confidence, and kept its own colour in
    the ring of pixels around its old place, whose footprints reached past its edge: 63 and 180 of 840 trail pixels at
    Native and Quality passed a freshly revealed wall's difference by more than 0.05, against 1 and 0 now. The cost
    falls on a moving object's edge pixels whose centre texel lies on the farther surface. They now read their own
    history, not the edge carried along. A keyed box tilted and crossing the flat wall at 2 internal pixels a frame
    averages a luma error of 0.00146 and 0.00173 over its edges at Native and Quality against 0.0008 and 0.0014, and the
    temporal error over the band it crosses rose from 0.00085 and 0.00168 to 0.00208 and 0.00260. A keyed line one
    internal pixel wide kept only 0.26 of its reference energy over its own coverage at both presets, where dilation
    kept 1.06 and 0.94 but smeared it to 2.09 and 1.28 over the band with an 8 pixel trail at Native, until the moving
    share's colour described below gave it back. The energy summed over the band is no measure of the line, because it
    also counts luma the pixels the line left keep, where the reference shows only background: while the narrow
    exception kept their history it read 0.69 and 0.43 against 0.26 over the coverage. At the moving edges of the 30
    pixel box on the flat wall fast flips rose from 0.0000 and 0.0007 to 0.0061 and 0.0071 at Performance and
    UltraPerformance, with UltraPerformance's added change rising from 0.0034 to 0.0042. A keyed line one or two
    internal texels wide that moved on at 2 internal pixels a frame is as narrow as a still blade the jitter missed, and
    while the narrow exception took any narrow feature it kept the line's colour where it left over the textured wall:
    128 and 177 trail pixels at Native and Quality for the line one texel wide and 50 and 136 for two, against
    acceptance 3's 2 and 3. The stored state's veto brought them to 11 and 23, and 0 and 3, and the moving share's
    colour to 0 and 8, and 0 and 0, passing a freshly revealed wall's difference by more than 0.05. The line one texel
    wide kept 8 at Quality because its colour reaches its neighbours through the reconstruction, and over a grey texture
    the clip, whose chroma range is nothing, pulls such a pixel's luma to the neighbourhood mean at full confidence
    ([#1186](https://github.com/APKiwiOrg/KhaozEngine/issues/1186)). It keeps 1 since a moving surface's footprint holds
    its history only whole, as below, and `TemporalNarrowCrossingGpuTests` holds it at acceptance 3 at both presets.
    A pixel right behind a fast trailing edge that reprojects by its own motion was
    covered, so it restarts from the current sample, which the reconstruction tints with the object's colour on the
    jitter phases that put the object's edge texel within a pixel of it. The clip removes that tint over a flat or grey
    wall, but over a textured wall whose colour varies 6 and 10 of the 840 trail pixels of a keyed box crossing at 4
    display pixels a frame keep it two frames after the box uncovered them at Native and Quality
    ([#1187](https://github.com/APKiwiOrg/KhaozEngine/issues/1187)). Beside a fast narrow feature the pixel's own
    history misses the feature. Where a pixel's centre texel misses a keyed line one internal pixel wide crossing the
    flat wall at 2 internal pixels a frame, the pixel reprojects by its own motion onto converged wall, the clip box
    spans wall and line so it keeps that wall, and the current sample adds the line at about a sixteenth, while a pixel
    whose centre texel is the line reads history along the line's motion from a pixel as dim on the frame before. The
    line kept 0.26 of its reference over its own coverage, against 0.999 under MSAA 4x at the display size. So where the
    pixel reprojected by its own motion and the nearer surface is at most two texels wide in this frame's depth, the
    share of the reconstruction weight on texels nearer than the pixel's own surface takes their current colour in the
    clipped history before step 8's blend, and the pixel stores `MovingShareConfidence`, no confidence, so it restarts
    once the feature has moved on. The line keeps 1.05 and 0.97 now, 1.054 and 0.969 of MSAA 4x's, and its band's
    temporal error is 0.00155 and 0.00202 against 0.00288 and 0.00376. Kept whole, the confidence left the two-texel
    line 7 of its 630 trail pixels at Quality, and scaled by one minus the share 6, against 0 now, because over a grey
    texture the clip pulls a pixel holding the line's colour to the neighbourhood mean at full confidence. Beside any
    moving surface rather than a narrow one, the colour raised the tilted box edge's fast flips at Native from 0.00058
    to 0.00112. The narrow test reads the run through one texel, so it also fires where a wide object is one or two
    texels across in this frame's depth, at a corner's tip or on a face seen at a grazing angle: it raised the tilted
    box's fast flips from 0.00058 to 0.00068 at Native and from 0.00168 to 0.00186 at Quality, within the bounds of
    0.00088 and 0.00273. The rule costs two fetches for every pixel, the centre texel's own depth and motion read before
    the 3x3, and sixteen for the narrow test behind rule 1's branch. The emitted resolve grew from 33367 to 38149 bytes
    of HLSL with it and the kept lock below, to 41801 with the fast flag and the held share, and to 42551 with the
    whole-footprint rule on every moving surface and the band. Its hardware cost has
    not been measured: section 7's cost acceptance measures it with the rest of the resolve's (group F, F15). The line's
    fast flips are 0.019 and 0.022, about MSAA 4x's 0.021 and 0.022. Each pixel shows the line for about one frame, so
    every flip of the reference is fast, and against half the reference's raw flips the reference itself, scaled to a
    share of its own contrast, stayed within the bound only up to 0.30 at Native and 0.20 at Quality. So the fast-edge
    facts bound fast flips by the larger of half the reference's flips and 1.25 times its fast flips, 0.022 and 0.024
    for the line, and `TemporalFastEdgeGpuTests` holds the line at 0.8 of its reference's and 0.85 of MSAA 4x's coverage
    energy. A pixel that reprojected by its own motion and kept its history also keeps its lock at a moving edge, where
    step 6 released every lock (amendment 19), since the history it holds is its own, a moving surface's footprint
    keeping its history only whole, as below. A still line three eighths of a texel wide, with a keyed 30 display
    pixel box sliding past one internal texel away at 2 display pixels a frame, fell on its worst frame to 0.01 of its
    energy without the box at Native and to 0.00 to 0.25 at Quality, and was still at 0.43 to 0.69 sixteen frames after
    the box passed. It keeps 0.99 now. The stored moved flag played no part there: storing it only where a pixel
    followed the moving surface changed nothing. A pixel that restarted still lets go, because the moving surface's
    colour can reach its ridge through the reconstruction, and keeping that lock too left the ridged keyed box 6 and 8
    trail pixels against 3 and 4. Where a moving surface left, its history could still be kept. A keyed box with ridged
    pixels walking across the textured wall at 2.5 display pixels a frame while the camera follows it, as a third-person
    camera follows an avatar, is still on screen while the wall pans under it, so each wall pixel it uncovers reprojects
    by its own motion onto the box's stored depths and state. The lock exception kept the box's history there, since its
    ridged pixels hold locks near whole, and a footprint half on the box kept a history half its colour, which the clip
    leaves over a textured background: 38 and 22 of 510 wall pixels at Native and Quality passed a freshly revealed
    wall's difference by more than 0.05. So where any texel carrying weight holds the moved mark, neither exception
    applies and any weight on a stored depth nearer than expected drops the history. The follow camera leaves 0 and 0
    there, and the box crossing the textured wall at 4 display pixels a frame 6 and 12 of 840 at Performance and
    UltraPerformance against 17 and 63. The rule first held only where the surface travelled past
    `DilationReachInternalPixels`, and at 1.5 display pixels a frame on Quality, 1 internal pixel, the follow camera
    kept 101 of 300 against 5 with it on every moving surface, where it holds now. Three printed lines of the lock and
    reset facts move with it, each within its fact's bounds. The keyed line at 0.9 display pixels a frame on Quality
    (the two keyed thin feature facts) trailed 1 pixel at 17 percent of its contrast, kept at least 59 percent of its
    still contrast and 74 on average, and now trails none and keeps 63 and 75, because the pixels it leaves drop a
    history it covered in part. The dark surface crossing a held line at 0.6 internal pixels a frame on UltraPerformance
    showed no ghost, peaking at 2 percent, and now shows one revealed pixel at 50 percent on its last frame, within the
    fact's bound of one: the line's first raw sample after the restart. The reset facts' mip bias and sharpness changes
    without a reset, whose keyed body drifts 0.8 display pixels a frame, move their mean from 13.2413 to 13.2596 and
    from 17.4340 to 17.4266 steps, and every reset case stays byte-identical.

    Under the reach the follow camera still trailed. The pixels beside the box's trailing edge take its motion by
    dilation and read their own spot's history every frame, which holds the box's anti-aliased edge over a mix of the
    wall that passed under it. The wall pixel just clear of the edge reprojected onto that history by its own motion,
    passed the depth test on the band's own wall depth, and carried the box's luma out with the pan for up to 8 frames.
    At 0.5 display pixels a frame on Quality the box travels 0.33 internal pixels, under `MovingSurfaceInternalPixels`,
    so it counted as still and stored no mark at all. Once every moving surface takes the whole-footprint rule the
    stored state's fast level says nothing the moved level does not, so minus three minus the lock now marks a band
    pixel: one that took by dilation the motion of a nearer surface that moved in the world, its own sample more than
    `WorldMotionInternalPixels` (0.05 internal pixels) plus the rounding fraction from a static point's, that is wide by
    step 5's narrow test, and that moves less on screen than the farther surface its centre texel shows. The band still
    counts as moved. A depth-tested pixel off any moving edge that carries the mark drops the history, unless every
    stored depth lies farther than expected, a nearer surface reading the band where its edge was, or its own 3x3 holds
    a surface farther than its centre, the nearer surface's own edge pixel on a frame the jitter centres it on the box.
    The follow camera, excess pixels of those checked, before and now:

    | Walk, display px a frame (checked) | Native | Quality | Performance | UltraPerformance |
    | --- | --- | --- | --- | --- |
    | 0.5 (90) | 20, 0 | 32, 0 | 35, 3 | 35, 36 |
    | 1 (210) | 45, 0 | 87, 0 | 109, 2 | 86, 35 |
    | 1.5 (300) | 0, 0 | 101, 0 | 168, 0 | 111, 15 |
    | 2 (420) | 0, 0 | 2, 2 | 213, 0 | 182, 35 |
    | 2.5 (510) | 0, 0 | 0, 0 | 172, 0 | 171, 23 |
    | 3 (630) | 0, 0 | 0, 0 | 0, 0 | 180, 23 |

    Native and Quality meet acceptance 3 at every walk, and `TemporalFollowCameraGpuTests` holds them there, Performance
    from 1.5 display pixels a frame. Below that Performance holds its measured 3 and 2 at 5 and 4, and UltraPerformance
    its measured 36, 35, 15, 35, 23 and 23 at 45, 44, 19, 44, 29 and 29, about a quarter over each. There the
    reconstruction spreads the box's texel over 6 display pixels of wall, which the bare-wall floor never shows, and a
    restarted pixel gathers display-scale sample weight slowly. At 0.5 display pixels a frame on UltraPerformance the
    band does no better than before, 36 against 35, and its bound cannot tell the rule apart. The box's own edges keep
    their anti-aliasing: the same tilted keyed box followed across the textured wall at 0.25, 0.5 and 1 internal pixel a
    frame (`TemporalFastEdgeGpuTests`, report only) averages an edge error against the 4x reference of 0.0173, 0.0175
    and 0.0115 at Native and 0.0216, 0.0222 and 0.0234 at Quality, against 0.0197, 0.0218, 0.0169, 0.0271, 0.0305 and
    0.0359 before, and its fast flips stay under the reference's but at 0.25 internal pixels a frame on Native, 0.0003
    against 0. Crossing the flat wall under a still camera at the same speeds it does not change. Set aside on the
    numbers: reprojecting such a pixel by its own motion under the reach, as past it, shimmers the followed box's edge,
    fast flips 0.0134 and 0.0275 at 0.25 internal pixels a frame against the reference's 0 and 0.0069. The band where
    the nearer surface moves more on screen, a keyed object crossing a still view, restarts a revealed sub-texel line on
    a frame the jitter hits it, so its raw sample shows brighter than its converged value in up to 3 display pixels
    against the lock fact's bound of one. Without the world-motion test the isometric zoom's lines move, since under a
    zoom the farther surface can move more beside a still object, and a still box under a perspective camera stepping
    sideways keeps dilation there too. A narrow nearer surface, a swaying blade or a thin line, is left to the lock. A
    keyed object crossing a still view under the reach keeps the trail it had.

    Beside a keyed passer one or two texels wide sliding past a held still line half a texel away, the moving share's
    colour and its missing confidence dropped the line's history on the frames the jitter missed it, so the line blinked
    out: 0.02 of its energy at Native and 0.00 at Quality on its worst frame. The colour now takes only the share the
    pixel's lock does not hold, and a pixel whose hold is whole keeps its own history and confidence: the line keeps
    0.91 and 0.94. Output with no anti-aliasing passes every other check of the fast keyed line: its fast flips of 0.012
    and 0.015 fall under the bound and its coverage energy is 1.00. Its temporal error, 0.00185 and 0.00154 against the
    resolve's 0.00155 and 0.00202, and its edge error, 0.00054 and 0.00052 against 0.00043 and 0.00073, do not tell the
    two apart: on a thin line moving 2 internal pixels a frame the resolve's error is no anti-aliasing's, where MSAA 4x
    holds 0.00072 and 0.00060. That is a finding for the motion-clarity check against MSAA 4x. The local contrast
    against the reference does tell them apart, 1.223 and 0.985 of the reference's against 1.707 and 1.551, and
    `TemporalFastEdgeGpuTests` holds the resolve's departure from 1 under half of no anti-aliasing's.
24. Withdrawn. Step 6's lock was also released after a partial reveal, where a stored depth nearer than the one the
    pixel expects, and no thin feature, lay in last frame's 3x3 around it. It compared last frame's samples with this
    frame's, so it also fired in a still scene: a line narrower than a texel beside a still surface whose edge lies
    inside the next texel column lost its lock whenever last frame's jitter put that column's sample on the surface and
    this frame's did not, and averaged 5.1 and 3.3 percent of its contrast at Native against its coverage of 37.5. A
    release that also required the stored surface to stand nowhere in the current 3x3 around it, and to be wider than
    two texels, kept that line and the teleport's corners clean, but wherever step 3 kept the history it still wiped the
    lock of the right one of three still blades narrower than a texel side by side, which averaged 2.6 percent against
    41.8. The lock therefore keeps amendment 19's behaviour. A keyed object's 10 m jump leaves its partly covered
    corners holding their luma for about four frames, since a corner whose lock a ridge refreshed on the frame before
    the jump keeps its history through amendment 23's disocclusion: two frames after the jump 7 and 9 of the 144 pixels
    of its old place differ from the wall by more than 0.05 at Native and Quality, at most 0.110 and 0.098. Before
    amendment 23 and while its lock clause took any lock whose hold was whole, 8 and 12 did.
