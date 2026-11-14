# Tile ground relief and natural material blending

The rationale behind `TileGroundRelief`, the relief tile-ground programs, and the ground catalog's relief fields
([#1799](https://github.com/APKiwiOrg/KhaozEngine/issues/1799), milestone E7 of the world look fidelity program
toward [#1776](https://github.com/APKiwiOrg/KhaozEngine/issues/1776)). Behaviour and API live in the
[Render3D README](../../KhaozEngine.Render3D/README.md) (`TileGroundRelief`), the
[TileWorld.Render3D README](../../KhaozEngine.TileWorld.Render3D/README.md) (ground materials and feathers) and the
[TileWorld README](../../KhaozEngine.TileWorld/README.md) (catalog fields). This page keeps the alternatives
weighed and why each decision stands. Scores are 1 to 10, higher is better.

## The problem

Tile ground was albedo only, so stones read as a flat print under the sun, and every transition was a uniform
ramp: corners blended linearly across a tile and a feathered overlay faded inward over one global width. A cut
stone apron and a dirt track got the same even smear, along the tile grid. Two fixes were engine work: maps on the
ground, and transitions that follow what the maps describe.

The hard constraints: visual only (`TileGroundTriangles`, raycast, collision and navigation inputs unchanged),
deterministic and seamless across region borders and both ground LODs, and no cost for a consumer that ships no
maps.

## A plain and relief program pair, chosen per mesh

| Criterion | One shader, features behind uniform branches | Plain and relief programs |
|---|---|---|
| A map-less consumer keeps today's cost | 6 (register pressure of untaken code) | 10 (unchanged program) |
| A map-less consumer keeps today's pixels | 5 (whole fragment recompiles) | 10 (shader bytes unchanged) |
| Plumbing | 9 | 6 (second layout, more programs and hash rows) |
| Physically based lighting later | 8 | 7 (two fragments share GLSL helpers) |
| Total | 28 | 33 |

The pair was then refined by measurement. The first relief fragment, with every feature in one program, cost about
twice the plain pass even on fragments that used none of its maps, so the cost was the code's register footprint,
not its taps. Keeping every role in named registers instead of dynamically indexed local arrays roughly halved it,
and compiling hex-tiling into its own program halved the remaining overhead again. The program is chosen per MESH
from the slots its vertices name, because a world-wide choice made one hex-tiled material bill every fragment of
the world, while per-mesh choice bills only the regions that paint relief. A relief material therefore keeps a
plain-program resource set too, and the plain block being the relief block's prefix is what lets one params buffer
serve both.

## Packing: height in the albedo alpha

| Criterion | Albedo a = height, surface = normal rgb + roughness a | Surface = normal xy + roughness + height | One array per map |
|---|---|---|---|
| Memory | 9 (height free, surface only for mapped layers) | 7 | 8 |
| Taps | 9 (height rides the albedo tap) | 7 | 5 |
| Bindings | 8 (one new) | 8 | 5 |
| Block compression later | 7 | 8 | 9 |
| Mip quality | 8 (normal z kept) | 6 | 8 |
| Total | 41 | 36 | 35 |

Single-channel maps read GREEN, a grayscale image's value and where glTF metallic-roughness keeps roughness, so a
kit's metallic-roughness texture and a plain grayscale map both work. One size per array is kept, with the two
arrays independent, for the reasons the TileWorld.Render3D README gives beside the rule.

## The height blend operator

The operator has to keep a weight of 0 at 0 (or a shared corner or edge seams), leave map-less neighbours exactly
linear (or adding one mapped material to a set would sharpen every grass and dirt boundary in the world), resolve
crisply along height, and never pop at the 0.001 weight below which the fragment skips a role's taps.

| Criterion | HDRP max minus transition | Log-weight softmax with a flat group | Unreal clamp(2w - 1 + h) |
|---|---|---|---|
| Continuity at shared corners and edges | 8 | 10 | 7 |
| Map-less neighbours stay linear | 2 | 10 | 4 |
| Crisp along height | 9 | 9 | 5 |
| Bounded intrusion, no pop | 8 | 9 | 8 |
| Cost | 9 | 8 | 9 |
| Total | 36 | 46 | 33 |

Map-less materials join one flat group at height 0.5 whose members keep their linear split, which is what keeps
them linear among themselves. Sharpness is a per-material choice, averaged by weight over the mapped materials in
play, because a crisp cut-stone edge and a soft dirt and grass edge are both right and a per-set knob forces one.
Mixing per-material exponents inside the softmax was rejected: it penalises the sharper material's weight term and
shifts boundaries away from where the heights put them. The influence ln 10 bounds how far a height difference moves
a boundary, which bounds the skip-threshold pop at 1 percent at the softest sharpness.

## Feather width belongs to the material, and stays inward

| Criterion | Per material (catalog) | Per tile (document) |
|---|---|---|
| Authoring | 9 | 5 |
| Format stability | 10 | 4 (tile layer or settings bits, format version) |
| Expressiveness | 7 | 9 |
| Seamless within one overlay union | 10 | 6 |
| Total | 36 | 24 |

The half-tile cap stays: an overlay one tile wide must still reach full strength on its centre line. A feather does
not reach into neighbouring tiles: a vertex carries one overlay slot, so a tile beside two different overlays could
not carry both, and keeping overlays inside their own tiles leaves the underlay tiles' meshes, coarse LOD cells and
raycast surfaces untouched by overlay authoring.

## Edge breakup warps inward only

The breakup is a world-space noise warp of the feather weight in the fragment, so its detail is finer than the
feather subdivision and it costs no vertices. Its exponent was first symmetric, `o ^ exp2(2 b n)`. The GPU test that
checks the boundary caught the flaw: an exponent below 1 lifts the overlay to a large share just inside the tile
boundary, which draws exactly the grid line the breakup exists to hide. The exponent is now `1 + 6 b m`, never below
1, so the boundary fringe stays as soft as the straight feather and the visible edge wanders inward. Two summed value
noise octaves rarely leave the middle of their range, so the noise is stretched before it drives the exponent.

## Hex-tiling, and where it stops

| Criterion (hiding the repeat counted twice) | Hex-tiling | Macro variation | File it |
|---|---|---|---|
| Hides a 1.8 m repeat over 24 m | 18 | 8 | 0 |
| Cost | 6 | 9 | 10 |
| Fit with the rest | 8 | 8 | 10 |
| Risk | 6 | 9 | 10 |
| Total | 38 | 34 | 30 |

Hex-tiling (Mikkelsen 2022) was built as an opt-in per material. Captures of a Voronoi cobble then showed its limit:
it hides the repeat of stochastic textures such as grass and dirt, and shatters a mosaic of discrete stones,
because its copies overlap stone on stone and any seam cuts a stone. Weighting a height-mapped layer's hexes by
exp(4 h) makes seams follow joints where a joint meets a stone, which helps, but cannot help where stone meets
stone. Hiding a mosaic's repeat is therefore its own design,
[#1803](https://github.com/APKiwiOrg/KhaozEngine/issues/1803), and the README tells authors to leave cobble and
flagstones at None.

## Measured cost

Apple M2 Max, Metal, 1920x1080, 512 px layers, tile-ground pass medians over three interleaved rounds of 120 frames
from `TileGroundReliefCostDiagnosticTests`, transition-heavy ground then single-material ground:

| Program | Transition-heavy | Single material |
|---|---|---|
| Plain | 0.60 ms | 0.48 ms |
| Relief set, mesh naming no relief layer (plain program) | within noise of plain | within noise of plain |
| Relief, no maps | 0.93 ms | 0.76 ms |
| Relief, height, normal and roughness maps | 0.98 ms | 0.77 ms |
| Relief with hex-tiling | 2.18 ms | 1.34 ms |

The maps themselves are cheap and the relief program's cost is mostly the state it keeps for five roles, which is
why the next step, sampling surfaces only after the height blend, is filed as
[#1801](https://github.com/APKiwiOrg/KhaozEngine/issues/1801). Memory: a 512 px RGBA8 layer with mips is 1.33 MiB,
height adds nothing, and the surface array adds one layer per material with a normal or roughness map.

## Not done here

- MapDoc native terrain still draws albedo only, and the scene resource upload refuses relief layers until it can
  carry them, [#1800](https://github.com/APKiwiOrg/KhaozEngine/issues/1800).
- The steep companion stays a slope blend and takes no part in the height blend. A rock companion's heights could
  resolve the grass to rock shoulder the same way, once a game asks for it.
- Specular anti-aliasing for normal-mapped ground (a Toksvig-style roughness lift for minified normals) belongs with
  physically based lighting, which takes the same roughness value.
