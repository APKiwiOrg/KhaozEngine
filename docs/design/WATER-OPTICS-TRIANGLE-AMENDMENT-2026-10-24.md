# Triangle-backed optical beds

Status: Execution amendment selected by the engine orchestrator under the owner's authorization to
resolve failed optical gates. Implementation and verification remain open. No release is authorized.

Task 5 final evidence, local Metal, Release, with the exact datum and certified seam charge: the parent Task 6
visual gate passes on local Metal. The unchanged saddle fact passes with one live field and upload. At the
0.08 m probe, authored influence is 0.0722819 against scene-depth control 0.20033678, within the 0.04 allowance.
All 11 focused GPU facts pass with none skipped. They cover the other diagonal, absent-overlay forced rotation,
all four corner-cut rotations on level and sloping cut edges, diagonal-half overlays with material, feather
subdivisions on a sloping -0.70 m edge, a Coarse4 interior with transition fans, a Full and Coarse4 region border
with neighbouring fields and a 2048 m render-origin reduction. Every admitted GPU probe hits and lies within its
field's certified bound of a binary64 drawn-mesh oracle. The largest error is 4.5e-8 m, at most 4.7 percent of
that bound. Every image matches an independent field built from the drawn triangles at 0 RGB, and every negative
witness fails both checks. The feather field holds 1052 triangles in 32 by 32 bins, with occupancy 30 and a
certified bound of 9.86e-7 m. Hosted Metal, D3D11 and Vulkan verification, final reconciliation, review and the
remaining parent gates stay open.

This amends [per-body water optics](WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md) for the failed Task 6
saddle gate. It supersedes the bilinear-only TileWorld producer and universal four-fetch requirement.
Existing `WaterOpticsField` callers retain their bilinear contract. All original immutable ownership,
strict coverage, pre-record preparation, retirement, legacy compatibility and backend gates still apply.

Execution follows the [sequential amendment plan](../superpowers/plans/2026-10-24-water-optics-triangle-amendment.md)
inside parent Task 6.

## Evidence and decision

The public saddle has SW and NE heights 0 m and SE and NW heights -1 m. The actual terrain mesh joins
the high diagonal. Its center height is 0 m, while bilinear `HeightAt` gives -0.5 m. Water at -0.02 m
therefore remains fully tinted up to a hardware depth-test cut. The unchanged 0.08 m probe measured
authored influence 0.37254903 against scene-depth control 0.20033678, beyond the fixed 0.04 allowance.
The failing proof is committed in `7d3feccdd3f85536fdffcf7b8c587dc25f237574`.

`TileTriangulation` selects diagonals from integer heights or authored rotation. Corner cuts use four
triangles. Coarse4 can emit larger triangles and transition fans. An optical diagonal inferred from
four float depths cannot represent all of those surfaces. `GltfMesh.Indices32` and indexed positions
of the actual completed mesh are the source authority, including feather subdivisions.

Scores are engineering judgments, with 10 best. Simplicity includes validation and maintenance.

| Approach | Correctness | Compatibility | Simplicity | Total |
| --- | ---: | ---: | ---: | ---: |
| Actual triangles in one bounded R32Float atlas | 10 | 9 | 6 | 25 |
| Generic topology sidecar over the existing scalar lattice | 9 | 9 | 4 | 22 |
| Dense bilinear resampling | 3 | 10 | 8 | 21 |

Use actual triangles. A complete sidecar also needs arbitrary vertices and connectivity, duplicating
the scalar representation. Finite bilinear resampling remains approximate across arbitrary creases.
The atlas costs more shader reads and source-generation coordination. Measure those costs, rather
than claiming unchanged rendering performance from unchanged allocation counters.

## Neutral immutable resource

Add these Render3D public types, with no TileWorld dependency:

```csharp
public readonly record struct WaterOpticsTriangle(Vector3 A, Vector3 B, Vector3 C);
public readonly record struct WaterOpticsRectangle(Vector2 Minimum, Vector2 Maximum);
public sealed class WaterOpticsTriangleField
{
    public const int MaxDimension = 1024;
    public const int MaxTrianglesPerBin = 32;
    public WaterOpticsTriangleField(Vector2 size, int binColumns, int binRows,
        ReadOnlySpan<WaterOpticsTriangle> triangles,
        ReadOnlySpan<WaterOpticsRectangle> certifiedRectangles);
    public Vector2 Size { get; }
    public int BinColumns { get; }
    public int BinRows { get; }
    public int TriangleCount { get; }
    public int EdgeLineCount { get; }
    public int CandidateReferenceCount { get; }
    public int MaximumBinOccupancy { get; }
    public int TextureWidth { get; }
    public int TextureHeight { get; }
    public long TextureBytes { get; }
    public long RetainedBytes { get; }
    public float ReconstructionErrorBoundMetres { get; }
    public ReadOnlySpan<WaterOpticsRectangle> CertifiedRectangles { get; }
    public float SampleDepth(float u, float v);
    public bool TrySampleDepth(float u, float v, out float restDepth);
}
```

