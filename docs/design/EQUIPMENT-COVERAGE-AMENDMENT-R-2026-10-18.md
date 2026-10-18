# Equipment coverage amendment R: GPU compute conformation with an offline exact oracle

Status: owner approved on 2026-10-18, including the model, the GPU approach, the four design sections
and this written amendment. It replaces the CPU-approach text first committed as `7b786f6cd`.
Implementation follows only after the plans are approved. Owner ruling NC2, also chosen on 2026-10-18,
replaces NC1's device tolerance with device exactness.
Measurements and any release remain unauthorized until their own approvals.
It amends sections 7, 8, 10, 11 and 12 of the [approved design](EQUIPMENT-COVERAGE-DESIGN-2026-10-16.md)
and replaces the actual render-frame error contract of the
[construction and render-error amendment](EQUIPMENT-COVERAGE-AMENDMENT-2026-10-16.md).
The engine work is tracked by [#1418](https://github.com/APKiwiOrg/KhaozEngine/issues/1418) and the
consumer work by [Grimhollow #547](https://github.com/APKiwiOrg/Grimhollow/issues/547).

## Outcome and evidence

The outcome is unchanged. A rigid close-fitting chestplate sits on the skinned body in full motion,
with no host surface breaking through it, using the approved bounded collar conformation.

The approved model proved every submitted pose at runtime on the CPU. It cannot meet its own budget.
The budget allows 2 ms added CPU preparation for 32 typical actors, or 62.5 microseconds per actor.
The [conditioned pilot after cost unit 4](../superpowers/plans/proofs/2026-10-16-equipment-coverage-cost.md)
measured 26.292074 ms added per actor-frame on a hosted CPU: 12.461836 ms exact construction and
13.776059 ms full late validation. One actor costs more than a 60 Hz frame. No constant-factor unit can
close a gap of this size, and the approved plan returns a budget miss to design review.

The review considered compiled pose-cell proofs, certificate reuse, pose envelopes, actor scheduling,
conservative proxies, GPU certification, exact memoization and a CPU float runtime. The scoring and the
owner's choices are recorded in the ledger research and root assessment for this program.

## Owner rulings

- **R1, model.** Runtime runs a float version of the approved conformation with a guard. The exact
  certifier becomes an offline oracle. This reverses the approved rule that every submitted pose is
  proved at runtime (design section 7).
- **R2, oracle domain.** For each armour asset, the oracle certifies every authored frame of every clip
  worn with it and a declared blend grid of the game's real layering. Any failure fails the game's CI and
  blocks that content.
- **R3, GPU compute.** Conformation runs in GPU compute on Metal, Direct3D11 and Vulkan, batched across
  actors, with a GPU-side final surface and history. This replaces the CPU float runtime that was briefly
  approved. Uncovered actors keep today's vertex-shader skinning.
- **R4, GPU timers in scope.** GPU timestamp queries are added to the engine GPU layer in this program,
  so the 15 percent GPU gate can be measured.
- **R5, design sections.** The architecture, GPU layer, reference and oracle, and evidence and delivery
  sections below were each approved as presented, including parallel parts A and B.
- **NC1, numerical tolerance.** Device tolerance is a per-sample interval enclosure, not a fixed bound,
  and construction reserves `MarginFraction x Clearance` for it. Chosen over a conditioned asset domain
  and bit-exact GPU arithmetic after the part B planner showed a fixed bound cannot be derived.
- **NC2, device exactness, supersedes NC1.** The NC1 enclosure was built and failed on real fixtures:
  intervals lose correlations and branch joins hull covered and uncovered outcomes, so one vertex the
  reference moved by 4e-9 m received a 2.8 cm box. Every coverage kernel now reproduces the reference bit
  for bit, and the device tolerance is zero. Chosen over measured tolerance and a correlated enclosure.

## What a covered actor means after this amendment

A covered actor is one whose CPU-side admission passed (identity, command frames, origin magnitude and
completeness) and whose GPU conformation produced a finite surface without any guard refusal. If the GPU
guard refuses, the same buffer receives the actor's uncovered current pose, so every pass draws the
explicit fallback without a CPU wait. No submitted surface carries a runtime proof.

The no-breakthrough guarantee comes from the oracle. It is proved exactly for every sampled pose in R2,
on every device that meets the exactness contract in the reference and oracle section. It is not proved between samples, for
blends outside the declared grid, or for poses from layers the game did not declare. Those poses still run
the same construction and guard, so a gross failure still falls back, but a subtle breakthrough there is
possible and unproved.

## Part A: engine GPU layer

New `KhaozEngine.Gpu` capabilities on Metal, Direct3D11 and Vulkan. Each is general, documented in the
engine guide, and tested on all three CI devices.

1. **Ordering within a command list.** The existing ordering contract, which covers storage-texture
   handoff, extends to buffers. A dispatch that writes a read-write buffer is ordered before every later
   dispatch or draw in the same command list that reads it. Backends insert the barrier themselves:
   explicit pipeline barriers on Vulkan, hazard tracking on Metal and Direct3D11. The public seam keeps
   no barrier call. Dependent dispatches no longer need `Submit` and `WaitForIdle` between them.
2. **Storage reads in vertex shaders.** Resource sets may bind a read-only storage buffer to the vertex
   stage, so draws read positions and normals by vertex index. This avoids binding a structured buffer as
   a vertex buffer, which Direct3D11 forbids.
3. **Integer atomics.** `atomicOr` and `atomicMax` on `uint` storage buffers, cross-compiled to the
   HLSL interlocked and MSL atomic forms, verified through FXC and the Metal compiler. Float values are
   never accumulated atomically.
4. **Async readback.** A copy into a readback buffer is recorded in the frame. A non-blocking `TryRead`
   returns false until the GPU finished. A fixed ring of slots means it never stalls.
5. **GPU timestamps.** Fixed-size timestamp pools, `WriteTimestamp` at pass boundaries, a non-blocking
   resolve and the tick period. `SupportsTimestamps` is probed per device. Where it is false the GPU gate is
   reported as unmeasurable, never estimated.

## Part B: CPU float reference and the exact oracle

1. **Reference constructor.** A C# implementation of exactly the algorithm the kernels run, with the
   same formulas, operation order and refusal codes. It is test and oracle infrastructure only, never a
   runtime path.
2. **`CoverageOracle`.** Public and headless, with no `Scene3D` and no GPU. It is built for one
   `CoverageRefinement` and its inverse binds and owns its scratch. `TryCheck(palette, model, commands,
   out certificate, out failure)` skins and constructs with the reference, then runs the exact
   `CoverageCertification.ValidateGeometry` at the approved clearance. A failure states its stage:
   construction with its guard code, device exactness with the first subnormal operation, or
   certification with its predicate code, command and count. Warm checks allocate no managed memory.
3. **Device exactness (owner ruling NC2).** Every coverage kernel computes the same binary32 bits as the
   reference. Add, subtract and multiply are correctly rounded on Vulkan, on Metal with fast maths off and
   on Direct3D11, and kernels compile in precise mode (GLSL `precise`, Metal fast maths off for these
   kernels, FXC IEEE strictness) so nothing contracts or reorders. Division and square root are correctly
   rounded in the reference. Where a backend's native operation is not, the kernel corrects its result to
   the correctly rounded value with an exact residual check built from correctly rounded multiply and add.
   Exact predicates follow item 6. Every value is then a function of the uploaded binary32 inputs and the
   pinned operation order, so a conforming device reproduces the reference bit for bit. Subnormals are
   the one place backends may differ, because Direct3D11 flushes them and Vulkan and Metal may. The oracle
   therefore fails a sample with a device-exactness code if any reference operand or result in it is
   nonzero with magnitude at most the smallest normal, `2^-126`, so every certified sample is identical on
   every conforming device. Construction still
   reserves `MarginFraction x Clearance` in the receding direction, as margin against a driver that breaks
   its precision rules. Native equivalence tests on all three CI backends assert bit equality on every
   fixture. A mismatch fails, and nothing is loosened to pass.
4. **Real-hardware verification.** CI GPUs are software or virtual. A dev-only verification mode, off by
   default, reads the local player's GPU surface back asynchronously every N frames, reconstructs the
   reference for that pose and logs the first vertex whose bits differ, with the pose and the difference.
   Playtests on real GPUs therefore check driver conformance.
5. **Consumer contract.** Each game owns its sampler and grid. Grimhollow adds a headless blocking CI test
   over every armour asset and its host: every authored frame of every clip at the clip's own sample
   rate, and a blend grid over travel weight, travel-clip pairs, and turn and jump crossfades at weights
   0, 0.25, 0.5, 0.75 and 1, with clip pairs sampled together at each authored frame of normalised phase.
   Poses come from the game's actual locomotion sampler. A failure fails CI and reports asset, clip,
   frame, weights, stage, code and command.
6. **Exact predicates, float constructions (root ruling B27).** Every 2D edge determinant whose exact-path
   counterpart drives a decision or a construction (overlay inside and side tests, triangle areas, support
   projection and barycentric interpolation) returns two values computed from its binary32 inputs: the
   exact sign, and the exact value rounded once to binary32, to nearest with ties to even. Control flow
   follows the exact overlay on those signs. Crossing parameters, points and heights stay binary32 with a
   pinned operation order. Plain binary32 determinants rounded near-degenerate signs to zero or the wrong
   sign, and bounds heuristics on top of them traded a false contact for a lost crossing. Both values
   are facts of the inputs, not of the method, so a kernel matches the reference bit for bit with any
   exact device method. Part C proves its method on all three backends: float error-free expansions only
   where correctly rounded add, subtract and multiply without contraction are verified, otherwise 32-bit
   integer multiword arithmetic. A crossing parameter divides the two exact side values after scaling
   both by the common power of two that puts the larger magnitude in [1, 2). The result is bit-identical
   whenever the values are normal, and a device that flushes subnormals never divides zero by zero (root
   ruling B30).

## Part C: GPU coverage path in Render3D

### Static data, once per host and equipment

At conformer creation and equip, upload immutable data with preallocated capacity:

- Host topology: refined-vertex records (region, strength, position group, core flag), the refinement
  recipe, triangles, and adjacency in compact sparse-row form (position group to incident core faces,
  vertex to incident faces). Kernels gather and never scatter floats.
- Each command's actual inward support sheet in coverage space, with a uniform grid over its triangles
  for height and boundary lookups.
- Per actor: source-posed scratch, group scratch and a double-buffered final surface of positions and
  normals.

Topology and support data are shared by every actor that uses them.

### Per frame on the CPU

Staging keeps its entry and identity checks and composes each command frame (host-to-coverage
transform, recess direction, clearance, caps and collar width) into a small per-actor uniform. The bone
palette for a covered actor is composed on the CPU by the pinned scalar reference routine, the same one
the oracle uses, so the uploaded bits do not depend on the machine's vector or fused instructions (root
ruling B31). The CPU refusals are identity, capacity, command frame admission and the
origin-magnitude guard.

**Origin magnitude.** Staging bounds the actor's coordinate magnitude relative to the latched frame render
origin from its model translation and host bounds. If a conservative float32 rounding bound for that
magnitude exceeds `Clearance x MarginFraction` for any command, the actor is uncovered for the frame.
`Scene3D.Begin` latches the origin before it opens the coverage arena that staging requires.

### Per frame on the GPU

One batched kernel chain over all covered actors:

1. Skin source vertices with the uploaded palette.
2. Refine to the refined surface.
3. Conform one thread per position group toward the actual inward sheet with the collar smoothstep,
   reserving `MarginFraction x Clearance` beyond the clearance in the receding direction. That reserve
   is margin against a driver that breaks its precision rules. A conforming device reproduces the
   reference exactly.
4. Core-face correction gathered per group.
5. Transition weighting.
6. Normals gathered per vertex. A folded patch is a refusal.
7. Select. Any refusal sets the actor's flag with one integer atomic OR. A refused actor's final buffer
   receives its uncovered current pose, a covered actor's buffer receives the conformed surface.

The guard is the approved refusal set evaluated in the kernels: cap contact, core escape, unsupported
support, recession budget, non-finite output, folded patch and the core-correction refusals, with their
existing failure codes.

### Consumption and history

Main, motion, outline, cascade and point-face passes read the actor's current final buffer through
vertex pulling. The motion pass also reads the previous buffer. Previous actually presented FINAL
vertices, current-origin rebasing, per-view history identity and local transition rejection move to the
double-buffered GPU surface. When history is invalid, the select kernel seeds the previous buffer from the
current surface, matching the existing current-position seeding rule. Equipment draws are unchanged.

**CPU-side bounds.** The CPU never reads the GPU surface back for drawing decisions. Culling, shadow
caster classification, the coverage sphere and temporal rejection bounds use the existing skinned-actor
bounds expanded by each command's `MaxWorldRecession`. Conformation only recedes the host surface
within that budget, so these bounds are conservative for both the conformed and the fallback surface.

**No compute.** If `SupportsCompute` is false or the coverage kernels fail to build, every actor is
uncovered and draws through today's vertex-shader skinning. This is reported once as a coverage
capability failure, never retried per frame.

### Outcomes

At resolution, an actor that passed CPU admission and completeness is reported as dispatched in the same
frame. Its GPU result (covered, or refused with code and command) arrives through the async readback ring
a few frames later, keyed by frame and actor. Diagnostics and tests read it. Nothing in drawing waits on it.

### Kept and removed

Kept: the approved conformation algorithm and look, region metadata, actual inner sheet, collar weights,
recession budget, fixed topology, protected uncommanded vertices, one surface for all passes, frozen
per-frame decisions, re-staging for a new view, the explicit uncovered fallback and zero warm managed
allocation.

Removed: the CPU exact construction and `TryConstructCandidate`, render pass capture for coverage, the
pass list, `CoverageCertification.Validate` at resolution, the render stamp and drawable certificate, and
the render-arithmetic certifier (`CoverageRenderError`, `CoverageRenderArithmetic`,
`CoverageRenderContext`). Nothing in this feature is released, so no published API compatibility is
affected.

## Evidence

1. **Exact reference first.** Before the exact constructor is deleted, its outputs on the typical and
   limit fixtures, seams, transition weights 0, 0.5 and 1, and refinement levels 2 to 4 are captured and
   committed.
2. **Reference fidelity.** Run at the capture's own effective clearance, so the comparison measures float
   construction differences only, each reference position stays within `Clearance x MarginFraction` of the
   exact capture. A core-corrected group that exceeds it is investigated, never accepted by widening the
   bound. The reserve's deliberate extra recession is checked separately against its mapped size.
   Refusal codes match on the existing construction negatives.
3. **Oracle.** Every fixture's reference output passes the oracle. An over-tight clearance, a support
   escape and a recession-budget excess each fail with the correct stage and code.
4. **Part A capabilities.** A backend test per capability on all three CI devices. Vulkan synchronisation
   validation stays clean.
5. **Device equivalence.** Every GPU output vertex equals its reference vertex bit for bit on every
   fixture on all three backends.
6. **Scene behaviour.** Every pass reads the same surface, using the existing ghost envelopes, masks and
   negative controls adapted to the GPU surface. A forced GPU refusal draws the uncovered pose in every
   pass with no CPU wait. Double-buffered history transitions and rebasing hold. A batched 32-actor
   correctness case passes. CPU refusals, including the origin guard, have positive and negative cases.
   Tests that asserted render-certification refusals are rewritten or removed.
7. **Allocation.** Warm staging, dispatch, resolution and oracle checks allocate zero managed bytes.
8. **Goldens.** Any of the 18 goldens no longer at worst 0 receives a side-by-side inspection, and its
   reference is refreshed only after root review with the delta recorded. No tolerance is loosened.

## Gates and delivery order

The 2 ms for 32 typical actors CPU gate, the 15 percent native GPU gate and the zero-allocation gate are
unchanged. The CPU gate now measures command frames, uploads and encoding. The GPU gate is measured with
the part A timestamps on real GPU hardware. Every measurement needs fresh owner approval. Hosted CI GPUs
are software or virtual, so the place GPU timing runs is decided with the owner at that approval.

1. Owner approval of this amendment, then a plan per part.
2. Parts A and B in parallel, in separate worktrees branched from `feature/equipment-coverage`, one
   writer each with fresh review per task. They touch disjoint code.
3. Part C after both, including the removals.
4. Native hosted verification, then the approved pilots.
5. Task 10: merge current main, full verification, documentation and API sweep, whole-branch review.
   The deferred Task 5 minors are settled there. Q2 and Q5 become moot once late refusal and pass capture
   are removed. Q4 and Q6 are reviewed.
6. Exact engine release with owner tag approval through the engine-rooted release handoff.
7. Part D, Grimhollow adoption: the blocking oracle CI test, armour fit and catalog change, owner
   full-motion look, a playtest with the dev verification mode on, then the final playtest.

## Superseded text

- Design section 7: runtime checks no longer apply to every submitted pose. The offline suite becomes the
  R2 oracle domain and certifies exactly.
- Design section 8: the public surface adds `CoverageOracle` and the asynchronous GPU result, and loses
  the render-context evaluators.
- Design section 10 and the construction and render-error amendment: the CPU-prepared final stream
  becomes the GPU double-buffered surface. The render-error context, pass capture before the first covered
  draw and the derived `WorldErrorBound` are replaced by the origin-magnitude guard.
- Design section 11: the budgets are unchanged. Their measured workload is the GPU path, with GPU time
  from the new timestamps.
- Design section 12: the synthetic oracle becomes a public engine API used by consumers.
- Plan amendment Task 5 and cost unit 4 text: staging no longer awaits late certification. The Task 5
  late-render-refusal, all-pass-matrix, stale-context and prepared-bounds tests are rewritten or removed
  under evidence item 6.
