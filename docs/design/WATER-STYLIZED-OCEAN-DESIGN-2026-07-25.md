# Stylized ocean: swell, sky reflection, GGX glint, depth grading, foam

Design rationale for the 14.24.0 water rework. Issue: #295. Predecessor: #278 (de-tiling, 14.22.0).

This is the **why**. What shipped and how to use it live in `CHANGELOG.md` 14.24.0,
`docs/USING-KHAOZENGINE.md` (Render3D -> Water) and `KhaozEngine.Render3D/README.md`.

## The problem

14.22.0 fixed the reported checkerboard repeat. The user played it in Ruinborne and the verdict was that the
water "still looks very basic and tiled - a bit better". Three specific defects, read off the screenshot and
confirmed in the code:

1. **No silhouette.** `WaterMath.BuildGridPositions` emitted a flat 17x17 grid at `plane.SurfaceY` and
   `WaterVert` passed it straight through. Every "wave" was a normal perturbation, so at any grazing angle the
   surface was a mirror-flat sheet with shading painted on it. That reads as a texture, not water.
2. **Two-tone banding.** `WaterFrag` did `mix(body, HorizonColor.rgb, fresnel)` where `HorizonColor` is one
   uniform colour. The whole surface therefore ramped between exactly two colours, and the ramp showed as a hard
   band across the water.
3. **No foam anywhere.** #51 had already listed shore foam as a follow-up.

## Direction: stylized-AAA, chosen with the user

Two credible targets. **Photoreal** (FFT/Tessendorf spectrum, screen-space reflections, refraction, subsurface
scattering) is the technically obvious answer and the wrong one here: the consumer pulling on this is a
toon-styled island world, so a photoreal ocean would fight the art, and the engine's stated bar (recorded in
#51) is "A"-tier semi-realistic with a stylization accent, explicitly not full PBR+IBL. **Stylized-AAA** (the
Wind Waker / Sea of Thieves read: bold swell, clean gradients, graphic foam) matches both the art and the bar,
and it is cheaper.

The user chose stylized-AAA in chat on 2026-07-25. Everything below follows from that choice: where a physical
model and a graphic one disagree, the graphic one wins, but the physical model is still the skeleton wherever
it is free (dispersion, absorption, the Jacobian) because physically-shaped curves are what stop a stylized
look reading as arbitrary.

## Decisions

### Gerstner, not a height field, not FFT

A sum-of-sines height field cannot pinch a crest: it is symmetric about the still level by construction, so it
gives rounded hills and rounded valleys. Gerstner (trochoidal) waves move each point on a circle, which sharpens
crests and flattens troughs, and that asymmetry is most of what "reads as ocean" at a glance. It also hands us
the whitecap driver for free (below).

FFT/Tessendorf was rejected: it needs a compute or render-to-texture pass and a spectrum, which is out of scope
for a release that must stay one draw per plane with no new render targets, and its main advantage (a
statistically correct sea state) is worth little at a stylized bar.

### Wind parameterization, not a wave table

Two options for getting components to the shader: upload a per-component table, or regenerate it in the shader
from a compact parameter set. The table is more flexible; the parameterization is what a designer can actually
tune. We took the parameterization, for three reasons:

- The UBO stays two `vec4`s for the whole swell regardless of component count. A table for six components would
  be six more `vec4`s, and the UBO already sits inside a 256-alignment constraint (below).
- "Wind direction, spread, wavelength, steepness" is a sea. "Eight rows of direction/amplitude/frequency/phase"
  is a spreadsheet, and every consumer would end up copying the same eight rows.
- It forces the internal relationships to be right rather than hand-tuned: wavelengths ladder geometrically
  (so no component is a harmonic of another, which is the same non-tiling argument 14.22.0 made for the ripple
  layers), amplitudes are proportional to wavelength (so every component carries the same steepness and the
  short ones cannot dominate), and speed comes from the deep-water dispersion relation `omega = sqrt(g*k)` (so
  long rollers overtake short chop, for free, instead of the whole field sliding at one rate).

The escape hatch, if a consumer ever genuinely needs a hand-authored table, is to add one alongside rather than
replace this: the generator and the evaluator are already separate functions (`BuildComponents` / `Evaluate`).