Triangle vertices contain local X metres, signed rest depth metres and local Z metres, in that order.
They are finite and inside `[0, Size]`. Projected triangles must have nonzero area. Construct an extent
containing complete source triangles, without clipping or moving their vertices to fit a water patch.
The field owns its packed atlas and certificate arrays, retaining no mutable caller arrays. Temporary
indexing and validation structures are released after construction.

Invalid ordinary arguments throw `ArgumentException`. Unsupported surfaces throw
`WaterOpticsSurfaceException : ArgumentException` with readonly `Reason` of enum
`WaterOpticsSurfaceFailure { IncompleteCoverage, AmbiguousSurface, CapacityExceeded, UnrepresentableSurface }`.
The adapter maps typed reasons to diagnostics rather than parsing exception text.

Normalized CPU queries outside `[0,1]` or containing non-finite values throw. Within that range,
`TrySampleDepth` returns false outside certified coverage. `SampleDepth` throws there. No sample value
represents missing data. Signed depths remain signed until the existing displaced-water depth clamp.

Add `Scene3D.LoadWaterOpticsTriangles(WaterOpticsTriangleField field) -> WaterOpticsHandle`. Use a
distinct method name to preserve null-literal source compatibility of `LoadWaterOpticsField`.
Both kinds share owner, slot, generation, unload and explicit new-scene reload contracts. Triangle
bindings require Size equal to the field Size. Existing bilinear mapping rules remain unchanged.

An internal immutable `WaterOpticsPayload` adapts either field for dimensions, kind, bytes, retained
storage and coverage. Resolution creates no wrapper or copy. `WaterOpticsResources.Entry` retains it.
Checked accounting charges padded texture storage and retained CPU storage through actual retirement.
Preserve the four positional `WaterOpticsStats` members. Add non-positional live/retired CPU-byte
properties. Do not claim to measure opaque driver overhead or managed object headers.

## Bounded atlas

One sampled R32Float texture uses existing bindings 10 and 11. Both axes are 2 through 1024. Every
encoded address and count must round-trip through float32 exactly. The scalar limit is 1024 squared.

| Scalars | Content |
| --- | --- |
| 0 through 15 | Version 1, bin columns, bin rows, reference base, triangle base, edge base, triangle count, edge count, used scalar count, seven zero reserved values |
| 16 + 2 * bin | Absolute first-reference address and candidate count |
| Reference range | Triangle ordinals, ascending in each bin |
| triangleBase + 12 * triangle | ax, az, qa, bx, bz, qb, cx, cz, qc, three signed edge references |
| edgeBase + 4 * edge | Canonical line endpoints loX, loZ, hiX, hiZ |
| Physical padding | Zero, never addressed as payload |

For B bins, L references, T triangles and E edge lines, used scalars are
`U = 16 + 2*B + L + 12*T + 4*E`. Width is `min(1024, max(2,U))`. Height is
`max(2,ceil(U/width))`. Charge `checked(4L*width*height)` bytes. Refuse overflow or height above 1024.
Bin axes range from 1 through 1024, subject to storage limits. No bin contains more than 32 candidates.
Use triangle/bin intersection, including closed boundary support, rather than bounding-box-only lists.

Triangle input order defines stable ordinals. TileWorld orders sources by region, then emitted index
order. A signed edge reference is plus or minus `(lineOrdinal+1)`, selecting its inward half-space.

## Sampling, edges and precision

`OpticsParams.y` becomes 0 for unbound, 1 for bilinear and 2 for triangle atlas. Its other components
remain reference Y and physical texture dimensions. `OpticsRect` retains relative minimum and inverse
extent. UBO payload remains 1088 bytes and slot range 1280 bytes. Mode 0 returns legacy depth before
atlas reads. Mode 1 retains its four-fetch sampling and final-row convention.

