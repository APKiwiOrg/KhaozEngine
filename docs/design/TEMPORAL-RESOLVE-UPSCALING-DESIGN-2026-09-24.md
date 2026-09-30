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
   as the edge's, and a pixel of the followed surface itself marks its history as that surface's own (amendment 23).
2. **History fetch.** Sample the history at the reprojected position with a 5-tap Catmull-Rom filter. Bilinear history
   sampling blurs a little more every frame, and a stability-first filter keeps history for many frames.
3. **Disocclusion.** Reproject the pixel's linear depth and compare it with the previous frame's depth at the
   reprojected position. Beyond a relative tolerance the pixel was hidden last frame, and its history weight drops to
   zero. A reprojected position off screen does the same. A moving surface skips the test (amendment 17). A mostly
   covered footprint counts as hidden unless a still narrow feature covered it or the pixel carries a lock a ridge
   refreshed on the last frame, and one a moving surface showed at counts as hidden unless it is whole. A
   depth-tested pixel whose nearest surface did not move in the world, ground panning under a follow camera included,
   drops a history marked as a followed surface's edge, and one the followed surface's own pixels marked where a
   surface moving otherwise on screen shows around it now (amendment 23).
4. **Current sample reconstruction.** Gather the 3x3 internal samples around the display pixel and weight each by a
   Lanczos 2 kernel on the distance from its jittered sample position to the display pixel centre, measured in
   internal pixels. That is what turns jittered low resolution frames into a higher resolution image, and at `Native`
   it reduces to plain temporal anti-aliasing. The kernel is separable (amendment 18). Below Native a pixel whose
   history is converged and still also takes the 3x3 reconstructed with the kernel sized in display pixels
   (amendment 26).
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
    moved skips the test. Under a perspective camera so does a static point on or behind last frame's camera plane.
    Under an orthographic camera that point keeps the test and is rejected as disoccluded, because its expected depth
    is not in front. A surface moving out from behind a static occluder therefore keeps no disocclusion test and relies
    on neighbourhood clipping (step 5).
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
    Its table no longer prints the line at Quality as a known shortfall, 1 excess pixel of 630 then, since the fact's
    own line prints it. A pixel right behind a fast trailing edge that reprojects by its own motion was covered, so it
    restarts from the current sample, which the reconstruction tints with the object's colour on the jitter phases that
    put the object's edge texel within a pixel of it. The clip removes that tint over a flat or grey wall, but over a
    textured wall whose colour varies 6 and 10 of the 840 trail pixels of a keyed box crossing at 4 display pixels a
    frame keep it two frames after the box uncovered them at Native and Quality
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
    0.00088 and 0.00273. The rule costs sixteen fetches for the narrow test, behind rule 1's branch and the band's,
    which share it, and inside the narrow moving branch alone a second pass over the 3x3 that sums the nearer texels,
    with the centre texel's own depth and motion read first. Summed in the 3x3 every pixel runs, those five accumulators
    made the whole program spill on Metal and cost every pixel about half a millisecond at 2560x1440 on an M2 Max. The
    emitted resolve grew from 33367 to 38149 bytes of HLSL with it and the kept lock below, to 41801 with the fast flag
    and the held share, to 42551 with the whole-footprint rule on every moving surface and the band, to 44650 with
    the nearer texels summed in the narrow moving branch alone, and to 44995 with the 3x3 read a texel ahead. Section
    7's cost acceptance measures its hardware cost with the rest of the resolve's. The line's fast flips are 0.019 and
    0.022, about MSAA 4x's 0.021 and 0.022. Each pixel shows the line for about one frame, so
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
    kept 101 of 300 against 5 with it on every moving surface, where it holds now. Five printed lines of the lock and
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
    `WorldMotionMetres` (1 mm, below) plus the rounding fraction from a static point's, that is wide by step 5's narrow
    test, and that moves less on screen than the farther surface its centre texel shows. A pixel of that surface itself
    whose travel in the world is more than twice its motion on screen is a band pixel too (below). The band still
    counts as moved. A depth-tested pixel whose dilated nearest surface did not move in the world and that carries the
    mark drops the history, unless every stored depth lies farther than expected, a nearer surface reading the band
    where its edge was. The follow camera, excess pixels of those checked, before and now:

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

    Under the perspective follow camera a game uses the band did not hold. `FollowCamera3D`, 60 degrees of field of
    view, 1.2 metres up and 12 back, followed a keyed ridged box 0.8 by 1.8 by 0.5 metres walking over textured ground
    at 320 by 180 (`TemporalPerspectiveFollowGpuTests`). Neighbouring ground texels move apart on screen by their depth
    step, so every ground pixel read as a moving edge, and the drop, then keyed on a pixel off any moving edge, never
    fired on the ground. The walk away from the camera left a grey column along the box's whole path, and it was not
    the band's. The box's lower front face lies within the disocclusion tolerance of the ground it stands on. On the
    frame a ground point is uncovered its pixel's centre texel is still the box's, so it reads its own spot's history,
    the box's. On the next frame its nearest texel is ground, it is depth tested, and no stored depth lies nearer by
    the tolerance, so it keeps that history and pans it out at full confidence: a traced luma of 0.37 against the bare
    ground's 0.43 for 9 frames. The band never marked the box's own pixels, so nothing told the ground that the history
    was the followed surface's. A pixel of the moving surface itself, whose centre texel moves with the dilated nearest
    so that it is no moving edge, now stores the band mark where the surface travelled in the world more than twice
    its motion on screen, a followed surface rather than one crossing a still view, and the drop is keyed on the
    dilated nearest surface having moved in the world, the band's own world test, in place of a moving edge. At the
    boot pitch of 0.75 radians the walk away at half a display pixel a frame to 2.5 left 17, 50, 49, 21 and 8 trail
    pixels at Native and 18, 47, 55, 35 and 18 at Quality, and leaves none. The drop also spared a pixel whose 3x3 held
    a surface farther than its centre, for the nearer surface's own edge pixel on a frame the jitter centres it on the
    surface. At a grazing angle, 0.35 radians at 320 by 180, every ground texel lies farther than the one a row below
    it by more than the tolerance, so that spared every ground pixel, and the walk away at 1 display pixel a frame on
    Quality still left 22. The world test now covers that edge pixel, and the spare and the 3x3's farthest depth are
    gone.

    The world test was 0.05 internal pixels. A static surface's travel is the motion target's float error on positions
    relative to the render origin, up to about 90 metres from it with Y never rebased, and the depth's reconstruction:
    one distance in the world, seen through more pixels at a higher resolution or a nearer depth. Orbiting its target 3
    or 0.3 degrees a frame, strafing 0.15 metres a frame or creeping 0.4 millimetres a frame past crates and 12 metre
    towers whose side the eye passes 0.6 metres from, near the origin and 10 km out across a render-origin step, at 2560
    by 1440 and 3840 by 2160 (`TemporalStaticOrbitGpuTests`), it reached 0.075 internal pixels on a tower side 0.81
    metres away at 3840 wide. No fixed pixel threshold has three times that while the slowest follow, half a display
    pixel a frame on UltraPerformance, moves the box 0.167 internal pixels. In the world it was 0.211 millimetres at
    most at that pitch, 0.75 radians, whose ground ends 28 metres away. So the test compares the travel with
    `WorldMotionMetres` times last frame's `P00` times the internal width over twice the clip w, the internal pixels 1
    millimetre spans at the nearest texel's depth, clip w being that depth under a perspective camera and 1 under an
    orthographic one. That is 6 centimetres a second at 60 frames a second, 4.7 times that figure, and the slowest walk
    still stores 315 and 360 band marks on its measured frame at the two pitches. From the grazing pitch of 0.26
    radians, whose ground reaches the camera's 500 metre far plane, the static travel reached 0.508 millimetres, on the
    fast orbit near the origin at Native at a depth of 10.7 metres. Farther out, as a follow camera zooms to 22 metres
    in a game and 30 by `FollowCamera3D.MaxDistance`'s default, the fast orbit's eye moves faster: at 22 and 30 metres
    the static travel reached 0.374 and 0.466 millimetres from the boot pitch and 0.778 and 1.23 from the grazing one,
    and from a steep pitch of 1.36 radians 0.058 at most. At 3840 by 2160 from the grazing pitch 12 metres away it
    reached 0.368. So to 22 metres the millimetre is 1.3 times the largest static travel measured. At 30 metres from the
    grazing pitch the fast orbit, its eye moving 1.5 metres a frame, passes it on up to 0.24 percent of its texels,
    60898 of 25.3 million at Quality, and still no still run stores a band mark, which also needs the texel to travel
    more than `FollowedTravelRatio` times its motion on screen, or a farther centre moving more on screen beside a
    nearer edge. On the orthographic wall it is 0.04 internal pixels at Native and 0.013 at UltraPerformance. The error
    does not peak on the frames that cross a render-origin step, and no still run stores a band mark on any frame, so
    none drops a band history.

    Native and Quality meet acceptance 3 on 64 of the 72 walks at both pitches, away from the camera, sideways and
    towards it at half a display pixel a frame to 3, against 27 before, the worst 5 pixels against 55. The other 8 stay
    1 to 3 pixels over, on pixels the drop restarted on the measured frame, read against a floor restarted on the frame
    the box uncovered them over textured ground, and on the feet row behind a sideways walk: at the boot pitch 2 of 311
    sideways at 2 display pixels a frame and 2 of 60 towards at 1 on Quality, and at the low pitch 5 of 436 away at 2.5
    on Quality, 2 and 3 of 71 sideways at half a pixel and 3 and 3 of 160 at 1 at Native and Quality, and 2 of 92
    towards at 3 on Quality. Performance meets acceptance 3 on 21 of 36 walks, the worst 7 against 63, and
    UltraPerformance on 5, the worst 42 against 48, the reconstruction's spill. `TemporalPerspectiveFollowGpuTests`
    holds acceptance 3 where it is met and the measured excess and about a quarter more, at least 2, elsewhere. Of those
    54 bounds, 32 fail on the resolve before the band: 28 where it left more than the bound, and 4 set under what it
    left where that still leaves the measured value a margin. Three cannot, where it left one pixel more than this
    resolve, and 19 where it left no more, 15 of them on UltraPerformance. By default the fact holds six walks, the boot
    pitch walking away at half a display pixel a frame, 1 and 2, at Native and Quality, and the rest only with
    `KE_TEMPORAL_ACCEPTANCE_TABLE=1`. On UltraPerformance the whole trail mostly measures the reconstruction's spread of
    the box's texel, so the fact also holds the trail past its reach, 6 display pixels from the box, where only a kept
    history can leave its colour: 13 of the 28 walks with ground there fail on the resolve before the band, and the same
    13 with the band drop removed. The rule left 14 walks slightly worse: at the boot pitch on UltraPerformance away at
    3 from 2 to 4 and towards at 1.5, 2, 2.5 and 3 from 7, 13, 6 and 3 to 8, 18, 7 and 7, and at the low pitch away at
    2.5 and 3 from 0 and 0 to 4 and 6 on Performance and from 23 and 4 to 25 and 7 on UltraPerformance, sideways at half
    a pixel from 1 to 2 at Native and from 15 to 16 on UltraPerformance, at 1 and 2.5 on UltraPerformance from 24 and 10
    to 27 and 14, and towards at 3 from 0 to 2 on Quality.

    Still lines three eighths and a quarter of a texel wide on the walk's ground (`TemporalFollowLinesGpuTests`) keep
    at least 0.915 of their energy without the box on their worst frame under the orthographic follow, and the fact
    holds 0.85. Under the perspective walk the blades along the box's edge keep as little as 0.722 at Quality from 1 to
    2 display pixels a frame, with 3 to 9 trail pixels where the band left them from half a pixel to 2, and blades the
    box reveals trail 1 to 6 pixels of 16 to 45 at Native at 1 and 1.5. The two failures pull apart: dropping the
    band's history as a blade leaves the band shows its raw sub-texel sample, dark on a frame the jitter misses it and
    bright on one it hits, while keeping it carries the box's colour under the thin-feature lock. Deferring the drop to
    a frame the jitter hits the blade removed neither and brought back the grazing ground's trail. Keeping the blade
    and removing only the box's share needs the state to record which part of a pixel's history the box gave, which it
    does not ([#1191](https://github.com/APKiwiOrg/KhaozEngine/issues/1191)). On the orthographic wall the lines a
    quarter of a texel wide across the path keep 2 trail pixels of 18 and 16 at Quality at half a display pixel a frame
    and 1.5, where the pixel beside the band restarts every frame through the whole-footprint rule, since its
    footprint reaches the box's stored depth, so a line crossing it shows its raw sample.

    Printed lines outside the perspective facts moved only on the followed box's own edges (`TemporalFastEdgeGpuTests`):
    at 0.25 internal pixels a frame on Native its edge error from 0.01729 to 0.01728, and at 0.5 on Quality its
    temporal error from 0.01115 to 0.01114, its fast flips from 0.03913 to 0.03914 and its edge error from 0.02222 to
    0.02219. Every reset case, the reveal, and every still camera over still content are byte-identical. The emitted
    resolve fell from 44995 to 44851 bytes of HLSL, and to 42685 once the carried marks were read from one least state
    and the centre-farther test shared, which changed no output. At 2560 by 1440 on an M2 Max the twelve keyed boxes
    cost the same, about 2.00 ms at Quality, and the moving thin field about 0.05 ms more, 2.36 against 2.30 ms at
    Native and 2.26 against 2.21 at Quality, since the swaying blades the pan follows now pass the band's world test and
    run its narrow test.

    The followed surface's own pixels read the mark they stored when it stops. On the frame its travel falls under
    `WorldMotionMetres`, stopping with the camera or turning back through zero travel, its dilated nearest did not move
    in the world, the depth test passes and every stored depth equals the expected one, so the drop fired on every
    pixel of the avatar, which showed one jittered sample and settled over about 15 frames
    (`TemporalFollowStopGpuTests`). Its pixels farther inside its outline than the reconstruction's reach read up to
    3.7 and 3.0 times a kept history's error against the 4x reference at Native and Quality on the orthographic follow
    walk, and 1.2 and 1.5 times on the perspective one. Those pixels now store a mark of their own, minus five minus
    the lock, and a pixel whose nearest surface did not move drops a history that carries it only where one of the
    nine current texels around the history position moves on screen otherwise than the pixel reprojected, by more than
    `FollowedHistoryMotionFraction`, half, of that motion. The ground the avatar uncovers pans past the avatar, which
    stays put on screen, so it still drops the avatar's history. The avatar's own pixels read it in place when it
    stops, and where the same avatar shows moving with them under a camera that eases on after it, as
    `FollowCamera3D`'s target damping does, and keep it. The inner pixels read exactly the error of the same avatar
    standing still in the world on every frame after a stop or a reversal, and under the damped camera, which resamples
    the history at fractional offsets, at most 0.3 percent more than they read over it while walking (below). Keyed
    only on the pixel having moved on screen, the stop and the reversal held, but the damped
    stop kept the drop, up to 1.43 times on its turn frame, and applied to the band beside an edge too, a still pixel
    kept a band history. Reading only the texel at the history position, or the four around it, left the ground the
    box's colour where it stood, up to 8 pixels at Quality, and the orthographic follow at Performance 18 of 90
    against 3. Every walk cell holds, and the low pitch walking away at 2.5 display pixels a frame on Quality goes
    from 5 to 4. The perspective blades beside the box move both ways, the worst share at Quality 1.5 along from 0.769
    to 0.722. Every reset case, the reveal, and every still camera over still content are byte-identical. The emitted
    resolve grew from 42685 to 44875 bytes of HLSL, most of it the fourth level's store and its four decodes, and fell
    to 43424 with the lock read back as the remainder over two of minus one minus the stored value, exact for a half
    float, in place of a branch per level.

    The nine texels were first read without the motion target's background test, so a background texel read as about
    60000 UV of motion, and an avatar stopping with the clear colour beside its outline dropped the outline's history:
    every pixel showing it read up to 1.39 times its error when the avatar's own pixels stored only the moved mark. A
    background texel is left out now, as it is never the followed surface. On a hard stop the pixel's own motion is
    exactly zero, so any texel with motion, as a second avatar walking past behind, dropped the outline beside it, up to
    1.12 times on the orthographic walk at Native. A pixel that moved on screen no more than
    `FollowedStillDisplayPixels`, a tenth of a display pixel, reads none of the nine and keeps the history. A floor on
    the share instead held nothing under the passer's speed and, at one internal pixel, brought back the ground trail
    the walks bound. Leaving out every texel farther than the pixel's nearest depth also held the damped passer, but
    would keep a followed history on a still pillar the avatar walks behind. A damped stop beside a passer still drops
    the outline where the passer shows, up to 6.1 percent over a still avatar's error at Quality on the orthographic
    walk, since the easing avatar moves on screen past the floor and the passer moves otherwise. Under the damped camera
    an outline pixel whose nine texels reach ground more than twice the avatar's depth drops too, that ground moving
    less than half the avatar's motion on screen.

    The floor was measured against the supersampled reference on the perspective walk at the boot pitch, 320x180. A
    walk away from the camera slower than the floor, 0.05 and 0.08 display pixels a frame, keeps the avatar's history
    on up to 31 pixels of the ground it uncovers, which read 12.8 to 22.4/255 from the reference where no floor reads
    10.1 to 13.2. The same walk sideways and an acceleration from standstill over 32 frames read nearer the reference
    with the floor, 6.6 to 9.7/255 against 8.7 to 19.3, and an acceleration over 8 or 16 frames changes no pixel. A
    walk at 1 m/s moves the ground 0.36 display pixels a frame away from a camera 30 m back at 1920x1080 and 0.47 at
    2560x1440, so only a walk under about 0.3 m/s at that distance falls under the floor. The floor stays at 0.1.

    The stop facts hold every pixel showing the avatar to a control rendered in the same run: the same walk with the
    avatar standing still in the world while the camera and the passer keep their paths relative to it, which keeps its
    own history by construction. Values measured on one backend did not hold on another, three NVIDIA legs reading up to
    3.3 percent over Metal's under the damped camera. The same run's frames before the turn are no reference either: the
    error still drifts up as the history converges, from 0.00437 to 0.00475 over 28 frames on the orthographic walk at
    Native, in the control too. Past the most they read over the control before the turn, the inner pixels read at most
    0.3 percent of its error more under the damped camera, and every pixel showing the avatar 0.8 percent after a stop
    or a reversal and 2.5 under the damped camera, where the ground or wall passes behind the outline during the walk
    and holds still in the control. The facts allow 1, 2 and 5 percent, and the gate covers the avatar over the clear
    colour and beside a passer too, except the damped stop beside a passer. Beside a passer the lead is what the walk
    read over its control on the frame before the turn, or the same walk's lead without the passer where that is more.
    The passer shows beside the walking avatar some frames before the turn, and the most read over the control since
    then had left the outline up to 16 percent of the control's error to drop after the turn, where this leaves at
    most 5.

    The ring of pixels beside the outline, on the ground or wall, drops the band's history when the avatar stops, since
    its nearest surface no longer moves. Kept on a pixel that moved less than the floor, the ring read 1.21 to 1.40
    times its error at worst over the turn frame and the 15 after on the perspective stops and 1.28 to 1.76 on the
    orthographic ones, and every pixel showing the avatar up to 1.028, and 1.057 beside a passer, while the ring's
    flicker fell from 0.00506 to 0.00219 on the orthographic stop at Native. The band's history holds the edge's colour
    over the ground that passed under it, so the ring restarts on a stop, nearer the reference.

    A footprint whose carrying texels stored both marks reads as the followed mark, which takes a fractional position
    across the outline under the damped camera. The emitted resolve grew from 43424 to 43661 bytes of HLSL.

    Every rule above lives once, in shared shader functions, and the resolve has two entry points over them. The
    per-texel preparation (`TemporalPrepareGlsl`) derives what the resolve needs from one internal texel alone: its
    weighted YCoCg and reactive difference, the nearest surface of its 3x3, and the surface every display pixel centred
    on it reprojects by, with steps 1 and 3's tests, the reach, the band, the followed mark and the narrow test. The
    per-pixel accumulation (`TemporalAccumulateGlsl`) gathers the 3x3 and runs steps 2 and 4 to 8. The fused entry point
    (`TemporalResolveFrag`) runs once per display pixel, prepares its 3x3 inline and is followed by the depth store. The
    split entry point runs a first pass once per internal texel (`TemporalPrepareFrag`), which prepares each texel once
    into internal-size targets and writes the history's previous depth itself, so the depth store does not run, and a
    second pass once per display pixel (`TemporalAccumulateFrag`) that reads those targets. At the upscaling presets a
    texel lies in the 3x3 of several display pixels, so the split does that work once instead of once per pixel around
    it. At Native there is one texel a pixel, and the split's second pass and targets are pure cost.

    Both entry points apply every rule to the same values. The shared preparation rounds a texel's weighted Y, Co and
    Cg and its reactive difference to half float (`temporalPrepared`, `temporalHalf`), in the fused pass too, and the
    split stores every value exactly: those four in one half-float target, the expected depth and step 6's edge
    motion in single-float targets, the motion in a half-float target, the motion target's own format, beside the
    surface's eight flags as a whole number below 256, and alpha is read back from the scene colour. Moving the rules
    into functions changed no output: a hash of both history outputs after every resolved frame of every temporal fact
    on Metal, 2928 test fixtures and 63108 frames, matched the single-pass resolve they replaced, with each entry point
    forced in turn. It took the emitted resolve from 43661 to 42862 bytes of HLSL.

    The same rules over the same values make the two entry points identical only where both round alike, and
    packHalf2x16 with unpackHalf2x16 did not on Vulkan. On a Tesla T4, Windows and Linux, and on lavapipe the first
    frame differed in about 55 percent of the history colour values by one half-float step, and over the walk the colour
    differed by up to 316 steps as the history carried it on and the lock by a whole ridge, while the confidence and the
    marks matched. A probe that rewrote the programs found why on the Linux T4: the split's first pass rounding through
    the pair, the fused pass rounding through it inline and a round to nearest even gave three different values, and
    `precise` on the preparation changed nothing. So `temporalHalf` rounds in integer steps: the 13 low mantissa bits
    dropped with ties to even, and a value below the half's least normal value, 2^-14, flushed to zero of its sign by
    masks. Its result is a normal half or a zero, which the half-float target stores unchanged. Keeping the half's
    subnormals took a second rounding path that cost the fused pass up to 2.5 ms on Metal, and a comparison in place of
    the masks up to 1.8 ms. The flush moves no printed line of the temporal facts on Metal, on either entry point. It
    runs on the scene colour before exposure, which the tonemap applies after the resolve, so the values it cuts grow
    with the exposure: 2^-14 is about 0.2/255 in sRGB at an exposure of 1, and at 16 it becomes 2^-10 in linear light,
    about 3/255 in sRGB where the tonemap is near linear by black. A game that exposes a dark scene that far resolves
    those near-black values to black. On the M2 Max the rounding costs the split nothing measurable and
    the fused pass up to 0.28 ms at 3456x2234.
    `TemporalEntryIdentityGpuTests` is the check: two scenes render one perspective follow walk, still, walking and
    stopped, over still blades narrower than a texel, with a keyed pole an internal texel wide sweeping across the
    view at 3 internal texels a frame, one forced to each entry point, and after every frame their history colour and
    state are compared bit for bit. It reports the colour, the confidence, the lock under the same mark and the mark
    apart, so a rounding difference reads apart from a rule that decided otherwise, and it counts the marks and the
    moving shares the state stored, so the followed mark, the moving share and, at the upscaling presets, the band are
    shown to run. All 48 frames of Native, Quality and Performance match on Metal (Apple M2 Max), with up to 2096 band
    marks, 5073 followed marks and 974 to 19930 moving shares stored (the pole alone gives Native and Quality theirs),
    and without the pole on a Tesla T4 on Direct3D 11 and on Vulkan under Windows and Linux. Run 36543165854
    ran the integer rounding with the subnormals kept on all three, and run 36542189945 ran this rounding on Linux in
    another form that gives the same bits. CI run 36553987435 ran the committed rounding on WARP and on Metal (a
    hosted macos-26), identical. On lavapipe (Mesa llvmpipe, LLVM 20.1.2) the confidence, the locks and the marks
    match on every frame and the colour does not: its largest difference is 0.0049 at Native, 0.0154 at Quality and
    0.0439 at Performance, over 0.010, 0.052 and 0.118 percent of the values, since its compiler orders the two
    programs' arithmetic differently. So the fact holds the entry points bit for bit on hardware and on WARP, and on a
    software Vulkan device it holds the confidence, the lock and the mark identical and the colour within 0.0625 on at
    most 0.25 percent of the values. A claim of identity on a backend holds as far as that fact passes there. The
    per-frame hash over every temporal fact, 2931 fixtures and 63228 frames, was identical between the two entry points
    on Metal after the half-float rounding.

    The half-float rounding is the one output change of the two entry points. Holding the prepared colour at single
    float needed 32 bytes an internal texel, and on a Tesla T4 that bandwidth made the split slower than the fused pass
    on Direct3D 11 and on Linux Vulkan at Quality. Rounding moves 1166 of 2304 printed acceptance lines, by 0 to 2
    pixels in the pixel counts and small shifts in the errors, and fails one bound: the still box over the textured
    wall under a sideways camera leaves a trail of 14 pixels of 3968 at Quality, against 10 at single float, with Y,
    Co and Cg rounded. Holding Y at single float leaves 13, so it does not help. Holding Co and Cg at single float
    over a rounded Y leaves 9, at 28 bytes an internal texel, and is the measured way back if the playtest calls for
    it ([#1202](https://github.com/APKiwiOrg/KhaozEngine/issues/1202)). Rounding the reactive difference alone
    leaves 10. The bound is 16. The emitted resolve is 43622 bytes of HLSL, the split's first pass 13304 and
    its second 32791.

    The split's targets hold 24 bytes an internal texel: Y, Co, Cg and the reactive difference at 8 in RGBA16F, the
    motion and flags at 8 in RGBA16F, and the expected depth and the edge motion at 4 each in R32F. At 3456x2234
    Quality, 2304x1489 internal texels, that is 82.3 MB (10^6 bytes) beside the 212.7 MB the history and previous
    depth pairs hold. They live for one frame, so they have no pair. They are made at
    the history's internal size on the first frame that records the split, and a frame that records the fused entry
    point or no resolve retires them, so a device on the fused entry point holds none of them. The
    first pass writes five colour attachments, the four targets and the previous depth. Vulkan guarantees a device
    only four (`GpuCapabilities.MaxColorAttachments` carries the smaller of its `maxColorAttachments` and
    `maxFragmentOutputAttachments`, and Direct3D 11 and Metal allow 8), so a device that allows fewer than five
    records the fused entry point, even when the split is forced.

    `TemporalResolvePolicy` records the split on every graphics backend at every internal size. The fused pass runs
    only on a device that allows too few colour attachments for the split's first pass and under the diagnostic
    override below. Direct3D 11 at the display's size was the last place the fused pass was picked. Hosted run
    36553954502, on a Tesla T4 with both entry points rounding in integer steps, measured the resolve and the sharpen
    together, fused against split, in ms:

    | Scene | Preset | Display | Fused | Split |
    | --- | --- | --- | --- | --- |
    | Boxes | Native | 2560x1440 | 2.165 | 2.237 |
    | Field | Native | 2560x1440 | 2.802 | 2.663 |
    | Boxes | Native | 3456x2234 | 5.423 | 4.762 |
    | Field | Native | 3456x2234 | 7.056 | 5.818 |
    | Boxes | Quality | 2560x1440 | 2.107 | 1.695 |
    | Field | Quality | 2560x1440 | 2.640 | 1.959 |
    | Boxes | Quality | 3456x2234 | 4.925 | 3.686 |
    | Field | Quality | 3456x2234 | 6.320 | 4.373 |

    The Native rows at 3456x2234 render at 3342x2160 under the default render cap. The integer rounding slowed the
    fused pass, so the split is faster everywhere but the boxes at 2560x1440 Native, where the two are even. Vulkan on
    the same GPU was already faster or even split at every size (hosted runs 36517355602 and 36524126806): at
    2560x1440 Native by 0.70 and 0.36 under Windows and 0.03 under Linux, and at Quality by 0.46 to 2.05. Metal at
    half precision is faster split at every size (amendment 25). At the display's size the split's targets are
    display-sized, 88.5 MB at 2560x1440. With the exact 32-bit targets (run 36507108295) the split had been slower on
    Direct3D 11 at every size but the moving field at 3456x2234 Quality, which was even, and on Linux Vulkan at
    2560x1440 Quality, which is why the targets moved to 16 bits.
    `KE_TEMPORAL_RESOLVE` set to `fused` or `split` forces one entry point for the process. It is a diagnostic
    override, for tests, for measurement and to tell a fault in one entry point from the other on a player's machine,
    not a setting a game ships with.
    `TemporalResolveCostPerfGpuTests` times both entry points and each split pass alone in one run on any real GPU and
    prints the policy's pick beside them.

    Vulkan frees a disposed texture's image and memory only at the frame boundary, `Present`, or when the device is
    disposed ([#1199](https://github.com/APKiwiOrg/KhaozEngine/issues/1199)). The temporal fixture submitted and waited
    without presenting, so every target the scene replaced stayed allocated, and the cost measurement's flips between
    entry points and presets at 3456x2234 ran a 16 GB Tesla T4 out of device memory within one fact. The fixture now
    presents after each frame. `TemporalEntryFlipMemoryGpuTests` flips the entry point and the preset at 3456x2234 for
    four cycles, checks the split's targets exist only on a split frame, and on Vulkan holds the live device
    allocations at the first cycle's and prints the retire list, which the fixture's present drains every frame. It
    passes on both hosted Vulkan legs (run 36507108295).
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
25. The per-pixel 3x3 runs at half precision where the device runs half floats natively. One source compiles to two
    variants: a header after `#version` names the types of the 3x3's Lanczos kernels and their products, its colour
    and alpha range, its sample weight, its largest reactive difference and its lumas, and the conversions to and from
    them (`TemporalFullPrecisionGlsl`, `TemporalHalfPrecisionGlsl`). The full header makes every type a single float
    and every conversion nothing, so the full programs emit the same HLSL, MSL and SPIR-V as before, byte for byte.
    The half header makes them half floats through `GL_EXT_shader_explicit_arithmetic_types_float16`, which Metal
    emits as `half`, Direct3D 11 as `min16float`, and SPIR-V as the `Float16` capability. The fused resolve, the split's
    second pass, the debug views and the count probe each ship in both variants, and `TemporalResolvePrecisionPolicy`
    gives every temporal program on a device the same one, so the two entry points agree bit for bit within it.

    The prepared values the 3x3 gathers are half floats already, so its range, its lumas and its largest reactive
    difference are exact at half, and only the kernels and their products round. What runs at half was chosen by the
    acceptance facts. With the reconstruction's sums at half as well, as the measured variant of the split had them,
    the followed keyed box at 2 px a frame at Quality left an excess of 3 pixels against its bound of 2 (2 at single
    float). Single-float display kernels and sample weight left it at 3, and single-float kernels with half sums
    also 3. Single-float sums over the half kernels give 2, the single-float line exactly, so the sums stay single
    floats. The moments, the ridge that refreshes the lock, the clip, the depth tests, the history and the blend stay
    single floats as well. The emitted split second pass is 33512 bytes of MSL at half against 33161, the fused resolve
    44292 against 43950.

    Metal takes the half variant. On an Apple M2 Max, measured alternately against the full variant (full, half, half,
    full, full, half, half, full, load average 6 to 18), medians of four, resolve and sharpen at most, split full
    against half: the boxes at 2560x1440 Quality 1.99 against 1.86 ms, the moving field 2.38 against 2.11, at Native
    2.21 against 2.05 and 2.84 against 2.74, at 3456x2234 Quality 4.18 against 3.73 and 5.17 against 4.66, at Native
    4.24 against 3.77 and 5.51 against 4.92. The second pass alone falls by 0.16 to 0.58 ms. The fused pass moves by
    -0.18 to +0.27 ms, within the spread, as the split measurement found: half pays only once the per-texel work has
    left the display pass. The policy records the split at every size (amendment 23), so the fused pass runs only as
    the attachment fallback or the diagnostic override.

    Direct3D 11 and Vulkan keep the full variant. Hosted run 36567912437 forced the half variant on every backend on a
    Tesla T4. At 3456x2234 Native over the boxes the split cost more at half than at full, by 0.34 ms on Direct3D 11,
    0.66 on Vulkan under Windows and 0.97 under Linux, and the two entry points no longer agreed on any of the three
    (under Windows Vulkan the history colour differed by up to 218 half steps). That GPU runs half floats as scalars,
    so the variant adds conversions without packed arithmetic to pay for them.

    Every temporal fact passes on Metal at half with the acceptance table, each entry point forced. Their printed
    lines are identical between the two entry points, and 983 of 1873 differ from the full variant's, most by 0 to 2
    pixels in the counts and in the third decimal of the errors. The still box over the textured wall under a
    sideways camera leaves a trail of 7 pixels at Native against 4 at full precision, and 15 at Quality against 14,
    under bounds of 11 and 16. The Native bound is set by the Tesla T4, where the trail is 10 under Direct3D 11 and
    Windows Vulkan and 8 under Linux Vulkan (hosted runs 36553976825 and 36639533321), and Quality's by Metal at half.
    The 14 resets still match their from-scratch renders
    with no pixel differing, the reveal's comparison with the wall from scratch is unchanged, and the perspective walk
    cell that failed the earlier half variant (pitch 0.35, sideways at 2.5 px, Native) passes.

    Keeping the split's prepared chroma exact does not pay for its trail pixels, measured with the half 3x3 and the
    display-sized reconstruction (amendment 26) on scratch branch fix/taa-chroma-2
    ([#1202](https://github.com/APKiwiOrg/KhaozEngine/issues/1202)): Co and Cg in two R32F targets and Y and the
    reactive difference in RG16F, 28 bytes a texel against 24 and seven first-pass outputs against five, both entry
    points rounding only Y and the reactive difference. On Metal storing the chroma exactly changes nothing on its own,
    since the half 3x3 converts the colour it gathers to half floats: the still box over the textured wall stays at 7
    and 15 trail pixels at Native and Quality. With the 3x3 also gathering the colour at single float it leaves 7 and
    10. On the Tesla T4 (hosted run 36644627034 against 36644616620) it leaves 6 and 10 under Direct3D 11 and Windows
    Vulkan against 10 and 10, and 8 and 10 under Linux Vulkan against 8 and 10. The split, resolve and sharpen at most,
    costs more everywhere: on the Apple M2 Max 0.02 to 0.17 ms at 2560x1440 and 0.10 to 0.26 at 3456x2234, on the T4
    under Direct3D 11 0.21 to 0.26 and 0.35 to 0.45, and under Windows Vulkan 0.37 to 0.65 and 0.94 to 1.56. The Linux
    leg failed at vkCreateInstance before its cost facts
    ([#1200](https://github.com/APKiwiOrg/KhaozEngine/issues/1200)). The targets grow from 39.3 to 45.9 MB at 2560x1440
    Quality and from 82.3 to 96.1 MB at 3456x2234 Quality. The layout stays at 16 bits, and the fact keeps its bounds.
26. Step 4's reconstruction is sized in display pixels for a converged pixel. The Lanczos 2 sized in internal pixels
    band-limits each frame's current sample to the internal Nyquist, a period of 4 display pixels at Performance, so the
    accumulated image stayed softer than its jittered samples allow
    ([#1188](https://github.com/APKiwiOrg/KhaozEngine/issues/1188)). Below Native, a pixel that kept its history also
    reconstructs the same 3x3 with the Lanczos 2 sized in display pixels, whose kernels the sample weight already
    evaluates, and takes it in place of the internal one in proportion to a share: none where the pixel restarts, rising
    as its carried confidence passes `DisplayKernelConfidenceStart` (half) towards whole, falling to none as it moves
    `DisplayKernelMotionPixels` (2 display pixels) a frame, and cut by the reactive estimate. The display kernels' sum
    over the 3x3 is whole where a sample lands on the pixel and falls to zero where the jitter puts the pixel between
    samples, so the share also scales by that sum up to `DisplayKernelFullWeight` (a quarter). The result is held to the
    neighbourhood's range as the internal reconstruction is. The sample weight, the confidence and every other rule are
    unchanged, so a fresh or moving pixel resolves as before, and Native, where the two kernels are one, is unchanged
    bit for bit. The 3x3 gathers the display-sized sum beside the internal one (`temporalGather`), from the same
    half-float values in both entry points, so they still write the same history. Marking every pixel that took it as a
    moving share in a scratch run, the identity walk stored 116719 more at Quality and 63280 at Performance, with every
    tally still identical. Reading the 3x3 again for the pixels that take it, in a second loop behind the share, wrote
    the same output and cost more even where no pixel took it: on the Apple M2 Max the split's second pass at 3456x2234
    Native took 2.69 and 3.38 ms over the boxes and the moving field against 2.33 and 2.94 without the rule, where
    gathered it takes 2.50 to 2.54 and 3.04 to 3.05. The gathered form costs the second pass 0.10 to 0.22 ms at
    3456x2234, Native included, where no pixel takes it, and 0.02 to 0.07 at 2560x1440, against one run without the rule
    at a load of 8 to 13.

    On the Tesla T4 (hosted runs 36580778346 without the rule, 36639533321 with the second loop, 36644616620 gathered)
    the split, resolve and sharpen at most, costs 0.03 to 0.12 ms more at 2560x1440 and 0.15 to 0.32 at 3456x2234 under
    Direct3D 11 gathered, against 0.19 to 0.39 and 0.68 to 0.95 with the loop, and under Windows Vulkan 0.06 to 0.10 and
    0.15 to 0.23 against 0.08 to 0.22 and 0.47 to 0.55, at Native too. The Linux leg failed at vkCreateInstance before
    its cost facts (#1200). Both entry points still write the same history on both Windows legs, and on NVIDIA Vulkan
    the checkerboard and the chart keep what Metal keeps within 0.001.

    Where the internal size is the display's, the split's second pass is compiled without the rule
    (`TemporalAccumulateAtDisplaySizeFrag`, one switch after the precision header, so the rule is written once) and
    recorded on every frame whose ratio of display to internal size is not above 1, the test the share makes. The
    upscaling programs emit the same HLSL, MSL and SPIR-V as before, byte for byte. The fused resolve keeps the rule,
    so the identity walk at Native holds the new program to the rule's output bit for bit. Under the default render
    cap a 3456x2234 display at Native renders 3342x2160, a ratio of 1.03, so the rule runs there and its cost stays.
    On the Apple M2 Max at 2560x1440 Native (alternately without and with the program, load average 3.5 to 12.4) the
    split took 1.727 and 1.703 ms against 1.600 and 1.605 over the boxes, and 2.240 and 2.257 against 2.206 and
    2.206 over the moving field.

    Measured on Metal (half precision), 320x180, HDR off. On the mip bias checkerboard, local contrast as a share of
    native's: Quality 0.781 to 0.883, Balanced 0.741 to 0.806, Performance 0.747 to 0.768, and UltraPerformance
    unchanged at 0.827. With the share forced whole on every pixel with history Performance reached only 0.800 and
    UltraPerformance 0.836, so on this receding ground the reconstruction is not what holds them back. The chart's
    contrast kept per group, vertical and horizontal bars, at Quality: 3 px 0.782 and 0.769 to 0.867 and 0.856, 2 px
    0.863 and 0.889 to 0.905 and 0.927, 1.5 px 0.698 and 0.702 to 0.764 and 0.779, 1.2 px 0.599 and 0.624 to 0.718 and
    0.719, 1 px 0.359 and 0.489 to 0.463 and 0.604, and its error 0.01974 to 0.01697 against the reference, 0.577 to
    0.496 of a bilinear upscale's. At Performance: 3 px 0.817 and 0.924 to 0.832 and 0.942, 2 px 0.841 and 0.844 to
    0.877 and 0.876, 1.5 px 0.650 and 0.624 to 0.679 and 0.650. Its 1.2 and 1 px groups lie past Performance's internal
    Nyquist, read as aliasing and move by 0.006 at most. The test floors rose with it: Performance on the checkerboard
    from 0.7 to 0.72, and the chart's per-group floors at about 0.7 of the new values. The chart's Metal grid moved by
    0.016 in its worst cell and was rebaked. The checkerboard's grid stayed within its tolerance.

    Every temporal fact passes forced split with the acceptance table, and the identity fact forced each way. 622 of
    2330 printed lines moved, none at Native. The keyed box crossing the textured wall leaves an excess of 2 pixels over
    its trail floor at Quality against 1, and 1 at Balanced against 0, both within acceptance 3's 4. The still thin
    fence keeps a sharpness of 0.368 against 0.322 at Quality, and the slow pan over it flickers no more (0.082 of MSAA
    4x's added error against 0.085). From a confidence of a quarter it kept 0.785 at Performance and 0.896 at Quality,
    and a ridged keyed box crossing the textured wall at Quality left an excess of 6 against 4, under a bound of 7, so
    the share waits for half. With neither the confidence nor the motion rule a followed keyed box at 2 display pixels a
    frame at Quality left an excess of 3 against its bound of 2, and a followed avatar on perspective ground 2 against 1
    at Quality and 14 against 1 at UltraPerformance.
