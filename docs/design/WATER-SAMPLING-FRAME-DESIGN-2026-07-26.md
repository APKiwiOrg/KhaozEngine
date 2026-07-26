# The FFT ocean's sampling frame

Design rationale for the 16.5.0 water release. Issue:
[#328](https://github.com/APKiwiOrg/KhaozEngine/issues/328). Sits on top of the 16.3.0 FFT ocean,
`docs/design/FFT-OCEAN-DESIGN-2026-07-26.md` ([#310](https://github.com/APKiwiOrg/KhaozEngine/issues/310)).

This is the **why**. What shipped and how to use it live in `CHANGELOG.md` 16.5.0,
`docs/USING-KHAOZENGINE.md` ("FFT ocean") and `KhaozEngine.Render3D/README.md`.

## Where this came from

Ruinborne played 16.3.1 and reported the release as a large improvement, with two asks:

1. The waves always run on one heading. On an island world the sea runs PAST the island rather than at it.
2. From an elevated vantage (about 35 metres up, roughly 600 metres of water in frame) the 250 metre cascade-0
   tile visibly repeats.

Both are properties of how the maps are SAMPLED, not of the maps. So nothing here touches the spectrum bake, the
two compute kernels, or the produced textures. That is the whole reason this release is cheap: no extra dispatch,
no extra memory, no change to the one stall per frame, and no golden re-bake.

## The load-bearing decision: the focus is a blend, not a rotation

### What was built first, and why it is wrong

The obvious implementation, and the one this program set out to build, is to rotate the sampling coordinate:
sample at `R(-phi(P)) . P`, where `phi(P)` turns the spectrum's dominant heading from the wind direction toward
the focus point. It was built, and it produces a perfect bullseye.

The reason is one line of algebra. Put the focus at the origin and write `P` in polar coordinates `(r, theta)`.
The direction from `P` toward the focus is `theta + pi`, so at full strength `phi = theta + pi - w`. The sample
coordinate's angle is then

```
angle(R(-phi) . P) = theta - phi = theta - (theta + pi - w) = w - pi
```

which does not depend on `theta` at all. **The entire plane maps onto a single ray of the map**, parameterized by
`r`. Every crest becomes a circle. The surface is a 1D profile revolved, which is exactly what the bake showed.

Backing the strength off does not repair it, it scales the damage. At strength `s` the map is
`(r, theta) -> (r, (1 - s) theta + const)`, an azimuthal COMPRESSION of the sample domain by `1 - s` and therefore
an azimuthal STRETCH of the world by `1 / (1 - s)`. At `s = 0.6` that is 2.5x, and the bake at that setting reads
as a whirlpool. There is no setting where the convergence is visible and the smear is not.

And it is not a tuning problem. Ask whether ANY non-constant rotation field can be a coordinate map: require
`dq/dP = R(-phi(P))` and equate the mixed partials. With `c = cos phi`, `s = sin phi`, integrability of the two
rows gives `-s phi_z = c phi_x` and `c phi_z = s phi_x`; multiply the first by `c`, the second by `s`, add, and
`(c^2 + s^2) phi_x = 0`. Both components of `grad phi` fall out zero. **Only a constant rotation is a valid
sampling-coordinate rotation**, so no version of this approach works, at any strength, with any smoothing.

The shape of the failure is worth keeping because it is not obvious from the code: the shader is short, every
term in it is right, and the bug is that the thing being asked for does not exist.

### What replaced it

Since a continuous per-position rotation is unavailable, the heading is carried by BLENDING fixed rotations. The
wanted rotation is quantized onto a ring of `OnshoreFocusSectors` fixed lattice rotations; the two either side of
it are sampled; the results are mixed.

Three properties make this the right shape rather than a workaround:

- **Every tap is undistorted.** Each is a plain constant rotation, which the algebra above says is exactly the
  case that IS a valid coordinate map. There is no smear at any strength.
- **The cost does not depend on the sector count.** Only the two sectors either side are ever non-zero, so 64
  sectors costs what 4 costs: two cascade samples per stage instead of one. That is why the count is exposed as a
  quality knob and defaults generously (12).
- **The mix is physically the right kind of thing.** Two decorrelated realizations of the same spectrum at
  headings one sector apart sum to a sea with directional SPREAD around the wanted heading. At the default that
  is about 15 degrees either side, narrower than `DirectionalSpread`'s own lobe. A real sea has spread; this one
  gains a little more.

The weights are L2-normalized (`(1-t, t) / sqrt((1-t)^2 + t^2)`) rather than linear, and that is not fussiness.
Displacement and slope are zero-mean Gaussian fields, so mixing two decorrelated realizations with weights
`(a, b)` gives variance `a^2 + b^2`. Linear weights would therefore drop the wave height to `sqrt(0.5)` halfway
through every sector, which would draw a ring of calm water around the focus point at each sector boundary. Foam
is NOT a Gaussian field - it is a bounded coverage - so it takes the plain linear pair, which preserves its mean.
`OceanFocusTests` pins both.

### The seam, which is real and cannot be removed

A partial focus has one heading discontinuity, on the ray running from the focus point in the direction the wind
blows. This is topological, not an implementation artifact: a uniform heading field wraps zero times around the
focus point and a converging one wraps once, and no continuous homotopy exists between fields of different
winding number. Something has to break somewhere at every intermediate strength.

It is placed where it is by wrapping the angular difference to the short way round, which is the choice that puts
it on one clean ray rather than smearing it. It closes at both ends of the range (0 turns nothing; 1 turns by a
whole turn, which is the same heading), so **strength 1 is seam-free and is the value to use**, and
`WindDirectionDegrees` aims the seam for a consumer that wants a partial focus anyway. The knob's own doc says
all of this, and a test pins the seam's location so it cannot move silently.

## Consistency between the two stages

The frame belongs to the VERTEX stage. It computes the focus rotation from the UNDISPLACED grid position and
hands it to the fragment as a varying, along with the (warped) sampling XZ. The fragment never re-derives it.

That is not tidiness. The only position the fragment has of its own is the DISPLACED one, so re-deriving there
would rotate the normals in a frame the displacement was never computed in, and the surface's lighting would
detach from its silhouette wherever the two disagreed - by a little everywhere and by a lot near the focus, where
the field's angular gradient is largest.

The rotation travels as a `(cos, sin)` PAIR rather than as an angle, and that is load-bearing too. An interpolated
angle would sweep the long way round wherever the angle wraps, which near the focus point is every triangle. A
pair interpolates to the chord, which always takes the short way. The chord is a hair short of unit length, and a
short pair scales the sample position as well as turning it, so the fragment rescales it - but only when it
actually is short, because neither hardware interpolation of a constant attribute nor `inversesqrt(1.0)` is
promised exact, and the unrotated case has to stay bit-exact.

Sampled VECTOR quantities (horizontal displacement, height slope) come back through the inverse rotation into the
world frame. Scalars the maps carry (height, foam, the Jacobian) are rotation-invariant and pass through. Rotating
the slope back matters more than it looks: the rotation preserves its LENGTH, so the normal stays unit and the
Toksvig variance the glint lobe receives is unchanged, but skipping it would light the whole surface as though the
waves ran on the unrotated heading while their geometry ran on the rotated one.

## De-tiling: three levers, and only one of them reaches cascade 0

The cascade ratio (4.2, deliberately not a power of two) already stops the three cascades sharing a repeat
PERIOD. What it does not touch is any single cascade's own period, and cascade 0's 250 metres is the only one big
enough to read as a structure at 600 metres of view. So:

- **Per-cascade rotation offsets** decorrelate the lattices DIRECTIONALLY. Without them all three repeat along
  the same two world axes and reinforce each other into one grain. This is worth having and does not touch
  cascade 0's period.
- **The domain warp** is the lever that does. Bending the sample position before the rotations means world space
  no longer maps onto cascade 0's lattice regularly, and the repeat stops being a repeat. It is the same trick
  14.22.0 used for the procedural ripples, at ocean scale, and STATIC rather than scrolling: at a wavelength
  several times the largest tile, a drifting warp reads as the whole sea sloshing rather than as detail. Its
  Jacobian is deliberately not folded back into the sampled slope, for the same reason the ripple warp's is not.
- **`CascadeTileMetres`** is the direct fix and its default was NOT moved. The tile size a scene wants follows
  from its camera rather than from the sea, and raising it drags the whole ladder with it, so the doc now states
  the trade instead.

**The warp has to be sized against the tile.** This was measured rather than guessed, from a 1500 metre overhead
bake with six tile periods in frame: at 30 metres of amplitude the lattice is still plainly readable, at 100 it
wanders, at 150 it is gone. That is 40 to 60 per cent of the tile, which is far more than a "subtle" warp, and
the first bake at 30 was the one that showed it. The price is stated by `2 pi * amplitude / wavelength`, the local
stretch: 0.75 at the top of that band, so wavelengths vary by up to three quarters either way across the field.
That reads as the sea being livelier in some places than others. Past 1 the domain folds and the surface tears.

## Testing, and the untested-default trap

Every knob here defaults to the identity, so `scene3d_fftocean` and `scene3d_water` come out byte-identical and no
bake cycle is owed. That is exactly the trap [#298](https://github.com/APKiwiOrg/KhaozEngine/issues/298) names: a
feature whose default is "unchanged" ships with full green coverage of the path nobody asked for. The coverage
therefore runs the other way round.

- `OceanFocusTests` (headless) pins the frame maths against `Internal/OceanFocus`, the CPU mirror both shader
  stages are written from: the convergence claim itself from every azimuth, the seam's location, the two taps
  straddling the wanted heading, unit power across the blend, and finiteness at the focus point.
- `OceanFocusGpuTests` (on device) runs the features ENABLED on all three backends, and asserts the surface still
  reads as a lit sea. That count-and-spread assertion is the NaN detector: a non-finite displacement drops its
  whole triangle so the count collapses, and a non-finite normal drives the fragment to a constant so the spread
  does. Neither would fail a compile, and neither is visible in the maps, which this release does not touch.
- The byte-identity claim is itself a test rather than an assumption: the same scene with every new knob written
  EXPLICITLY at its default has to come out byte-equal to one that never touched them.

The exact-identity requirement is why several defaults are reached by an early return rather than by evaluating
the maths at zero. GLSL allows `sin`/`cos` a couple of ULP, so a backend answering 0.99999994 for `cos(0)` would
scale every sample position by that, and the unfocused ocean would stop being bit-identical to the one that
shipped before any of this existed.

## Deferred, deliberately

- **Real refraction.** Bending crests to follow depth contours needs a wave model over the bathymetry, not a
  sampling frame. The focus is a stylized stand-in that a consumer aims by hand.
- **Per-plane focus points.** One ocean state still serves every queued `WaterPlane`
  ([#275](https://github.com/APKiwiOrg/KhaozEngine/issues/275)), so there is one focus.
- **Animating the domain warp.** Static is a decision, not a gap: see above.
- **Choosing the warp amplitude automatically from the tile size.** Tempting, since the useful band is a fixed
  fraction of `CascadeTileMetres`, but it would couple two knobs whose right values also depend on how much water
  the camera sees, and a consumer that wanted the old look would have no way back.