Mode 2 maps render-relative XZ to bounded local metres, selects a bin with an exact outer-edge
last-bin convention, and visits at most 32 candidates in ascending order. Canonical line predicates
select a containing triangle. Barycentric interpolation of its three signed depths produces rest
depth. Apply `max(0, restDepth + displacedY - referenceY)` only afterward. No scene-colour pass is added.

The TileWorld producer stores rest depth against the exact datum 0. Each vertex holds `q = -y`, an exact
binary32 negation, and the binding reference Y is 0. `TileGroundMesher.WorldMatrix` has no Y translation, so
world y is the mesh's binary32 y and depth conversion adds no error. Collinear drawn geometry, such as the
mid-edge vertex of a sloped corner cut against its uncut neighbour's long edge, stays exactly collinear in the
field. Subtracting from a water level such as rim minus 0.02 m would round that vertex off the long edge, and
the field would charge that seam to its error bound. The water level stays on the plane and keys field
sharing per reference level and domain, as well as refusal diagnostics.

With datum 0 the rest depth is the absolute vertex height, so the certified arithmetic bound scales
with absolute height, not with water depth. Binary32 rounding near height `|y|` is about `|y|` times
2^-23. `TileWaterOpticsAltitudeTests` raises sloped corner cuts and a 2 m per metre bank to plus and
minus 300 m. The worst certified bound is about 3.8e-5 m against the 1e-4 m budget, and off-grid
samples against the binary64 drawn-mesh oracle err by at most 2.86e-5 m, so those fields publish with
about 6.2e-5 m of headroom. The mapping term still grows with the steepest triangle's slope times the
field extent. A very steep, wide field near the plus or minus 327.68 m short-centimetre height limit
can exceed the budget and refuse as `UnrepresentableSurface`. No headless fixture pins that edge,
because it is not ordinary content.

Canonicalize encoded vertices exactly. Collinear edge segments, including long/short T junctions,
share one supporting-line record with lexicographically ordered extreme source endpoints. Exact
predicate fallbacks during preparation may use BCL BigInteger over binary32 dyadic values. Do not
weld near vertices, expand triangles with an epsilon, or move source geometry. Positive-area overlaps of
distinct planes are refused as `AmbiguousSurface` at any depth difference, rather than resolved by order.

Shared segments and points whose XZ geometry coincides but whose depths differ are certified instead.
Feather subdivisions placed in binary32 on an uncut neighbour's sloping long edge are the motivating
case, up to 2.61e-8 m off the line. Depths are linear along a shared segment, so its exact largest jump
is reached at a clipped endpoint. For each bin the constructor sums the largest jump of every
discontinuous supporting line and also takes the largest point-only jump. The largest bin total is
charged to `ReconstructionErrorBoundMetres`. A query and its mapped point share one expanded bin. Moved
into the interiors of their triangles, the straight segment between them avoids every vertex and crosses
each supporting line at most once, so the bin total bounds every jump it crosses. Closed predicates and
the first ordinal still settle boundary ownership. For every query whose exact local point lies in a
certified rectangle, the owner is then within the bound of every drawn triangle containing the query. A
continuous bed is charged nothing. A total above 0.0001 m with sampler error refuses as
`AmbiguousSurface`. The mesher, terrain topology and LOD are unchanged.

CPU and shader arithmetic and operation ordering must be explicit and independently tested. Canonical
lines alone are not proof of numerical correctness at multi-edge vertices. Implement an error-bounded
predicate strategy with a robust fallback where needed, or refuse uncertifiable inputs before upload.
An admitted fragment must find a candidate. Defensive shader discard is an invariant-failure guard,
never an accepted coverage policy. Diagnostic GPU tests must expose misses, not turn them into depth.

TileWorld preserves its positional budget `min(0.0001 m, TileSize/1024)` across source-to-field and
render-origin conversion. Account separately for slope-amplified coordinate and barycentric error.
The triangle producer must certify a conservative 0.0001 m reconstruction-error budget. Refuse
unrepresentable conditioning. This is not an excuse to reject ordinary 2 m, 30 m and 100 m fields.

`ReconstructionErrorBoundMetres` is the least binary32 upper bound on the constructor's exact rational
arithmetic, local-mapping and shared-boundary discontinuity bound. The producer adds source conversion
and render-origin costs to this consumed value within the same 0.0001 m total. It does not receive
another independent budget.