### Steepness normalization is load-bearing twice

`Q_i = steepness / (k_i * A_i * n)` makes `sum(Q_i * k_i * A_i)` equal the knob exactly. That buys:

- **A meaningful ceiling.** The no-self-intersection condition for a Gerstner sum is exactly that sum being
  <= 1, so `SwellSteepness = 1` is a hard, documented ceiling instead of a number someone guessed. A headless
  test samples the field at steepness 1 and asserts the Jacobian determinant never goes negative.
- **A steepness-independent foam knob.** The fold factor scales linearly with steepness, so dividing by it
  normalizes the driver to roughly 0..1 and `FoamCrestCoverage` means the same fraction of the sea whatever the
  shape knob is doing. Without this, lowering steepness to calm the sea would silently also remove the foam,
  and the fix would look like a foam bug.

### The grid: fixed budget, camera-focused, not adaptive

The plane is sized by the consumer, up to 600-unit half-extents (Ruinborne's ocean). Options weighed:

| Option | Why not / why |
| --- | --- |
| Keep 17x17 | 75 units between vertices on a 1200-unit plane. A 42-unit wave is invisible. Non-starter. |
| Uniform, much denser | To reach ~4 units near the camera you need ~300x300 = 90k vertices, uploaded per frame, most of them spent on water hundreds of units away where a crest is two pixels tall. |
| Grid size derived from plane extent | Quadratic blowup on a large plane, and the vertex/index buffers stop being allocate-once. |
| Projected grid (screen-space) | The standard solution, and genuinely better, but it needs a robust projector/frustum-intersection step and degenerates badly at horizon-grazing angles. Too much machinery for this release. |
| **Fixed 97x97 warped toward the camera** | Chosen. One draw, allocate-once buffers, a 113 KB upload, and resolution where the silhouette actually reads: about half a unit at the focus, 10 at 90 out, 22 at the far edge. |

The cost is a camera-relative mesh: vertices slide through the wave field as the camera moves. This is
continuous (nothing pops) and the near field is dense enough that resampling a smooth long wave there is
invisible, so it is an acceptable trade, but it is a real property and it is documented on the knob rather than
hidden. `GridFocusBias = 1` returns the uniform grid bit-for-bit (an early return, not a `pow(x, 1)` round
trip), which is what makes it a genuine A/B switch.

The warp is a per-side power on the parametric coordinate, applied to each axis independently and evaluated
once per axis rather than once per vertex. It has a wide dynamic range, which on a small pond crams the near
cells down to millimetres - wasteful, not wrong, and documented.

### Reflection: reuse the sky's evaluation, in per-direction form

The existing sky is drawn in SCREEN space (a vertical NDC gradient plus a disc at the sun's projected screen
position), because it has to read under the orthographic iso camera where a world-ray sky degenerates to a flat
colour. A water fragment needs the sky along its REFLECTED view ray, which usually has no screen position at
all, so the screen-space evaluation cannot be called directly.

Rather than invent a second sky, `SkyMath.ShadeDirection` evaluates the same gradient and the same
disc-plus-halo shape per world direction, off the same `SkySettings` numbers. Two reinterpretations were
unavoidable and are documented at the call site: the gradient runs off the direction's elevation rather than
screen height, and the sun distance is the chord between unit directions rather than a screen NDC distance, so
`SunRadius`/`HaloFalloff` read as angular sizes there. Keeping one set of knobs matters more than the exactness
of that mapping: the point is that the sky the water reflects and the sky the camera sees are the same sky, and
a second set of colours on `WaterSettings` would drift from the first within a release.

**Why the reflected sun is scaled down by default.** Physically, a mirror reflecting the sun disc off a wavy
normal IS the sun path, and the GGX lobe is the statistical version of the same thing. Carrying both at full
strength double-counts the sun and blows out the path. The split we took: the reflection carries a fraction of
the disc + halo (a broad, soft bloom that fills the space between glitter points, which reads very well) and the
GGX lobe owns the sharp glitter. `SkyReflectionSunStrength = 1` gives the full mirror disc for anyone who wants
it.

### Glint: GGX peak-normalized, widened by footprint

Blinn-Phong's falloff is too fast: the sun path comes out as a hard-edged blob. GGX's long tail is what makes it
read as thousands of facets fading into haze.

Raw GGX `D` peaks at `1/(pi*alpha^2)`, which at these roughnesses is in the thousands. In an HDR scene with
bloom that is a screen-sized flare. We peak-normalize the lobe to 1 instead, which (a) makes `GlintStrength`
mean the same brightness as the legacy Blinn-Phong path, so the two are directly A/B-able on one knob, and (b)
is the right call for a stylized glint that is controlled by an artist knob rather than by energy conservation.

The roughness widening is #292's ask, and it implements both of that issue's suggestions rather than one:
distance widening (the blunt version) OR pixel-footprint widening via `fwidth` against the ripple wavelength
(the correct version), whichever is worse. The footprint measure is what makes it right under a wide FOV, under
the ortho iso camera where footprint barely changes with distance, and at a resolution other than the one a
distance default was tuned at. `fwidth` is taken at the top of `main`, in uniform control flow, because a
derivative inside a per-fragment branch is undefined on some backends.

`DetailFadeDistance`'s normal-amplitude fade is kept, but it is no longer the anti-aliasing mechanism, it is a
look knob. A consumer can now set `DistantDetailScale = 1` and let roughness widening handle aliasing on its
own, which is the technically better configuration and was not available before.

### Depth grading: per-channel exponential, not a two-stop lerp

A scalar lerp between two colours puts every midtone exactly on the straight line between them, and the middle
of that line between a turquoise and a deep blue is a muddy grey-teal. Per-channel Beer-Lambert transmittance
leaves that line, because red dies several times faster than blue, so the ramp bends through green-teal. That
bend is the entire visual difference and it is what "physically-shaped curve, stylized-clean result" means
here. A headless test asserts the curve really does leave the straight line, so a future simplification back to
a scalar cannot pass silently.

Absorption reuses the existing depth reconstruction; no new sampling. An all-zero coefficient falls back to the
14.22.0 blend, following this file's established zero-means-off idiom rather than introducing an enum (every
other switch in `WaterSettings` is already zero-means-off, and a mix of enums and zero-switches would be worse
than either alone).

