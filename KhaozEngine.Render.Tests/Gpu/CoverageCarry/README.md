# Design B E1 carry cost floor

This is public F0 research only. Every output is an `uncertified cost floor`.
There is no certificate, coverage outcome, Scene3D integration or production draw path.
The fixture remains `coverage-typical-gpu/1` with 1,092 source vertices, 1,258 final
vertices, 2,332 triangles and two four-triangle inward supports. The signed-zero
variant changes only source colour and UV words for exactness tests. Timing always
uses the unchanged fixture.

## Pinned carry graph

`CoverageCarryAnchor` invokes the existing exact constructor once, at pose zero.
It owns copies of full, unweighted recession and active representative indices per
position group. GPU anchors have a separate read-only storage binding. Scratch,
current and previous surfaces never act as anchor storage.

`CoverageCarryReference.TryCarry` skins source vertices with the pinned binary32
skinning graph, then applies the frozen refinement to those posed sources. It uses
the existing pinned interpolation and normal-gather primitives. It never calls
conformation, face correction or the exact constructor during replay.

For each active group, use the anchor representative's current pre-carry position
and the current command frame's recession direction. At full transition weight,
apply the stored scalar directly. At other nonzero weights, multiply the current
weight by the stored scalar once. Subtract each resulting scalar-times-direction
component from the corresponding current position component. Every scalar
operation uses the inclusive B38 flush and separate RN32 operation from the kernel
contract. Corrected division and square root retain the existing residual graph.

Zero recession and either zero sign of transition weight copy each vertex's own
pre-carry position. Inactive groups also copy their own positions. Other active
aliases use the same canonical representative position. Protected originals retain
their own flushed source words. Generated positions retain the frozen recipe and
canonical-edge order. Numeric equality of before and after positions copies the
whole pre-carry vertex record, including zero signs. Moved vertices rebuild current
normals and tangents through the pinned ordered shading and vertex corner gathers.
Authored zero directions and tangent W keep their original words.

## Native shape

The four precise programs are source skinning, frozen refinement plus scalar
transport, complete current face basis, and ordered gathers plus publication.
Each generated vertex computes its canonical group recipe before its attributes
through the same scalar worker pump. Duplicate aliases repeat that small recipe
instead of introducing a dispatch boundary. X is flat across all live actors, with each actual topology
extent rounded to 64. Y is one. No serial actor loop exists in a shader. Source,
recipe, anchor and output records use the existing packed word ABI.

The basis program fuses preparation and division through one corrected scalar
worker pump. Carry has no geometric predicate jobs, so its worker excludes those
unused branches. Every physical program remains under the unchanged C8 caps.
The publication program copies old current words to previous, publishes all current
words, and computes tight bounds with one uniform 64-lane reduction per workgroup.
All lanes, including tails, execute every barrier. Only each workgroup's bound
summary uses global integer atomics. Per-frame uploads initialize bounds and errors
before any dispatch.

`CoverageCarryOrdinarySkin` is a compute control using the ordinary renderer's raw
four-bone matrix blend and native length/divide graph. It skins the same source
vertices with the same palettes. It includes the same frame uploads, tight bounds
and history publication. It has one dispatch and uses ordinary compilation. It is
a skinning storage floor, not a draw benchmark or an exact coverage substitute.

## Approximate native float experiment

`CoverageCarryNativeSkin`, `CoverageCarryNativeTransport`, `CoverageCarryNativeBasis`
and `CoverageCarryNativePublish` retain the same four stages, refinement recipes,
anchor transport and ordered gathers. They replace the entire exact arithmetic
prefix with native float operations, division and square root. They contain no
wide integer or corrected-rounding helpers, no inclusive flush emulation and no
`precise` declarations. `PreciseMath` is false on every backend. Performance
compilation may contract arithmetic. This is an approximate achievable floor for
the retained traversal, never an exact path or a geometric certificate.

Native stages compact unused bindings before compilation. Their explicit buffer
map drives both resource sets and the headless HLSL/MSL layout checks. Shader size
and corpus generation use the actual compilation options for every program,
including the ordinary control. No C8 cap is raised.

The native correctness fact compares both poses, zero and partial transition
weights, changed command frames and both source-word variants to the exact carry
reference. It reports maxima per actor for Euclidean XYZ position, normal and
tangent error, and error divided by the reference vector length. Zero reference
vectors with nonzero errors have undefined relative error and are counted
separately. Tangent W has a separate absolute error. Native history, tight bounds,
protected positions relative to native Before, and immutable anchors are checked.
There is no accepted error tolerance and no claim that these differences are safe.

## Diagnostic

`CoverageCarryProfileGpuTests` uses the existing `CoverageDispatchProfileFact`
opt-in and hardware capability checks. It uses 96 fixed conditioning frames, then 9 observed
frames on Metal and 32 on the other native backends, for each of 1 and 32 distinct
actors. Each carry path has an outer row and a separately conditioned stage row.
The ordinary control has an outer row. The diagnostic uses the existing ordinary hosted workflow
without an environment or workflow change. `KE_COVERAGE_CARRY_MEASURED_FRAMES`
can explicitly select 9 or 32. Metal accepts only 9.

One outer timestamp pair brackets frame and bound-reset uploads, every dispatch,
tight bounds and publication. This unchanged two-marker layout is the decision
number. A separate ten-marker layout adds a pair around skin, refine and transport,
basis and normals, and gather and publish. Stage intervals include binding and
dispatch hazards. Their enclosing outer interval also includes uploads and marker
overhead. Stage medians are not summed to replace an outer measurement.

Three retained slots drain after each batch, outside
the timed path. There is no readback or completion wait inside a frame. Reports
retain every raw sample, min, median, p95, p99 and maximum, upload bytes, retained
GPU storage, warm managed allocations through submission, CPU encoding time and
separate CPU submission time. Sampled outputs are
checked after the entire observed block. Offline reference computation is kept
out of conditioning and measurement. First-half and second-half means retain
chronological order and include every measured sample, including an odd tail.
They expose remaining clock drift rather than trimming it. Each layout conditions
independently. Conditioning is fixed, not a proof of convergence. The ordinary
control shares the exact carry harness's retained allocation. Native float rows
also retain per-actor error maxima over each row's final surface. The separate
native correctness fact retains errors across all its replay frames and variants.

Shared topology, compiled programs and exact anchors are resident before timing.
Their first admission, validity, atlas selection, certification, exact refresh and
drawing are excluded. This optimistic E1 floor does not satisfy those later gates.
Metal numbers indicate shape only. Root owns all-backend native exactness and the
T4 decision. No GPU timing fact runs during ordinary CI without `KE_GPU_TESTS=1`.

Results go to `KE_COVERAGE_PROFILE_RESULTS/coverage-carry-profile.json` and the full
`COVERAGE_CARRY_PROFILE_JSON=` test-output line. The ordinary hosted bench artifact
therefore retains the machine-readable evidence without a workflow change.
