# Bathymetry, shoaling and the breaking-surf band

Design rationale for 17.3.0's depth-aware water surface
([#355](https://github.com/APKiwiOrg/KhaozEngine/issues/355), the amplitude half of
[#330](https://github.com/APKiwiOrg/KhaozEngine/issues/330)) and for the clipmap LOD geomorph that
shipped alongside it ([#348](https://github.com/APKiwiOrg/KhaozEngine/issues/348)). Shipped API lives
in `CHANGELOG.md`, `docs/USING-KHAOZENGINE.md` and `KhaozEngine.Render3D/README.md`; this file is the
why.

## The complaint

Ruinborne playtest of 16.12.0, verbatim: *"Sea should be a bit calmer around the shore, and there is no
natural wave crash on the beach/rocks."* Plus, separately, *"there is a SLIGHT jump (very slight) when
moving fast"*, which is the ring-boundary residual #348 already had measured.

Both halves of the first complaint are one missing input. `WaterSeaState.DepthMetres` is a single
scalar for the whole sea: it shapes the spectrum at bake time (the Kitaigorodskii attenuation and the
finite-depth dispersion) and then never varies with position. So the surface carries open-ocean
amplitude two metres off a beach, and nothing anywhere knows a wave is about to break.

## The seam: a CPU depth field, not a texture

`WaterBathymetry` is an array of depths in metres over a world-space XZ rectangle, plus a `Revision`.
The renderer owns the GPU texture and re-uploads only when the revision moves.

Three alternatives were weighed:

- **Take an `IGpuTexture` from the consumer.** Rejected. Every game already has ground height as a
  terrain field, a heightmap or a collision mesh, and none of them have it as a texture in a format the
  water pass can read. That seam pushes the pixel format, the usage flags and the upload into four
  games; the array seam pushes nothing.
- **Derive it from the scene depth buffer the water pass already samples.** Rejected: the depth buffer
  only knows what is on screen and only where the water is drawn, so it cannot answer "how deep is it
  20 metres inland of this fragment", which is exactly what a break line and an up-beach direction
  need. It is also camera-dependent, and the surface has to be stable under camera motion (#296).
- **Bake it from the terrain in the engine.** Rejected as a layering inversion: `Render3D` does not
  depend on `Terrain`, and a game whose water body is a hand-authored lake has no terrain field to bake
  from. `WaterBathymetry.FillFromGround` gives the convenience without the dependency - the consumer
  passes its own height function.

Depth rather than ground elevation, because the surface is what the shader has: the plane's still-water
Y is not in world space by the time the fragment sees it (camera-relative rendering), and a depth needs
no frame at all.

**Format.** `R32Float` is the obvious choice and is wrong: it is documented as not linearly filterable
on Metal, and this field is read bilinearly in both stages, so a point-sampled depth would put the
texel grid straight into the surf band's edge. It rides rgba16f - the format the ocean maps already
prove filterable on all three backends - with depth in `.r`. Half precision resolves under a millimetre
in the first metre of depth, and nothing here reacts to 100 m of water.

**Outside the rectangle reads as DEEP**, not as the clamped edge value. That is what makes it
affordable to bake a coastal strip at a useful resolution instead of a whole ocean at a useless one.

### Binding order, which is where this could have gone silently wrong

Two Metal-only rules stack, and both are already written up on `ShaderSources.WaterFft.cs`: every
stage's resources must be a PREFIX of the resource layout, and within a stage the cross-compiler
numbers them by FIRST REFERENCE across the emitted bodies. The depth field is read by both stages, so
the prefix rule allows it anywhere ahead of the fragment-only scene depth.

It sits ahead of the OCEAN maps for the second rule. The taper is per cascade and is applied inside the
vertex stage's cascade loop, so the vertex needs the depth before it reads the first cascade. Keeping
the ocean first would have meant reading the depth halfway through that loop under a `cascade == 0`
guard: correct, and unreadable. Bathymetry first lets both stages sample in plain top-of-main order,
which is a rule a reader can check at a glance and a test can assert on the source text.

## Shoaling: tanh(kd), deliberately backwards

Linear wave theory says a wave entering shallow water GROWS. Group velocity falls, energy flux is
conserved, and the amplitude rises by `sqrt(cg_deep / cg_local)` until it breaks. That is true, and it
is not what the complaint asked for: a game wants the surface to settle down to meet the beach, not
pile up against it.

So the factor is `tanh(k d)`, which is the same physics read the other way round - 1 wherever the wave
cannot feel the bottom, falling toward `k d` as the bottom comes up. What makes it look right rather
than merely smaller is that `k` is PER CASCADE: the long swell starts calming in metres of depth where
the chop is still at full strength, so the shallows go glassy-and-rippled instead of uniformly damped.
That is what a lee shore actually looks like, by a mechanism that is not the real one.

`k` is the cascade's ENERGY-WEIGHTED mean wave number, not its band midpoint, and that distinction
decides whether the feature works at all. Cascade 0's band runs from 0 to its Nyquist with nearly all
of its energy at the spectral peak near the bottom of that: measured, the mean is 0.13 rad/m against a
midpoint an order of magnitude higher. A midpoint would put the swell's `k` so high that the swell
never felt the bottom, and the shallows would only lose their chop. It falls out of the bake loop that
already accumulates slope variance, so it costs nothing (`OceanSpectrum.CascadeStatistics`).

The same loop yields the total height variance, hence `Hs = 4 sqrt(m0)`, which the breaker criterion
needs.

## The surf band

`H / d = gamma` with gamma near 0.78, so the break line is `d = Hs / gamma`. Measured against the
UNSHOALED `Hs`, on purpose: feeding the shoaled height back in is degenerate, because in the shallow
limit `tanh(k d) -> k d` and the ratio `H(d) / d -> Hs k`, a constant. The sea would then break
everywhere or nowhere depending on a number the consumer never set.

### Two things the first implementation got wrong, both found by probe render

**1. The crest gate had nothing to read.** The band's whole point is that foam is gated on wave PHASE,
so the white travels up the beach with each wave instead of glowing in place. The first version
normalized the surface's rise by the open-water `Hs` - and by the break line the taper has already
flattened the sea, by design, so a crest measured that way is barely off the mean surface, the gate
never opened, and the band rendered as a bare pale line. Normalizing against the amplitude the wave
has HERE (the dominant cascade's own attenuation at this depth) makes a crest a crest at any depth.
This is not a refinement; without it the feature does not exist.

**2. Two soft ramps multiplied together are a grey wash.** `surfBand` (how deep into the zone) times
`surge` (how far up the wave) never reaches white anywhere, because neither factor is ever 1 at the
same place. Split the jobs: the band GATES where surf can happen (full by a quarter of the way in) and
the surge SCALES how much there is. The rendered difference between the two is the difference between
"there is something pale near the shore" and "that is surf".

### What carries the foam, given there is nowhere to accumulate it

A real break leaves its wash behind it, so the band needs memory. The FFT foam accumulator would be the
natural home and cannot be used: it lives in each cascade's own lattice, which TILES the world at that
cascade's period, so a world-anchored surge injected into it would be smeared across every repeat.
Adding a world-space accumulation target would mean a new render target and a new history buffer for
one effect.

So the trail is procedural and reads the geometry instead: foam extends from the crest DOWN the seaward
face, found from the surface slope along the up-beach direction. It is a phase-space stand-in for a
time-domain trail, and it is honest about that - the knob is `SurfTrailWidth`, in normalized wave
height, not in seconds.

**The up-beach direction comes from the depth gradient, not from the wind heading.** Two extra taps of
the depth field, taken only inside the band. That is what makes an isolated shallow - a rock, a bar -
break AROUND itself with no authoring: the direction wraps it, so every side gets its own onshore.

At the waterline the phase gate hands over to an unconditional wash, because past that point there is
no wave left to gate on (the taper took it) and a beach is white there for a different reason.

### Amplitude collapse

Past the break line the amplitude drops further, flat across every cascade. This is NOT double counting
the taper: the taper is per wave number and barely touches the chop, while a broken wave is turbulent
whitewater at every scale. It rides `ShoalingStrength` rather than `SurfStrength`, because it is
geometry rather than foam.

## Everything defaults to OFF, and off is byte-identical

No `WaterBathymetry` set means `BathyParams.x` is 0, every shore helper in both stages returns its
identity by an EARLY RETURN rather than by arithmetic that lands on it, and the multiplications
downstream are by a literal 1.0. The FFT gate is folded into that same flag on the CPU, so the
procedural wave source never looks at any of it (it has no cascades, and the taper is per cascade).
Verified rather than argued: the Metal golden suite including both water goldens passes unchanged, and
a GPU test binds a uniformly DEEP field with every knob live and requires the render to be the one with
no field at all.

## The clipmap geomorph (#348)

16.12.0's world-locked grid left one residual: when a ring snaps, an annulus changes which level draws
it and therefore which mip it band-limits to. Measured at 12 per cent of one frame of real motion, and
notably the same at 0.10 m and 0.50 m steps, because it is bounded by the band's width rather than by
how far the camera went.

**What shipped.** Every vertex within `ClipmapGeomorphBand` of its ring's outer edge fades toward the
NEXT ring out's evaluation: its sampled displacement blends toward the coarse surface, and its
band-limit spacing blends toward the coarse spacing, reaching both exactly on the boundary.

Three things make it compose rather than collide with what was already there:

1. **It SUBSUMES the stitch.** A boundary vertex is just the `Morph = 1` case: weights `(0, ½, ½)` over
   its two coarse neighbours, which is exactly the two-tap average 16.12.0 shipped. Band 0 therefore
   reproduces the old grid vertex for vertex, and the mechanism is one rather than two sitting beside
   each other.
2. **The weight is static per grid build.** It is a function of the ring INDEX, so it costs nothing on
   a frame where no ring snapped - which is most frames, and the whole point of the world-locked grid.
   The morphed band-limit spacing is precomputed on the CPU for the same reason.
3. **Absolute-lattice decisions stay absolute.** The weight comes from ring indices and the coarse
   offset is a difference, so neither needs reducing by the render origin, and a rebase still cannot
   re-quantize anything.

The two-tap form covers the diagonal case as well, which is worth stating because four taps look
necessary. A vertex with both indices odd sits at the CENTRE of a coarse quad, and the coarse surface
there is not the average of four corners - it is the average of the two the coarse triangulation's
diagonal runs between. The index builders emit `(i0, i2, i1) / (i1, i2, i3)`, so that diagonal is
`i1`-`i2` and the offset is the anti-diagonal.

### Why 0.5 is the default, and why it is not a taste call

Measured across the band (64 texels, three cascades, worst over five start offsets, the #348 metric):

| geomorph band | 0.10 m step | 0.50 m step |
|---|---|---|
| 0 (16.12.0's hard swap) | 0.00086 m RMS | 0.00086 |
| 0.25 | 0.00066 | 0.00066 |
| **0.5 (shipped)** | **0.00047** | **0.00047** |
| 0.75 | 0.00063 | 0.00063 |
| 1.0 | 0.00077 | 0.00077 |

1.83x better at the default, and still bounded by the band rather than by the step, which was #296's
headline property and had to survive.

The curve has a MINIMUM at 0.5 rather than improving monotonically, and the reason is structural. A
ring's drawn extent starts at half its half-width, because that is where its hole ends. At band 0.5 the
ramp therefore starts exactly at the hole edge: the ring's inner cells are un-morphed and its outer
boundary is fully morphed, so the finer level's boundary (which reaches this ring's own evaluation)
meets cells that are evaluating this ring's own surface. Past 0.5 the ramp starts INSIDE the hole, so a
ring's inner cells are already partway toward the next level out while the finer ring is still morphing
toward this one - the two sides of the boundary stop agreeing, and the mismatch is a new error that
grows faster than the wider band saves.

Cost: near a boundary the surface is band-limited toward twice its own cell spacing, i.e. deliberately
oversampled and a little softer than the geometry could carry. That is the whole trade.

## Deferred

- **Refraction.** #330 stays open for the other half: crests bending to become parallel to the depth
  contours, which is what makes waves wrap a headland and converge in a bay. This release does the
  amplitude; the heading is still `OnshoreFocusPoint`'s hand-aimed stand-in. The sampling machinery
  that a refraction field would need is already built and unchanged (see
  `WATER-SAMPLING-FRAME-DESIGN-2026-07-26.md`).
- **A time-domain foam trail**, which needs a world-space accumulation target. See above for why the
  cascade accumulator cannot serve.
- **Shoaling under `WaterWaveSource.Procedural`.** The taper is per cascade and the procedural swell
  has none. A single-wavelength taper against `SwellWavelength` would be possible and was left out: it
  is a second mechanism for a legacy path, and the whole depth-driven group is documented inert there.
- **Wave refraction of the surf DIRECTION.** The up-beach direction is the depth gradient, which is
  right for where a wave breaks and says nothing about which way it was travelling when it got there.