### Foam: Jacobian for crests, depth for shore, one mask over both

The whitecap driver is the determinant of the displacement map's horizontal Jacobian. A height threshold was the
obvious alternative and is worse: it foams the tops of gentle swells that would never break, and it misses the
sharp crest of a small steep wave that would. The Jacobian is exactly "how compressed is this patch of water",
which is the physical precondition for breaking, and the Gerstner formulation hands it to us analytically.

It is computed in the VERTEX stage and interpolated, but thresholded in the FRAGMENT stage. That ordering is
deliberate: thresholding per vertex would give a foam edge quantized to the mesh, while interpolating the raw
fold and thresholding per pixel gives a crisp edge at no extra cost.

Both sources are multiplied by a scrolling three-layer pattern on non-axis-aligned headings at mutually
irrational frequencies - the same construction, and the same reason, as 14.22.0's ripple layers: a product of
axis-aligned sines paints a visible grid of foam blobs across the whole ocean. It is thresholded tightly rather
than gently, because a gentle threshold gives soft photoreal scum and the brief asks for graphic shapes.

The shoreline band needed no wave coupling: it keys off `surfaceY - groundY`, and `surfaceY` is the DISPLACED
height, so a passing crest deepens the water beneath it and the foam line runs up and down the beach for free.
The same falls out for the waterline alpha feather. This was noticed rather than designed, and it is the nicest
thing in the release.

### Foam coverage was tuned against a number, not an eyeball

The default `FoamCrestCoverage` was picked by sampling the default field over a 260x260 grid and measuring the
fraction of the surface that foams: 5% strong, 8% any. A real ocean at moderate wind is nearer 1-3%; the
stylized read wants a little more than the truth. A headless test pins that band, because the golden's
anti-degeneracy guard (a brightness-spread check) would sail straight past a completely foam-free ocean.

## Constraints that shaped the implementation