The scene sampler implements the ordered binary32 operations with integer significands, guard,
round and sticky bits, including nearest-even restoring division. Exact canonical-line containment
sums six signed coordinate products. Each binary32 coordinate is an integer significand below `2^24`
times a power of two, including unnormalized subnormal significands. Remove the minimum exponent shift
of the nonzero products. When their exponent-shift span is at most 77, each product is below `2^125`
and their total magnitude is below `6*2^125 < 2^128`. Two unsigned four-word accumulators therefore
retain every bit and carry. Compare positive and negative magnitudes to determine sign, including exact
cancellation. Zero products do not widen the domain. All-zero products return zero. Wider spans use
the exact 18-word unsigned stream described below. No floating-point filter changes domain selection or sign.
This avoids optional doubles, float contraction and native division tolerances without changing
mode 0 or mode 1 arithmetic or the software interpolation operations.

The wide fallback caches six exact 24-by-24 products in the original signed operand order through
one retained preprocessing loop. One local six-record `uvec4` table stores three shifted fragments
and a start-word/sign tag, written once per record and then read-only. No whole table is passed
through a helper or copied as loop-carried state. Every shift count is bounded to 0 through 31,
including the zero-offset selection. Products at shift 506 reach word 17.

One retained low-to-high 18-word scan contains one retained six-contribution loop. Literal x/y/z
record reads select each digit. Separate scalar unsigned positive and negative low words begin at
their incoming carries. Unsigned wrap increments outgoing counters, each bounded to 0 through 5.
The highest differing normalized word overrides the sign. All 18 words are processed. The six
magnitudes total below `2^557`, so both final carries are zero within the 576-bit stream. Integer
reassociation changes no exact determinant bit. No 18-word array, carry walk, early low-word return,
17-word truncation or repeated product evaluation is permitted. Source-local `dont_unroll` controls
retain preprocessing, scan, contributions, candidate, three-edge and 27-step division loops through
the actual pinned SPIR-V and HLSL emissions. This internal replacement is authorized by the engine
controller. Public domain, admission and error budgets are unchanged.

Interpolation now retains its original 23 ordered software operations in one fixed `dont_unroll`
loop, selecting scalar operands into one add, multiply or divide body and storing each rounded result
in sixteen loop-carried scalar registers through sixteen explicit literal destination selects,
`r[k] = destination == k ? result : r[k]`, written separately for k from 0 through 15. This
controller-selected representation replaces the old sixteen-case destination switch and its
16-by-17 pass-through phi matrix. Keep the uint 23-case operand selector and arithmetic dispatch.
No slot loop, dynamic destination, bit mask, helper interpreter or branch hint accompanies this change.
Actual pinned
SPIR-V/HLSL must contain no 16-element interpolation bank or dynamic destination store.
All sixteen scalar entry values are explicitly uint zero. Every selected arithmetic read follows
its producing write, so these initial values only define otherwise unused pass-through state.
Trace each emitted select to its matching destination equality, the one common rounded result on
the true edge and that same slot's old scalar on the false edge, then its own header backedge.
Require sixteen unique routes and no destination multi-case switch or case-copy matrix. Reject
wrong conditions, cross-slot passthrough and wrong results. If the pinned compiler reconstructs
the matrix or a bank, or duplicates arithmetic, stop before hosted native expense.
Each slot therefore receives R iff d equals k and otherwise keeps its own old value, including
out-of-range destinations. Assignments cannot clobber another slot. Defined zero entry and the
unchanged 23-step operand trace establish bit equivalence by induction.
Every subtraction retains its sign-bit XOR. Constant/equal-zero
depths still return before interpolation. This shares compiler input without relying on a function
boundary surviving inlining. The narrow predicate also shares one unchanged product body through
a retained six-term loop in the original signed operand order, with unchanged four-word accumulators,
span-77 classifier and descending comparison. Bin mapping shares one software multiply body through
four ordered steps, u, v, column and row, with the unit clamp after each of the first two products.
A local two-word bank retains u and v. Triangle origin subtraction shares one software add body
through two ordered axes, X then Z, retaining the sign-bit XOR and signed-zero behavior. The arithmetic
helpers, CPU reference, exact wide sign, admission and mode 0/1 arithmetic remain unchanged.
The previous rolled-wide and interpolation candidates failed cold compile admission in hosted runs
40091542332 and 40094097628. The shared predicate and mapping candidate still requires a candidate-only
hosted Windows proof, with pixel FXC at most 5000 ms and the measured six-phase total at most 10000 ms.
These are engineering limits for that fixed proof, not universal hardware guarantees. Actual retained
SPIR-V/HLSL bodies, bitwise query results and matched full-render pixels are required before the hosted
proof. Fresh all-three-backend correctness and measured runtime observations remain pending gates.
Controller ruling: implement the already-required complementary default/triangle pipeline isolation
now, independently of the still-open opt-in compile budget. This supersedes only the earlier ordering.
Default mode 0/1 fragments exclude triangle integer helpers at source construction. Mode 2 fragments
include the unchanged exact triangle helper and share one physical water-shading body. Focused and
clipmap triangle programs prepare lazily and independently, before recording, from validated submitted
payload kind and the same geometry classification used by drawing. Preparation has no frustum, so a
submitted plane later culled may honestly cause compilation. The four internal programs preserve the
vertex variants, bindings 10/11, 1088-byte payload, 1280-byte slot and public API.
The triangle fragment retains its scene-depth fetch with a source-local NaN invariant guard. Valid scene depth is
finite, so physical depth math is unchanged. Without that read, triangle specialization eliminates
bindings 4/5 and compacts HLSL registers against the fixed twelve-element resource layout.

