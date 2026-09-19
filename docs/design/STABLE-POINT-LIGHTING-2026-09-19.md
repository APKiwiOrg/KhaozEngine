# Stable clustered point lighting

Grimhollow's authored lighting grew beyond its camera-nearest submission budget. Rotating the camera
changed which lamps contributed to otherwise stationary surfaces. Raising the fixed count would move
the failure to a larger scene. The engine must accept the complete submitted light list.

## Receiver data

Use a growable read-only structured buffer, shared by every lit receiver family. Each 48-byte record
contains three vectors: position and radius, colour and intensity, and shadow slot with comparison biases.
Set 0 binding 1 is the common fragment binding. The frame header's light count describes the full list.
Keep the existing sixteen-entry frame arrays as compatibility padding in this change so their byte offsets
and the cascade block do not move. The lighting shader reads the structured records instead.

Grow capacity geometrically. Rebuild all resource sets that capture the old buffer through the renderer's
existing replacement transaction, and retire old resources safely. A capacity boundary must neither discard
lights nor leave a material pointing at an old buffer. Reject impossible allocation sizes explicitly.
Skip a light outside its squared radius before normalization, cel lighting, shadow sampling or specular work.

## Full clustered forward lighting

The renderer partitions the camera frustum into 16 by 9 screen tiles and 24 depth slices. Perspective
slices use logarithmic distance, while orthographic slices use linear distance, including valid negative
near planes. A CPU builder conservatively intersects light spheres with six inward-facing planes for each
cluster. This retains instancing and uses the same clusters for every receiver family.

The cluster buffer contains 17 four-uint records per cluster: a count and overflow header followed by room
for 64 stable submitted-light indices. It occupies about 918 KiB, allocated once per scene. The fragment
shader reads its cluster's indices into the growable point-light buffer. A cluster that overflows evaluates
the complete light list. Invalid or unsupported projection data takes the same correctness fallback.
Overflow costs performance, never missing illumination.

CPU construction and shader lookup use the same GPU-corrected view-projection matrix, so neither side
guesses the backend's fragment-coordinate Y convention. Two vectors appended to the frame uniform tail
carry near/far and slicing parameters plus camera forward. Existing uniform offsets remain unchanged.
Cluster storage is the common fragment buffer at set 0 binding 2. The GPU-skinned per-draw block moves to
binding 3, and rigid material bindings follow both shared buffers.

Build conservative geometry at frame recording time once the actual camera and render origin are known.
Reuse fixed storage without steady-frame allocations. Bounds include near-plane crossings and floating
origin shifts. Degenerate matrices or non-finite data mark the frame for full-list fallback. The shader
also takes that fallback for a fragment outside the resolved depth domain.

CPU assignment avoids a new compute producer pass and its synchronization obligations. The renderer still
performs full 3D clustered shading. GPU-driven assignment remains a possible optimization if measured CPU
assignment cost warrants it, using the same light records and correctness rules.

## Static shadows

Static requests have stable owner keys. Their residency must not depend on camera distance. Reserve enough
atlas rows for all requested static lights, independently of the smaller dynamic effect budget. Preserve
the existing frame-boundary allocation and cached-map warmup contract. Lower resolution before sacrificing
static rows when allocation fails, and keep allocation failure visible in the existing diagnostics.

The receiver slot vector is indexed by the full submitted light list. Its capacity cannot be the old
sixteen-light uniform limit. Default and High quality retain their existing face resolutions. With 38
placed lights, 384-pixel faces fit the existing six-column atlas within a 16384-pixel height. Large row
counts must resolve to a supported conservative extent or report allocation refusal without camera-based
selection. Low quality remains the explicit unshadowed mode.

## Consumer and verification

Grimhollow submits all resident prop lights in stable object-ID order, including the additional workshop
lamp at (103, 74). Remove its camera-nearest cutoff from both the live viewer and snapshot captures.
Keep fixture exclusion boxes, colours, authored radii and collision unchanged.

GPU readback compares a receiver lit by a late queued light against that light alone, with more than
sixteen other submissions, reordered queues and multiple camera poses. Cover buffer growth beyond 256 lights and every
receiver family using the shared lighting function. Static-shadow tests prove stable keyed residency and
capacity across camera changes. Cluster tests cover faces, edges, corners, near-plane crossings, clip-Y,
orthographic and perspective slicing, floating origins, malformed projections and dense-cluster overflow.
Use invariant image comparisons rather than new self-baked goldens.

Run Release build and headless tests, the affected Metal GPU tests with zero skipped, and the native backend
matrix before release. Verify the Grimhollow camera paths and cold-restored packages after adoption.
