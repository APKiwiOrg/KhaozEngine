# World-locked water grid and mipped cascade maps

Design rationale for the 16.11.0 fix to the FFT ocean's camera-motion boiling
([#296](https://github.com/APKiwiOrg/KhaozEngine/issues/296), with the mipped-maps prerequisite
[#344](https://github.com/APKiwiOrg/KhaozEngine/issues/344)). Shipped API lives in `CHANGELOG.md`,
`docs/USING-KHAOZENGINE.md` and `KhaozEngine.Render3D/README.md`; this file is the why.

## The defect, measured

The water grid is camera-locked. `WaterMath.BuildGridPositions` lays a fixed 97x97 budget over the plane and
warps it toward the camera XZ, so every vertex translates rigidly with the camera: a 5 m camera move shifts
every vertex by exactly 5.0 m, with no snapping anywhere. The vertex stage then samples every cascade at
`textureLod(..., 0.0)`, with no band limit at all, while the grid spacing runs 7.7 to 20.4 m in the mid and far
field against cascade content down to 0.22 m.

Those two facts together are the artifact. The diagnosis probe (posted on #296, CPU mirror validated against
the GPU reference) froze wave time and stepped the camera:

| camera step | rendered height field changes by | as a fraction of one 60 fps frame of real motion |
|---|---|---|
| 0.10 m (walking) | RMS 0.0064 - 0.0076 m | ~85 per cent |
| 0.50 m (sprinting) | | 3.7x |

Nothing about the sea moved. All of that is resampling, and it reads as the ocean boiling in place.

## Two things that do not fix it

**Centre-snapping the existing grid.** Under `GridFocusBias > 1` the grid's vertex offsets are a power warp of
the parametric coordinate, so they are non-uniform and share no common quantum. There is no snap that leaves
the vertex set invariant, because there is no lattice to snap to. Turning the bias down to 1 gives a uniform
grid that could be snapped, but a uniform 97x97 over a 600-unit half-extent plane puts vertices 12 m apart,
which is why the warp exists.

**Dropping whole cascades in the far field.** Measured at 25 per cent of the artifact. A cascade carries a
broad spectrum; dropping it removes the resolvable content along with the unresolvable, and keeping it removes
nothing. What is needed is a low-pass WITHIN each cascade, which is a mip chain
([#344](https://github.com/APKiwiOrg/KhaozEngine/issues/344)) - the diagnosis put that at a 2.2 to 2.5x cut for
about 2 per cent of visible amplitude.

## The grid that shipped

`WaterGridMode.Clipmap`: concentric square levels, level 0 a solid block of `ClipmapRingCells` cells of
`ClipmapCellSize`, each level out a RING at twice the cell size with a hole in the middle. All of it is
`WaterClipmap`, pure CPU, headless-tested.

### The snap is to twice the cell size, and that is load-bearing

Level L's origin is the camera XZ rounded to the nearest multiple of `2 * cellSize(L)`. Rounding to twice the
cell rather than to the cell is what makes the nesting exact:

- level L-1's origin is then a multiple of `cellSize(L)`, so level L-1's outer boundary lands ON level L's
  lattice rather than halfway through one of its cells;
- level L's hole is therefore a whole number of level-L cells, offset from level L's own centre by
  `d` cells with `d` in `{-1, 0, +1}` (the two origins are each within a cell or half a cell of the camera);
- and a snap moves a level by an EVEN number of its own cells, which maps its lattice onto itself.

That last one is the whole payoff. For a sub-cell camera step nothing moves at all. For a larger step each ring
jumps by whole cells, and every vertex that is still in range is at the same world position it was at before, so
the triangulation over the overlap is identical rather than resampled. The only thing that changes is the strip
each level gains or loses at its boundary.

The alternative - one shared origin snapped to the COARSEST cell, which also nests exactly and has a fixed
index layout - was rejected because it couples the near field's tracking to the far field's extent. At 9 levels
from a half-metre base cell the shared quantum would be 128 m, so level 0 would only track the camera to within
64 m while covering 8. Per-level snapping tracks to half a metre at any level count, at the cost of a hole whose
position varies by one cell, which is why the index buffer is rebuilt on a snap rather than built once.

### Ring transitions: stitch vertices, not skirts

Level L-1's outer edge has twice the vertex density of level L's inner edge, so the shared edge has T-junctions.
The usual answers are both wrong here. A skirt is vertical geometry hanging under the seam, and the water pass is
ALPHA-BLENDED, so a skirt double-blends wherever it is visible. Collapsing the fine level's odd boundary vertices
onto their neighbours closes the seam with zero-area triangles, which is the degenerate geometry the whole
transition is supposed to avoid.

What shipped instead: the fine level's outermost vertices that have no counterpart on the coarse lattice carry a
`Stitch` offset naming their two coarse neighbours, and the vertex shader evaluates the surface at BOTH and
averages. Averaging two displaced positions puts the vertex on the straight world-space segment the coarse ring
draws between them, and a point on a world-space segment projects to a point on the projected segment - so the
seam is exact, not nearly exact. It costs one extra tap on `4 * ringCells / 2` vertices per level, which at the
defaults is 5 per cent of the grid, and no extra geometry at all.

The other half of the seam is the band limit: every vertex on a shared boundary (on BOTH sides) carries the
COARSE level's cell size in its `Cell` attribute, so the two sides low-pass identically and evaluate to the same
height before the stitch is even considered.

### Band limit

Each vertex low-passes the cascades to its own ring's Nyquist: mip `log2(spacing * samples / (2 * texel))`,
clamped to the chain, with `samples` = `ClipmapBandLimitSamples` (2, plain Nyquist, by default). The fragment
does the same against its pixel FOOTPRINT rather than a cell size, which folds into the `rippleResolve` logic
that was already there: `keepAll` (how much of the cascade the pixel can resolve at all, unchanged, and what the
Toksvig transfer wants) stays the total, and `keep` becomes only the residual attenuation the mip chain did not
already do. At mip 0 the two are the same expression, which is what keeps the camera-focused path arithmetically
untouched.

Foam is still not scaled by the band limit - a whitecap two kilometres out is still white - but it does ride the
same mip, which is right for it: foam is a bounded coverage, so box-averaging over the footprint preserves its
mean where a point sample at LOD 0 just aliases.

## Mips: two textures, because a storage image is one mip level

The obvious implementation is to give the compute pass's output texture a mip chain and call `GenerateMipmaps`
on it. That does not work: a storage-image binding must cover exactly one mip level, the seam binds whole
textures rather than views, and a view spanning a chain is invalid as a storage image on Vulkan.

So the compute target stays single-mip and byte-for-byte what it was (which also keeps the producer's existing
determinism guarantees intact), and a second SAMPLED texture carries the chain. Each frame in clipmap mode the
producer copies the base level across per array layer and calls `GenerateMipmaps`, both into the SCENE's command
list beside the column dispatch. That adds no GPU stall: the seam's expensive ordering rule is about a DISPATCH
reading what a dispatch wrote, and this is a transfer, which is where all three backends do emit the
synchronisation for free (Vulkan transitions the image out of its storage layout, Metal ends the compute encoder
to open a blit encoder, Direct3D11 serialises the resource). That reasoning is checked rather than trusted:
the acceptance test asserts mip 1 equals a CPU box downsample of mip 0 on every backend, which fails loudly if
`GenerateMipmaps` ran before the compute writes landed.

Measured cost on Metal at 128 texels over three cascades: the producer goes from about 0.37-0.47 ms/frame to
about 0.44-0.52 ms/frame, so roughly +0.1 ms, and still exactly one stall.

## Why it is opt-in, and why it is also cheaper

`CameraFocused` stays the default so every consumer and every golden is untouched, verified rather than assumed
(the full Metal GPU suite, including both water goldens, passes unchanged). The switch that makes it exact is
`FftParams.w`, the top mip index: with no chain it is 0, `oceanMip` early-returns 0, and both stages sample a
literal `textureLod(..., 0.0)` exactly as before. The default vertex shader also pins `taps` and `bandCell` as
compile-time constants, so its tap loop unrolls away.

It is worth being clear that the clipmap is not a quality-for-performance trade. At the defaults on a 600-unit
half-extent plane it is 9 levels: 9801 vertices and 14336 triangles against the camera-focused grid's 9409 and
18432, so 22 per cent fewer triangles, with half-metre cells around the camera instead of the warp's
half-a-unit-to-22-units spread, and coverage to 2048 m. And because the geometry only changes when a ring
actually snaps, most frames upload nothing at all, against 113 KB of vertices every frame unconditionally.

## What it measures, and what is left

The acceptance test is the diagnosis's own experiment, permanent: freeze wave time, step the camera, measure the
RMS change in the rendered surface over a world-fixed probe lattice, and report it against one 60 fps frame of
the field's own motion. It takes the WORST over five start offsets spanning the innermost snap period, because a
world-locked grid that does not happen to snap on a given step scores a perfect zero and would say nothing about
the frames where a ring does move.

At 64 texels over three cascades (the small size the software CI legs can afford):

| | camera-focused | clipmap |
|---|---|---|
| one frame of real motion | 0.00732 m RMS | |
| 0.10 m step | 0.00287 m (39 per cent of motion) | 0.00086 m (12 per cent) |
| 0.50 m step | 0.01343 m (183 per cent) | 0.00086 m (12 per cent) |

3.3x better at a walk, 15.6x at a sprint. The more important number is that the clipmap's is the SAME at both
steps: its residual is one ring-boundary band wide however far the camera went, while the camera-focused grid's
error is proportional to the distance travelled and so gets worse the faster you run.

That residual is the ring boundary itself: when a ring snaps, an annulus one coarse cell wide changes which
level draws it, and therefore which mip it band-limits to. Smoothing it would mean morphing the LOD across a
transition band (the geoclipmap geomorph), which is a second mechanism with its own tuning and is deliberately
not in this release. Filed as [#348](https://github.com/APKiwiOrg/KhaozEngine/issues/348).

## Camera-relative rendering

16.8.0 made every submission camera-relative: the plane, the grid and the eye arrive already reduced by a
`RenderOrigin` quantized to the 128 m frame grid, and the shader adds it back as `aXz` for everything anchored
to the world (the swell phase, the ocean sampling frame, the ripple and foam lattices). The clipmap composes
with that, but only in one particular order.

**The lattice is decided in absolute world space; the reduction happens on the ring ORIGINS, never per vertex.**
Both halves matter and they are separate claims:

1. *Snap absolute.* If the snap ran on render-frame coordinates, a rebase would re-quantize every ring: the same
   world position would round to a different lattice node, every vertex would jump, and the surface would be
   resampled. That is the artifact this grid exists to remove, reintroduced by the fix for a different problem.
   `WaterClipmap.Build` therefore takes an absolute plane and an absolute focus.
2. *Reduce on the origin.* A ring origin is a whole multiple of `2 * cellSize` and the render origin a whole
   multiple of 128 m, so both are exact integers in float32 and their difference is exact. A per-vertex absolute
   position is neither. Subtracting there instead measurably re-quantizes the grid: at 100 km with a 0.3 m cell
   the two orderings diverge by 3.1 mm, which is a lattice error, not a rounding detail.

The second point is easy to test wrong. At the default 0.5 m cell every offset is a whole multiple of the
float32 spacing at 100 km, so a per-vertex subtraction is exactly harmless and a test at the defaults passes
either way. `ClipmapCellSize` is a free float, so the test runs at 0.3 as well, where the difference is real.

The stitch taps recover their own absolute per tap (`aXz = sxz + RenderOrigin.xz` inside the tap loop rather than
once outside it), since a stitched vertex samples at two positions.

## Deferred

- **LOD morph across ring boundaries** to remove the remaining 12 per cent
  ([#348](https://github.com/APKiwiOrg/KhaozEngine/issues/348)).
- **A committed golden for the clipmap.** Judged against [#332](https://github.com/APKiwiOrg/KhaozEngine/issues/332):
  a golden would need a per-backend CI bake and would then re-render on both hosted legs on every push, and what
  it buys over the statistical render test is sensitivity to a small LOOK shift on a mode no consumer has adopted
  yet. The render test compares the clipmap's picture against the camera-focused one of the same sea instead,
  which proves the new vertex layout and shader variant cross-compile and draw correctly - the actual per-backend
  risk - and says what the picture has to BE rather than what it was on the day it was baked.
- **A geomorph for the render-origin rebase.** A rebase leaves the lattice alone but does re-upload every vertex
  (they are render-relative), so it costs one rebuild frame. That is one frame per 128 m of travel and was not
  worth a mechanism.
- **`GridFocusBias` under the clipmap** is inert by design and stays that way. The two are alternatives: the
  power warp is precisely the thing with no snap quantum.
