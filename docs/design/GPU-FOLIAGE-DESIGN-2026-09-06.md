# Persistent GPU foliage

Status: implemented for 18.29.0. Issue: https://github.com/APKiwiOrg/KhaozEngine/issues/841.

The immediate trigger was Grimhollow's dense grass rebuilding ordinary instance lists every frame.
Enabling combined MSAA and FXAA also regressed the owner's forest view. CPU encoding benchmarks had not
measured that complete frame cost. The game returns to FXAA independently of this engine change.

Retain authored meshes and placements in immutable GPU buffers. Spatial patches sorted by stable rank
let the CPU select conservative prefixes without visiting every blade. A vertex shader performs exact
fading, coherent wind and local actor displacement. Indexed instancing works on the existing Metal,
Vulkan and D3D11 backends without making mesh shaders or compute a prerequisite.

Use mesh-relative height so one shader serves short and tall grass. Quadratic weighting anchors roots.
Horizontal bending lowers tips to limit apparent stretching. Bounds account for both displacement axes
and arbitrary accepted affine transforms. Fading contracts toward the authored minimum Y, including
models with offset roots. Frustum culling happens after the final render size and camera are known.

The existing lit fragment shader preserves authored colours and alpha masks. Dynamic shadow casting is
excluded from this retained path, and previous cover policies remain available. Actor inputs are a
bounded cosmetic list with 3D falloff, separate from collision and world state. Separate uniform slots
keep multiple submissions independent on backends that snapshot uniform memory at submission.

Tests compare rendered images and persistent upload counters, with negative controls for fixed roots,
offset roots, later camera changes and separate actor floors. A matched offscreen Hollowmere benchmark
records queue time, command encoding and serialized GPU completion separately. Those measurements do
not establish windowed FPS or display shimmer, which remain a human playtest.

The final matched forest benchmark used the same authored world, a 64 m cover radius, FXAA and a
1600 x 900 internal target presented to an offscreen 3366 x 2058 surface on an M2 Max. Two alternating
CPU runs measured median queue plus encoding time of 4.64 and 4.60 ms. Two retained GPU runs with wind
and selected tall grass measured 1.88 and 1.89 ms. Waiting for GPU completion after every frame gave
medians of 8.18 and 7.93 ms for the CPU path, and 5.37 and 5.11 ms for retained foliage. Warmed foliage
instance uploads were zero, with 2,816 uniform bytes per frame. These are serialized offscreen frames,
not GPU timestamps or windowed FPS. Other processes on the machine were not controlled.

Shipped API reference lives in the package READMEs and docs/USING-KHAOZENGINE.md.