- **The UBO must stay 256-aligned on its BOUND RANGE.** 14.22.0 grew the payload to 272 bytes, bound 272, and
  D3D11 silently rejected the constant-buffer binding (Veldrid computes `numConstants = max(size,256)/16` with
  no rounding, so 272 yields a non-multiple-of-16 count), leaving the cbuffer unbound and rendering NO water on
  that backend while Metal and Vulkan were perfect. The payload is now 432 and the bound range is still 512.
  `UboLayoutTests` pins it.
- **Every new shader function needs a `WaterMath`/`GerstnerWaves` mirror and headless tests.** This is the
  repo's standing contract for mirrored shader math, and it is what let the whole swell be validated before a
  single GPU frame was rendered.
- **One draw per plane, no new render targets, no compute.** This is what rules out refraction (which needs a
  colour copy the pass can read, and the pass deliberately runs before the colour resolve), SSR, and caustics.
- **Every feature reaches zero independently**, and the 14.22.0 look is reachable through documented knob
  values. 14.22.0's ripple-field rewrite was granted an exception to that rule because the old field WAS the
  defect; this round needed no exception.

## Deferred, deliberately

Recorded here and in #295 so they are not assumed to be oversights: refraction, screen-space reflections and
environment probes (#54 records these as not planned at the current bar), seabed caustics, and the
submerged/underwater view (its own program, touching the camera and post chain rather than this shader).
Per-body water volumes remain #275; this shader takes no position on where water exists.

## 14.26.0 revision: the ripple field was the wrong shape

Shipped as 14.26.0, closing #299. Recorded here rather than in a new doc because it corrects a decision made
above rather than starting a program.

**What this doc got wrong.** The "Ripple field, and why it is shaped this way" reasoning inherited from 14.22.0
is sound about TILING and wrong about COHERENCE. Making three cosines non-axis-aligned and mutually irrational
removes the finite repeat, which is what 14.22.0 was asked to do, and the field genuinely has no tile. But three
cosines of any orientation have a slope that is constant along families of parallel lines, so they draw parallel
ribbons. A non-tiling ruled pattern is still a ruled pattern. The domain warp, which this doc leaned on as the
tiling-breakup mechanism, bends those ribbons over a long distance without breaking them, so it masks the defect
up close (where the bend is visible within a screen) and does nothing at range (where it is not). That is
precisely the "upclose its masked" half of the user's report.

**The second miss is narrower and more mechanical.** 14.24.0 introduced footprint-aware band-limiting and applied
it only to the specular lobe. That fixed sparkle and left moire, because moire is what an unresolvable NORMAL
oscillation looks like, and the normal field was never band-limited at all. The design doc treats
`glintRoughnessAt` as "the correct band-limiting response and already wired", which is half true: it is the
correct response for the lobe and does not touch the field.

**What replaced it.** A generated spectrum: headings on a golden-angle walk so no two components are parallel at
any count, wave numbers over about five octaves, amplitudes renormalized in closed form to the old field's total
slope variance so `NormalStrength` survives the change. Then per-component footprint band-limiting of the normal,
with the removed slope variance transferred into the GGX lobe rather than discarded, and the same footprint
measure attenuating the swell's shading contrast (not its geometry) so crests stop reading as parallel rules on
the horizon.

**The general lesson worth keeping.** Both misses have the same shape: a property was verified in the regime the
tests covered and assumed in the regime they did not. Every water golden frames a 9-unit lake from a corner under
an ORTHOGRAPHIC camera, where every ripple is resolved and no footprint problem can exist. The artifact lives
entirely in the perspective far field, so no golden could have caught it, and the anti-degeneracy guard would not
have either. The fix for that is the new `WaterDistanceBandingProbe`, which renders the open ocean at the
viewpoints the report came from and dumps PNGs for a human. A number would not have worked: the first metric
tried here (row-to-row luminance step) moved the WRONG WAY across a fix that is unambiguous by eye, because it
measures tonal range and the fix widened it.

**Still not reachable, deliberately.** The exact three-cosine field, on the same grounds the 14.22.0 checkerboard
was not kept reachable: the coherence IS the defect. What is reachable is `FootprintSamples = 0` (14.24.0's
unbounded normal oscillation), `VarianceToRoughness = 0` (its lobe behaviour) and `RippleComponents = 3` (a
sparse spectrum, though with golden-angle headings rather than the old fixed ones).
