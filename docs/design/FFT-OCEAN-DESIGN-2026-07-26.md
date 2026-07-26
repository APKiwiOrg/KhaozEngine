# The Tessendorf FFT ocean

Design rationale for the 16.1.0 water release. Issue: [#310](https://github.com/APKiwiOrg/KhaozEngine/issues/310).
Program 2 of two; program 1 is the compute seam, `docs/design/GPU-COMPUTE-DESIGN-2026-07-26.md`
([#309](https://github.com/APKiwiOrg/KhaozEngine/issues/309), shipped 15.2.0).

This is the **why**. What shipped and how to use it live in `CHANGELOG.md` 16.1.0,
`docs/USING-KHAOZENGINE.md` ("FFT ocean") and `KhaozEngine.Render3D/README.md`. Attribution is in `NOTICE.md`.

## The problem, and why FFT after it was declined once

`docs/design/WATER-STYLIZED-OCEAN-DESIGN-2026-07-25.md` declined FFT at 14.24.0, and its reasoning still holds on
its own terms: a photoreal ocean fights a toon-styled island, and Gerstner is what pinches a crest.

What changed is the failure mode, three times, in the same direction. 14.22.0 fixed a checkerboard repeat and the
water still read as tiled. 14.24.0 replaced the whole shading model and the report back was lines over the ocean,
textured and tiled. 14.26.0 / 14.28.0 replaced three fixed ripple cosines with a golden-angle ten-component slope
spectrum plus footprint band-limiting, and the screenshot after that still showed a checkered read.

Each round removed one coherent structure and the eye found the next. That is the signature of a finite sum of
directional components, not of any particular component count: a small sum of sinusoids always keeps SOME residual
regularity, and every fix was another attempt to hide it. Ten components is not meaningfully different from three
in that respect. A directional spectrum evaluated over a full grid has no such residual, because there is no small
set of headings left to read as a pattern - at the shipping default that is 16384 components per cascade, three
cascades, drawn from a real spectrum.

**The art direction is not being revisited.** This changes where the height and slope come from. The shading model,
the graphic foam, the depth grading and the analytic sky reflection from 14.24.0 all stay, and the procedural
surface stays reachable as a mode.

## Decisions

### Two kernels, not fourteen: the stall budget drove the whole kernel design

The compute seam has no cross-dispatch barrier and cannot get one from above
([#311](https://github.com/APKiwiOrg/KhaozEngine/issues/311)): the only ordering available for a dispatch that
reads what an earlier dispatch wrote is `End` + `Submit` + `WaitForIdle`. The obvious FFT - one dispatch per
radix-2 stage, ping-ponging two buffers, which is what 15.2.0's proof test does - therefore costs 14 full GPU
drains per 2D transform at 128 points. Times four complex fields, times three cascades, that is not a frame
budget, it is a slideshow.

So the transform is restructured until only ONE dependency is left:

- **Each axis is one dispatch** that loads a whole transform line into workgroup shared memory and runs every
  butterfly stage with `barrier()` between them. Shared memory is exactly the intra-workgroup barrier the seam
  cannot give across dispatches, and it is free.
- **The work either side is fused in.** The row pass carries the spectrum's time evolution (there is nothing to
  gain from writing `h~(k,t)` out and reading it back); the column pass carries the displacement / derivative /
  foam map assembly.
- **All cascades and all fields share both dispatches** via the workgroup grid's second dimension, so they are
  independent work in one command list rather than a chain.

The result is one row dispatch, one drain, one column dispatch - **exactly one stall per frame, independent of
cascade count and resolution**. The column pass is recorded into the SCENE's command list, immediately before the
draw that samples its output, which is the seam's other guaranteed pattern (compute writes a `Storage | Sampled`
texture, a graphics pass in the same list samples it). Measured on Metal at the shipping defaults (3 cascades,
128): about **0.3 ms** of wall-clock stall per frame, against a 2 ms budget, so the defaults stand. At 256 it stays
under 1 ms. A SINGLE cascade at 128 measures the same 0.3 ms, which says the cost is the submit-and-drain round
trip rather than the transform work - the stall IS the cost, exactly as #311 predicted, and it is why collapsing
the stall count mattered far more than making the kernels fast.

The alternative considered was load-balancing cascades across frames, as the reference does. It is unnecessary
here: the fused shape already reaches one stall per frame at any cascade count, and staggering cascades would
desynchronize their time evolution for nothing.

### In-place decimation-in-time, not Stockham

15.2.0's proof kernel is Stockham, chosen there because it needs no bit-reversal pass and ping-pongs cleanly
between two buffers. In SHARED memory that second buffer is the problem: four fields at 256 points would want 16
KB, which is exactly the guaranteed minimum with nothing left over. In-place Cooley-Tukey with a bit-reversed LOAD
needs one buffer (8 KB at 256, 4 KB at 128), and the permutation costs nothing because the load is a gather
either way. Within a stage each thread reads the two elements it writes, so a barrier between the reads and the
writes makes in-place safe with no second buffer.

That is a real algorithm change from the validated seed, so it gets its own proof: `OceanFftGpuTests` checks the
produced maps against a NAIVE direct 2D DFT of the same baked spectrum. Deliberately naive - a second copy of the
same butterfly would agree with a restructure that was wrong in the same way.

### Four complex fields carrying eight real ones

Every field the surface needs is Hermitian, so an inverse transform of `A + iB` returns `a + ib` with both halves
real and usable. Packed that way, height, both horizontal displacements, both slopes and all three displacement
derivatives come out of four transforms instead of eight:

```
F1 = h~ + i.Dx~          -> height,        x displacement
F2 = Dz~ + i.dh/dx~      -> z displacement, x slope
F3 = dh/dz~ + i.dDx/dx~  -> z slope,        x-displacement gradient
F4 = dDz/dz~ + i.dDx/dz~ -> the two remaining Jacobian terms
```

The alternative is finite-differencing the displacement map for normals and the Jacobian, which is cheaper and is
what a height-field-only implementation must do. It was rejected because the derivatives are the expensive thing
to get right: a central difference on a 128-texel map under-represents exactly the fine slope the far-field
band-limit then has to reason about, and the Jacobian is a product of three of them. Four transforms instead of
two is a rounding error against one stall.

### The spectrum is baked on the CPU, in closed form

`h0(k)` changes only when the sea state does, so it is built on the CPU (`Internal/OceanSpectrum.cs`) and uploaded
once. Two things follow that are worth more than the microseconds a compute bake would save.

It is **headless-testable**. TMA, the Kitaigorodskii depth factor, the finite-depth dispersion and its derivative,
the Longuet-Higgins normalization and the cascade band split all have closed forms with checkable properties, and
`OceanSpectrumTests` pins them: the spreading integrates to 1 at every setting, the dispersion derivative matches
a numerical one, stronger wind raises the variance, shallow water cuts it, the bands do not overlap. Those are the
parts most likely to be silently wrong, and none of them needs a GPU.

And there is **one copy of the maths**. The GPU never re-derives the spectrum; it reads the baked field. The only
thing both sides compute is the dispersion relation, which is four lines and is pinned by the GPU test against the
CPU reference.

The randomness is a position HASH rather than a stream, so the value at a texel depends only on the seed, the
cascade and the texel coordinates. That is what makes `conj(h0(-k))` at one texel and `h0` at the mirrored texel
the same draw, with no second pass and no ordering assumption, and it is what makes the surface bitwise
reproducible for a seed.

### TMA rather than Phillips, and why the knobs are oceanographic

Phillips needs an amplitude, which is a magic number. TMA is JONSWAP shaped by the Kitaigorodskii depth
attenuation, so a sea state is described the way a forecast describes one - wind speed, fetch, depth - and every
wave in the surface follows from it. That is the same principle as 14.24.0's "wind parameterization, not a wave
table", carried to its end.

It also has a consequence worth stating because it surprised this program's own golden: **wave height is not a
knob**. A ten-metre pond can only physically carry centimetre waves, so the FFT ocean rendered at doll-house scale
is a nearly flat sheet, correctly. The procedural mode remains the right answer for a pond, and the cascade tile
sizes are the knob for changing what scale of sea is being simulated.

### Cascade bands PARTITION wave-number space

Each cascade owns a disjoint band: cascade `i` covers everything from the previous, larger tile's Nyquist wave
number up to its own, and the finest cascade's upper bound is open. So no wave number is represented twice and
summing the cascades additively is correct by construction - there is no energy to weight away and no double
counting to compensate for. The tile ladder is two knobs (largest tile, ratio) rather than a list, and the ratio
defaults to 4.2 rather than 4 so no two cascades share a repeat period.

### One map array, bound first: three Metal-only landmines

This is the part that cost the most, and all three failures had the same shape - correct on Vulkan and
Direct3D11, silently wrong on Metal, with nothing wrong in the GLSL.

1. **Metal resource slots are numbered in first-reference order.** Metal has no binding decorations, so the
   cross-compiler assigns `[[buffer(n)]]` / `[[texture(n)]]` indices itself, in SPIR-V id order - which follows
   where each resource is first referenced across the emitted function bodies, and a helper function is emitted
   before `main`. The row kernel read `H0` (binding 1) inside a helper before anything read the uniform block
   (binding 0), so the two swapped: the kernel read its cascade tile size out of the spectrum buffer, got 0,
   divided by it, and produced a NaN surface. **This one is now guarded.** `ShaderValidation.ValidateCompute`
   compares the kinds of the Metal entry point's buffer arguments against the reflected layout's, in order, and
   rejects a mismatch in the GPU-free lane on every push. `OceanFftShaderValidationTests` keeps the real broken
   source as its negative case.

2. **Veldrid numbers resources with one counter PER KIND over the whole layout, while the cross-compiler numbers
   each STAGE densely.** They agree only when every stage's resources are a PREFIX of the layout. A vertex-only
   displacement texture sitting after the fragment-only scene depth therefore cannot line up at any binding
   number: the vertex sees dense index 0 and Veldrid binds at global index 1, so the vertex samples an unbound
   slot and reads zero. This is why the ocean is ONE array texture (displacement layers then derivative layers)
   declared identically in both stages and bound FIRST, ahead of the scene depth. It is not tidiness; it is the
   only arrangement that works. The same rule is why the fragment samples the ocean before the depth.

3. **A one-layer array texture is not an array texture.** With a single cascade the map was allocated with one
   layer, which the backend creates as a plain 2D texture, and binding that to a `texture2DArray` slot writes
   nothing and reads zero. A single-cascade ocean produced a perfectly correct foam BUFFER and an entirely blank
   map. The allocation now floors at two layers.

The common lesson, and the reason all three are written down at the code rather than only here: on Metal a
resource binding mistake does not fail, it reads zero, and a wave simulation that reads zero looks like a calm
sea.

### Foam is a buffer, and it crosses the frame boundary

The foam accumulator is a plain storage buffer, one float per texel, read and rewritten by the single invocation
that owns that texel - so there is no cross-invocation hazard and it needs no ordering of its own. It is the only
state that survives a frame, and it survives across the frame's own submit boundary, which is what makes it safe
under #311 by construction rather than by care.

A ping-ponged texture pair was the alternative and is worse twice over: typed UAV loads are restricted to 32-bit
formats on Direct3D11, and a within-frame sampled/storage usage flip on the same pair is exactly the case the
compute design doc warns has an upstream barrier-scope bug.

Foam ACCUMULATES rather than being a per-frame function of the current fold: it decays exponentially and takes an
injection wherever the Jacobian says the surface is folding, so a break leaves a trail behind it. That is the
GodotOceanWaves model, and it is the half a per-fragment threshold cannot have.

### Reusing the shading stack, and the band-limit that comes with it

FFT mode replaces the displacement, normal and whitecap SOURCE and nothing else. Absorption, the analytic sky
reflection, the GGX glint, the foam break-up pattern, the shore band and the waterline feather are the same code
reading the same knobs. That is what makes the two modes an honest A/B: switching `WaveSource` changes the
surface and not the look.

The footprint band-limit from 14.28.0 applies per cascade, against twice its texel size (the shortest wave it can
carry), and the slope variance it removes is transferred to the glint lobe. That variance comes from the baked
spectrum rather than the sampled slope, because Toksvig wants the statistic and a sampled texel is one
realization. Foam is deliberately NOT band-limited: a whitecap two kilometres out is still white.

## Answers to the spec's open questions

- **Cascade count and tile sizes.** Three cascades at 250 / 60 / 14 metres, from two knobs (largest tile and a
  4.2 ratio). They do not cross-fade, because they do not overlap: the bands partition wave-number space, so
  there is no seam to hide.
- **Compute bake or CPU upload for the spectrum.** CPU, for the testability and single-copy reasons above.
- **Projected grid ([#296](https://github.com/APKiwiOrg/KhaozEngine/issues/296)) or the existing grid.** The
  existing camera-focused fixed grid, unchanged, with `GridFocusBias` still live. A projected grid is a
  separate, orthogonal improvement and would have doubled this program's surface area.
- **Per-body water volumes ([#275](https://github.com/APKiwiOrg/KhaozEngine/issues/275)).** ONE ocean state
  serves every queued `WaterPlane` this release. The producer updates once per frame, not per plane.
  Multi-sea-state is out of scope and filed.
- **Water height query ([#297](https://github.com/APKiwiOrg/KhaozEngine/issues/297)).** Scoped OUT, deliberately
  and explicitly. The height now lives in a GPU texture, so the closed-form answer the procedural surface could
  give does not exist here. The three options (read back a coarse cascade, keep a CPU-side low-order
  approximation, or evaluate a CPU inverse transform of the coarsest cascade) all have real costs and none is
  obviously right without a consumer telling us what latency it can accept. #297 carries the choice.

## Deferred, deliberately

- **The water height query**, per above. #297.
- **Per-body sea states.** #275.
- **A projected grid.** #296.
- **Foam that advects.** Foam accumulates in the reference frame of the map, so it does not drift with the
  horizontal displacement. At the scales this renders at the difference is not visible; at a close-up it would be.
- **Spectrum bake on the GPU.** It is a few milliseconds on a sea-state change and would cost the headless tests.
- **Wave-height authoring.** Height follows from wind and fetch by design. A game that wants "the same sea, but
  bigger" has to move the sea state, not a multiplier, and that is the intended behaviour rather than a gap.