Isolation is not compile-cost acceptance. Run 40114027066 produced real pixel DXBC with six successful
phases per program. Default pixel FXC 1203.9132 ms and total 2040.2890 ms pass. Triangle pixel FXC
7015.7335 ms exceeds 5000 ms, while total 7893.1533 ms meets 10000 ms. Its actual destination region
contains 176 instructions including 140 MOVs. The literal selects are a bounded structural candidate,
not a latency claim. Task 6B and Task 6C remain unaccepted until every existing gate passes.
After fresh parent review, run one candidate-only Windows default/triangle program matrix at the same
candidate SHA and harness, one focused pair per isolated job, each requiring pixel FXC <= 5000 ms and
six-phase total <= 10000 ms. Preserve explicit program identities and artifact hashes. Only after native
functional acceptance run fresh correctness verification on all three backends. Numerical, resource,
retirement, allocation, matched-image and runtime gates remain unchanged. No further arithmetic change,
new compiler option, relaxed budget or geometry/golden change is authorized in this scope.
GPU queries use rounded local-coordinate subtraction directly. The inverse extent is used only for
bin selection, whose additional rounding fits the existing outward acceleration-bin support.

Scene upload refuses a nonzero subnormal atlas scalar or a non-normal inverse extent as
`UnrepresentableSurface` before allocating a texture. Vulkan permits subnormal image-fetch results
to flush before a bitcast, and a subnormal inverse exceeds the current relative bin-mapping proof.
The immutable CPU field retains its broader source-bit contract. No upload rewrites source data.
Supporting those GPU inputs requires a bit-preserving texture representation or a revised capability
contract. A successful local backend run cannot substitute for that portability contract.

Native division is insufficient for the constructor's `2^-24` relative rounding allowance.
[D3D11 floating-point rules](https://learn.microsoft.com/en-us/windows/win32/direct3d11/floating-point-rules)
permit reciprocal-based division, while the
[Vulkan floating-point environment](https://docs.vulkan.org/spec/latest/appendices/spirvenv.html#spirvenv-precision-operation)
allows 2.5 ULP for `OpFDiv` and permits subnormal image-fetch results to flush. `precise` alone
does not supply the required division guarantee. The triangle integer implementation keeps the
existing arithmetic certificate unchanged.

The simple shader has a conservative fetch bound of `15 + 16*K`, up to 527 for K=32. Record actual
occupancy and normal verification timing on each backend. Optimize only from evidence while retaining
the same source authority. No stress or repeated-load run is authorized.

## Certified coverage

Certify each actual row-run drawable rectangle expanded by effective procedural displacement and
outward mapping error. Include complete triangles supporting that domain. Unrelated empty cells
elsewhere in a region need no invented bed. The new mode needs triangle support, not a bilinear
one-cell sampling halo. Source collection may retain a conservative halo, but coverage is required
where the displaced drawable can sample.

The constructor proves projected union containment and single-valuedness over every certificate.
Corner/center probes or summed area are insufficient because holes and overlaps can cancel. Use bounded
per-bin arrangement or an equivalently rigorous union test with robust predicates. Check positive-area
faces and closed boundary segments. A hole outside all certificates is allowed, one inside is refused.

Submission and preparation require the expanded request to fit entirely within one existing immutable
certificate. This conservative containment check allocates nothing. Larger swell invalidates coverage.
No fragment falls back to legacy depth, zero, deep water or a nearest triangle. Bound FFT is still
refused until it has a real certified displacement envelope.

## Actual mesh source and provenance

Build a compact immutable `TileGroundSurfaceSnapshot` from each completed mesh's indexed positions,
using `Indices32`. The same mesh instance is submitted for GPU upload. A null mesh is known empty,
distinct from an unavailable accepted snapshot. Full, cut, feather, coarse and transition geometry all
pass through this extraction. Do not reconstruct topology with a second mesher or force Full LOD.

`TileGroundSurfaceId` contains region, plane and monotonically unique generation. A snapshot retains
LOD, world transform, compact owned position/index data and its source witness. Keep scene handles
out of worker payloads. Mesh extraction alone cannot prove source availability because legacy meshing
can use lifted-zero fallback.

Audit actual geometry-height reads through the shared corner-cache read point using strict provenance.
Associate source witnesses with emitted tile/block index ranges, including feather children. Do not
reject an interior lake for an unrelated unavailable corner elsewhere in its region. Capture the
known-unloaded availability manifest before detaching a worker snapshot, then validate against live
state at admission. Preserve requested/base plane, negative availability and saturation semantics.

Capture body/rim inputs and padding before mesh work, then construct optics from completed source
snapshots. Topology, masks, authored shape/rotation, Bridge, relevant material kinds and feather settings
participate in source validity. Pure water colour changes still reuse optical bytes. HeightAt remains
bilinear and unchanged. TileWorld optics now follows rendered triangles instead of HeightAt interiors.

## Neighbor generations and publication

An optical domain crossing a region boundary uses that neighbor's accepted or same-transaction candidate
mesh snapshot and actual LOD. Loaded document heights are not an accepted rendered surface. Missing
neighbor snapshots, NoDraw holes and zero-underlay absence cannot be replaced with a manufactured Full
halo. A nonzero missing material still emits a valid bed, whose colour is irrelevant here.

When a source mesh changes, prepare every resident water field whose certified domain or source id
depends on it against one candidate surface table. Validate sources and padding, upload all replacement
fields and changed meshes, validate again, then swap the complete affected group before submission.
Any upload failure or stale witness rolls back the group. Serialize overlapping groups. No frame may
observe a new mesh with an old dependent field. Unrelated fields retain their handles.

An accepted coarse fallback may remain visible while Full is pending only with optics matching that
coarse snapshot. Stale results never become halo sources. Adjacent fields use identical source snapshots
in overlapping certified areas. Source arrival, unload, topology edits and LOD changes invalidate the
appropriate reverse dependencies, even when corner heights did not change.

Append failure kinds without renumbering old values: MissingSurfaceData, IncompleteSurfaceCoverage,
AmbiguousSurface, SurfaceCapacityExceeded and UnrepresentableSurface. Keep MissingHeightData for strict
source failures. Bounded diagnostic detail identifies the offending source/bin/domain. Invalid water
is withdrawn and diagnosed. Retain unrelated valid terrain. Do not publish old water against new ground.

All metadata is owned by resident generations, pending requests or current refusal dependencies.
Release epochs, reverse dependencies, refusal memo entries and source payloads when their last owner
goes away. Pending witnesses survive cleanup and cannot pass stale admission through an ABA reuse.
Use bounded deterministic lifecycle tests, not stress traversal. Report retained source-payload bytes.

## Limits and verification

The atlas supports every existing topology kind, not unbounded geometry density. Dense feather output
can exceed 32 candidates or atlas capacity. Refuse the complete request with diagnostics. Do not silently
drop candidates, downsample, coalesce almost-coplanar geometry or disable terrain detail. Exact bounded
partitioning is a future extension unless required to pass a concrete ordinary acceptance fixture.

Keep the original saddle geometry, camera, 0.20 m feather, one-live-field/upload assertions and 0.04 RGB
threshold. Add independent mesh-intersection oracles for both diagonals, absent-overlay forced rotation,
all corner-cut rotations, coarse interiors and transition fans. GPU proofs cover actual hit status,
shared edges/vertices/T junctions, final bins/texels, origin shifts, signed banks and representative depths.
Headless tests cover holes, overlaps, immutable input ownership, limits, exact byte accounting, rollback,
mixed resource kinds, neighbor generation changes and bounded metadata cleanup.

Existing legacy and bilinear images remain unchanged. Full hosted Metal, D3D11 and Vulkan verification,
final Release build/suite/format, documentation sweep and independent whole-branch review remain gates.
No new golden grid is needed. Task 8 and release authorization remain with their original owners.
