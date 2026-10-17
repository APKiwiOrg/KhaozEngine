# Equipment coverage Part B: CPU float reference and exact oracle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the exact construction evidence and provide a headless float reference and exact coverage oracle for asset CI and the Part C kernels.

**Architecture:** Keep the current exact constructor and capture its results before introducing a separate reference implementation. The reference owns immutable gather tables and bounded scratch, while the public oracle uses the staging skinning math and the existing exact geometric predicates. A separate interval evaluator encloses the same device operations. The oracle rejects an excessive per-sample tolerance before exact certification. NC1 is resolved by the owner contract below, with the named specification-verification items.

**Tech Stack:** C# on .NET 10, System.Numerics, xUnit, existing expansion and interval geometry predicates, no new dependencies.

**Spec:** docs/design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md

## Global Constraints

- "It is test and oracle infrastructure only, never a runtime path."
- "Kernels gather and never scatter floats."
- "Warm checks allocate no managed memory."
- "Each reference position stays within `Clearance x MarginFraction` of the exact capture."
- "A core-corrected group that exceeds it is investigated, never accepted by widening the bound."
- "Refusal codes match on the existing construction negatives."
- "A breach fails, and no bound is widened to pass."
- Reserve `delta = Clearance * .25f` in construction. Use per-sample interval enclosures, never fixed rho or world epsilon. Reject unbounded enclosures and `epsilon > .25 * (double)Clearance` before certification.
- "Measurements and any release remain unauthorized until their own approvals."
- "The 2 ms for 32 typical actors CPU gate, the 15 percent native GPU gate and the zero-allocation gate are unchanged."
- `CoverageFrames.MarginFraction = .25`, `CoverageScratch.MaxCommands = 4`, support limits are 1,024 inward triangles and 256 boundary edges, and host limits are 65,536 vertices and 65,536 triangles.
- "Warnings are errors. Fix them at the source. Do not add blanket suppressions or disable the rule."
- "New behavior gets a headless test in the matching area test project."
- "Commit subjects use `area(scope): summary`."
- Implement later in `/Users/antonio/KhaozEngine/.worktrees/ec-oracle`, branch `feature/equipment-coverage-oracle`, created from `feature/equipment-coverage` containing `c525aa2bb52a75db2b4b9c0d17a31638b7938699`.
- Part A owns `KhaozEngine.Gpu*`. This plan changes none of those projects, no shaders, no Scene3D staging or drawing, and deletes no exact constructor or render certifier.
- Do not merge main, bump versions, pack, release, run pilots, launch clients or integrate either parallel branch in these tasks. The root orchestrator owns the later batch finish.

## Review Focus

1. Skinny support queries, near-zero correction divisors, subnormal operands and overflow must widen or become unbounded rather than acquire a coordinate-only error proof. Tasks 2 and 6 test amplification, flushing, domains and CPU containment.
2. Seams in opposite triangle order retain welded position bits, smooth normal gathers and separate hard-normal/tangent neighborhoods. Tasks 1, 4 and 5 test these, and Task 6 includes their interval outputs.
3. Zero transition still checks the full target and preserves every original byte when valid. Tasks 3, 4 and 8 test this. Task 6 encloses branch alternatives, including possible uncovered selection.
4. Refusal then success, reordered commands or a numerically worse equipment transform must not reuse candidates, bounds or certificates. Tasks 6 and 8 test reset, equipment contributions and deterministic tolerance attribution.
5. The reserve consumes the fidelity budget, never enlarges it. Task 5 retains exact quarter-clearance fidelity, Task 7 proves padding only strengthens separation, and Task 8 tests epsilon at and above delta and the new code.

## Resolved numerical contract NC1

The owner resolution in Part B item 3, Part C step 3 and evidence item 5 replaces fixed rho and fixed epsilon. Task 1 is unchanged. No constructor rounding bias, operation-count gamma bound or fixed world tolerance survives.

### Reserve and fidelity

Use scalar binary32 in this order, with Task 2's slope and inverse-map ordering:

```text
delta = Clearance * .25f
ReservedClearance = Clearance + delta
ClearanceCoverage = (ReservedClearance * sqrt(1f + S*S)) * SpectralUpper(inverse(C))
ActualClearanceCoverage = (ReservedClearance * sqrt(1f + S*S)) * SpectralUpper(inverse(E) * P)
target = sheetHeight - ClearanceCoverage
```

Core correction also uses the reserved `ActualClearanceCoverage`. Authored `Clearance`, cap margins and recession caps do not change. Full-target guards precede transition. There is no extra bias on point recession, correction or their sum. Rounding and final float32 stores belong to the enclosure, not a guessed constructor conversion allowance.

For the same active branch before correction, write `M = sqrt(1+S*S)*SpectralUpper(inverse(C))`, `W = length(A.linear(D))` and `alpha = strength*spatial*TransitionWeight`. Ignoring arithmetic differences only for this comparison, the pointwise difference from capture is `alpha*abs(delta*M-exactSlackCoverage)*W`. The capture's world slack is between zero and delta. The new reserve can therefore spend the entire fidelity budget even in a unit flat frame. Slope and anisotropic transforms can give `M*W > 1`. Float interpolation, branch changes and core correction add differences. This is not a proof of fidelity or spare roundoff headroom.

**Root ruling: compare like with like.** Fidelity measures float-versus-exact construction differences, so it runs the reference at the capture's own effective clearance. An internal test seam `CoverageReferenceConstructor.TryConstruct(..., ReadOnlySpan<double> clearanceCoverageOverride)` replaces each active slot's target clearance with `capture.ClearanceCoverage[slot] + capture.SlackCoverage[slot]`, the exact target depth, and changes nothing else. Production and oracle calls pass an empty span and use the reserve. In that like-for-like mode the assertion is `worldDistance(reference,frozenExact) <= .25*(double)Clearance` for every generated position and every corrected group. Report fixture, group, transition, capture slack and correction on failure. Do not add epsilon, float fudge or a normal tolerance, recapture or omit a row. A miss returns to root with evidence.

The reserve's deliberate extra recession is checked separately. For every conform-only group, `worldDistance(reserveRun,likeForLikeRun) <= up(alpha*abs(delta*M-capture.SlackCoverage[slot])*W)` with `M` and `W` from the same frame, plus the like-for-like bound. Core-corrected groups report this difference without asserting it, because correction depends nonlinearly on the target. This is the owner-accepted implication that skin may sit up to a quarter clearance deeper, measured perpendicular to the sheet.

### Precision sources and k_op table

- [Vulkan specification, Vulkan Environment for SPIR-V](https://docs.vulkan.org/spec/latest/appendices/spirvenv.html), **Precision and Operation of SPIR-V Instructions**, **Evaluation of Expressions**, **Precision of Individual Operations**, **ULP**, **Inherited From**, Table 3 **Precision of Core SPIR-V Instructions** and Table 4 **Precision of GLSL.std.450 Instructions**.
- [Metal Shading Language Specification 4.1, 2026-06-04](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf), **8.1 INF, NaN, and Denormalized Numbers**, **8.2 Rounding Mode**, **8.4 ULPs and Relative Error**, Table 8.1 **Accuracy of single-precision floating-point operations and functions** (pages 368 to 370), **8.5 Edge Case Behavior in Flush to Zero Mode** and **8.6 Conversion Rules for Floating-Point and Integer Types**. Fast maths is off, never use Table 8.2.
- [Direct3D 11.3 Functional Specification](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm), **3.1.2 Term: Unit-Last-Place (ULP)**, **3.1.3.1 Partial Listing of Honored IEEE-754 Rules** and **3.1.3.2 Complete Listing of Deviations or Additional Requirements vs. IEEE-754**. **22.10.18 rcp** and **22.10.19 rsq** are separately relaxed instructions.

| Operation | Vulkan | Metal with fast maths off | D3D11.3 | Chosen binary32 k_op |
| --- | --- | --- | --- | --- |
| Add, subtract, multiply | Correctly rounded | Correctly rounded | .5 ULP | `1` each |
| Divide | 2.5 ULP for divisor magnitude `[2^-126,2^126]` | Correctly rounded | Reciprocal 1 ULP then multiply .5 ULP, or better | `4`, plus expansion hull below |
| Square root | Inherited from `1/inversesqrt(x)`, inverse square root 2 ULP | Correctly rounded | 1 ULP | `16` on the finite normal intermediate domain, plus inherited hull below |
| Float32 final store/conversion | Correctly rounded conversion, bit-preserving store | Supported rounding for conversion, bit-preserving store | Bit-preserving float32 storage | `1` conservatively per final store |
| Abs, negate, min, max, clamp, compare, select | Exact finite result | Exact finite selection/sign operations | Exact finite selection with flushing | `0`, operand flushing and branch joins still apply |

The four-ULP quotient is conservative for strict D3D division with normal intermediates. Reciprocal relative error is at most `2^-23`, multiply at most `2^-24`, so the combined relative error is at most `3*2^-24+2^-47`, less than four output ULP. The square-root `16` is a conservative derivation, not a Vulkan table entry. Inverse-square-root relative error is at most `2^-22`. Combining it with the chosen four-ULP division gives relative error at most `(2^-22+2^-21)/(1-2^-22)`, less than sixteen output ULP. Test both expanded intervals and binade boundaries. These derivations do not replace intermediate flush or exceptional-domain handling.

**RV1 resolved by root.** The Vulkan Environment for SPIR-V states that positive and negative zeros are generated as IEEE 754 dictates for the encoding, that only results not mathematically defined are poison, and that denormalized inputs and inherited-operation results may be flushed to zero. `sqrt(+0)` is defined and equals `+0`. So for a square-root input interval `[l,h]` with `l >= 0` after operand flushing, the device result lies in `[lo,up(sqrt(h)*(1+16*2^-23))]`, where `lo = 0` when `l` is zero or a possible subnormal and otherwise `down(sqrt(l)*(1-16*2^-23))`, hulled with the inherited expansion on the normal part of the interval. Negative input stays unbounded. Add `Square_root_enclosure_at_zero_and_subnormals_is_finite` to Task 2 with inputs `[0,0]`, `[0,2^-140]` and `[0,1]`.

**Root verification RV2:** Part C must verify that strict `/` and `sqrt` lower to the strict D3D requirements and preserve the pinned expression order on every backend. The separately relaxed `rcp`/`rsq` relative error is `2^-21`, not one ULP. No unconfirmed finite bound for that lowering is chosen here. Part B writes no compiler or GPU code. Root must confirm the finite-domain four/sixteen-ULP derivations before treating them as an approved device contract, or retain the explicit expansion hulls as the conservative larger bound.

### Outward interval evaluation

Use `CoverageFloatInterval(Min,Max)` with binary64 endpoints and unbounded `[-Infinity,+Infinity]`. Exact constants are `MinNormal32=2^-126`, `MinSubnormal32=2^-149` and `MaxDivisor32=2^126`. Device-read inputs enter as point intervals of their uploaded binary32 values: source attributes/weights, CPU-composed palette, support tables, region data, model/equipment matrices and emitted command uniforms. Do not add device error to CPU frame composition again.

Round each binary64 endpoint operation outward with `Math.BitDecrement` and `Math.BitIncrement`, including extrema, error additions and radii. For outward real result `R=[l,h]`, set `m=max(abs(l),abs(h))`, `ulp32(m)=2^max(-149,floor(log2(m))-23)` for positive finite m and `ulp32(0)=2^-149`. Derive the exponent from bits or `Math.ILogB`, never a rounded logarithm. Each operation returns `[down(l-e),up(h+e)]`, where `e=up(k_op*ulp32(m)+2^-126)`. A final float32 store is an operation. Pure copies stay exact. [Math.BitDecrement](https://learn.microsoft.com/en-us/dotnet/api/system.math.bitdecrement?view=net-10.0) and [Math.BitIncrement](https://learn.microsoft.com/en-us/dotnet/api/system.math.bitincrement?view=net-10.0) return binary64 neighbors.

Before arithmetic or arithmetic comparisons, include zero in an operand interval containing possible binary32 subnormals. Keep both preserved and flushed outcomes, with either zero sign where needed. Output-only `2^-126` cannot cover `subnormal*largeNormal` input flush.

The complete operation set is add, subtract, multiply, divide, square root, float32 store, abs, negate, min, max, clamp, compare and hull/select. Expand dot, cross, affine transforms, norm, smoothstep, height and clipping into this set in reference order. `Divide(a,b)` hulls its direct four-ULP result with the D3D expansion `Multiply(a,Reciprocal1(b))`. `Reciprocal1` widens `1/b` by one ULP and has its own input/output flush. Square root also encloses `Divide(1,InverseSqrt2(x))`, with two ULP on inverse square root and each internal operation's flush, then symmetrizes the error about the outward ideal square-root interval as **Inherited From** requires. Hull that with the direct sixteen-ULP result. These are enclosure helpers, never reciprocal substitutions in the float constructor.

A divisor interval containing zero or any magnitude outside `[2^-126,2^126]`, negative square-root input, possible non-finite intermediate or finite-binary32 overflow yields unbounded. So does an internal expansion's precondition failure. Never clamp it finite or retain only the CPU result. Only a proven nonnegative range permits clamping an outward square's lower endpoint to zero.

If a comparison cannot select one branch, join both reachable results. Height queries join all possibly selected source-ordered triangles and possible boundary/miss results. Boundary ties join possible winners, overlays include every possibly accepted corner/crossing. Query every grid cell an interval AABB intersects, not the CPU point's cell. Include the uncovered current pose whenever any device guard, including a normal guard, might refuse. Join immediately in fixed scratch without path enumeration or allocated branch histories. A possible invalid arithmetic operation makes the enclosure unbounded. Replaying only the CPU-selected branch is unsound.

### Epsilon composition and refusal

Enclose each host's final stored model position and float device world transform. Enclose its exact-certifier center by outward binary64 evaluation of the stored reference position and uploaded model, without device widening. Component radii are `r_j=max(abs(deviceMin-centerMax),abs(deviceMax-centerMin))`, outward at every step. The world radius is `up(sqrt(up(up(r_x*r_x+r_y*r_y)+r_z*r_z)))`. Take the largest over every host vertex, including protected vertices. Stream every vertex of each complete rigid equipment solid through the same device transform enclosure and exact-center comparison. Take the largest equipment radius over every command, not only inward-sheet vertices.

`epsilon=up(maxHostRadius+maxEquipmentRadius)`, allowing exact zero for zero plus zero. A command budget is `.25*(double)command.Clearance`. Require finite nonnegative `epsilon <= budget` for every command, including partial transition. Equality passes. An unbounded result or the next binary64 number above budget fails. The sum covers host/equipment triangle separation by barycentric convexity and the triangle inequality.

Append `CoverageFailureCode.DeviceToleranceExceeded=15` after `UnsupportedAdapter=14` and extend the accepted-code guard, without renumbering. Append `CoverageOracleStage.DeviceTolerance=3`. After successful float construction, an unbounded enclosure or excessive epsilon returns `DeviceTolerance/DeviceToleranceExceeded` before certification, attributed to the first violating command in ascending ID order, with host/equipment identity, `Count=0`, `Limit=0` and default certificate. CPU output outside its own enclosure is an evaluator defect that fails correctness, never a reason to enlarge bounds. Certification gets `Clearance+epsilon` through Task 7's scalar seam, never subtraction or modified commands.

Task 6 exposes internal per-vertex model/world bounds and the equipment-vertex evaluator for Part C native tests and dev-only verification. Part B implements no GPU, shader, readback or Scene3D work.

## File structure

All new engine files live in `KhaozEngine.Render3D/Coverage/` and use namespace `KhaozEngine.Render3D`. New tests use `KhaozEngine.Tests.Render3D`.

| File | Responsibility |
| --- | --- |
| `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCases.cs` | Cold, headless fixture adapters and the 72-case capture manifest |
| `KhaozEngine.Render.Tests/Render3D/CoverageExactCaptureTests.cs` | One opt-in capture writer and unconditional exact replay tests |
| `KhaozEngine.Render.Tests/Fixtures/coverage-exact-v1/*.json` | Canonical input and output float bits, topology identity and refusal evidence |
| `CoverageReferenceTopology.cs` | Canonical position recipes and sorted compact sparse-row gather tables |
| `CoverageReferenceSupport.cs` | Float sheet and boundary data with deterministic lookup and overlay primitives |
| `CoverageReferenceMath.cs` | Explicit scalar arithmetic, operation order and float geometric helpers |
| `CoverageReferencePrecision.cs` | Cited k_op table, budget, outward world radius and epsilon composition |
| `CoverageFloatInterval.cs` | Outward binary64 operations with binary32 widening, flushing and domains |
| `CoverageReferenceEnclosure.cs` | Second device-formula evaluation, branch joins and per-vertex pose bounds |
| `CoverageReferenceEnclosureScratch.cs` | Fixed source, host, group and face interval arrays with reset |
| `CoverageReferenceFrames.cs` | Command admission and CPU-composed frames without construction intervals or slack |
| `CoverageReferenceScratch.cs` | Fixed per-call arrays for interpolation, construction, reductions and normals |
| `CoverageReferenceConstructor.cs` | Interpolation, point conformation, guard reduction and atomic output publication |
| `CoverageReferenceCorrection.cs` | Float face-overlay deficits and group-owned correction and transition |
| `CoverageReferenceNormals.cs` | Per-vertex normal and tangent gather with folded-patch guard |
| `CoverageOracle.cs` | Public headless owner of skinning, reference scratch and exact-certifier scratch |
| `CoverageOracleResult.cs` | Public stage, failure and certificate value types |
| `CoverageFailure.cs` | Append-only device-tolerance code and accepted-code range |
| `KhaozEngine.Render.Tests/Render3D/CoverageReference*Tests.cs` | Separate arithmetic, construction, correction and normal test cycles |
| `KhaozEngine.Render.Tests/Render3D/CoverageOracle*Tests.cs` | Exact-clearance seam, public API, negatives, reuse and allocation tests |

Existing engine changes are limited to exposing the immutable canonical position recipes in `CoverageRefinement.cs`, attaching cold float data in `CoverageSupportSurface.cs`, adding an internal threshold-padding overload in `CoverageCertification.cs`, and appending the failure code in `CoverageFailure.cs`. Existing exact construction behavior remains unchanged.

## Execution and verification convention

These commands are instructions for later execution, not authorization to run them while writing this plan. First create the execution worktree once from the stated parent branch. In every task run this starting guard before edits:

```bash
cd /Users/antonio/KhaozEngine/.worktrees/ec-oracle
test "$(git branch --show-current)" = feature/equipment-coverage-oracle
git merge-base --is-ancestor c525aa2bb52a75db2b4b9c0d17a31638b7938699 HEAD
```

Every task's red test uses `dotnet test` without `--no-build`. A missing new type is an expected red compile failure. After implementation, build the affected test project in Release, which also builds Render3D and its project references. Require zero warnings, then run focused tests with `--no-build`. Never loop a test invocation. Parameterized fixture cases within one invocation are ordinary correctness coverage.

The scoped formatting command uses the existing engine plan convention in `docs/superpowers/plans/2026-10-14-contact-controller-phase5b.md:20`: `dotnet format whitespace ... --verify-no-changes --no-restore`, limited to changed files. Use the project paths below rather than formatting unrelated projects. Also run `git diff --check` and the applicable dash, prose and size guards before each commit. Do not raise a size baseline to make a task pass. The full solution suite is a single later orchestrator finish through `build-slot --heavy`, not a per-task step.

Stage and commit only the task's listed paths, with an explicit pathspec on both commands. No task integrates or pushes independently of the root's assigned branch ownership.

### Task 1: Freeze the current exact constructor before source changes

**Files**

- Create: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCases.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/CoverageExactCaptureTests.cs`
- Create: `KhaozEngine.Render.Tests/Fixtures/coverage-exact-v1/manifest.json` and one JSON file per case
- Modify: `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj:30-34`, copy the new fixtures with `PreserveNewest`
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageExactCaptureTests.cs`
- Read only: `Gpu/CoverageCostGeometry.cs:63-130,146-163`, `CoverageFixture.cs:24-139`, `CoverageEvaluatorTests.cs:200-225`

**Interfaces**

- Consumes: `CoverageCostGeometry.Build(CoverageCostFixtureMode)`, `CoverageCostGeometry.Pose(int, Span<Matrix4x4>)`, `GltfLoader.LoadCoverageSource(byte[], string)`, `CoverageCommands.Poses(...)` and `CoverageEvaluator.TryConstructCandidate(...)`.
- Produces: `internal sealed record CoverageReferenceCase(string Id, CoverageRefinement Topology, Matrix4x4[] Palette, Matrix4x4[] InverseBind, ModelVertex[] Posed, Matrix4x4 Model, CoverageCommandPose[] Commands)`.
- Produces: `internal static CoverageReferenceCase[] CoverageReferenceCases.All()` and `internal static CoverageReferenceCase CoverageReferenceCases.Get(string id)`.
- Produces: `internal sealed record CoverageExactCapture(ModelVertex[] Before, ModelVertex[] Final, double[] FullRecessions, double[] Corrections, double[] ClearanceCoverage, double[] SlackCoverage, CoverageFailure Failure)` and `internal static CoverageExactCapture CoverageExactCaptureTests.Read(string id)` for later tests. `ClearanceCoverage[slot]` and `SlackCoverage[slot]` are the exact frame's `ClearanceCoverage` and `Slack` for each active command slot, so later fidelity tests can run the reference at the capture's own effective clearance.

- [ ] Write `Exact_capture_manifest_has_72_distinct_cases_and_replays_current_constructor`.

```csharp
Assert.Equal(72, cases.Length);
Assert.Equal(72, cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
Assert.Equal(new[] { 2, 3, 4 }, levels);
Assert.Equal(new[] { 0f, .5f, 1f }, weights);
Assert.Equal("c525aa2bb52a75db2b4b9c0d17a31638b7938699", manifest.ConstructorBase);
Assert.Equal(ExplicitFloatBits(capture.Final), ExplicitFloatBits(actual));
Assert.Equal(capture.Failure, failure);
Assert.Equal(ExplicitDoubleBits(capture.FullRecessions), ExplicitDoubleBits(scratch.FullRecessions));
Assert.Equal(ExplicitDoubleBits(capture.Corrections), ExplicitDoubleBits(scratch.Corrections));
Assert.Equal(ExplicitDoubleBits(capture.ClearanceCoverage), ExplicitDoubleBits(activeFrames.Select(f => f.ClearanceCoverage).ToArray()));
Assert.Equal(ExplicitDoubleBits(capture.SlackCoverage), ExplicitDoubleBits(activeFrames.Select(f => f.Slack).ToArray()));
```

  In the same class, `Capture_replay_checks_welds_protected_bytes_and_zero_transition` asserts each group's position bits agree, protected records retain all 16 float fields, and weight zero reproduces `Before` byte for byte. Include `Exact_capture_writer_is_disabled_without_explicit_environment_gate`, asserting ordinary test runs never write a fixture.

- [ ] Run to fail:

```bash
build-slot --label ec-b1-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageExactCaptureTests'
```

  Expected failure: the committed manifest and captures are absent. A skipped capture test is not the red result.

- [ ] Implement the cold adapters and capture writer. The matrix is 36 cost cases (`Typical` and `Limit`, levels 2/3/4, lifted/bent poses, weights 0/.5/1) plus 36 curved cases (ordinary/challenging collar, seams false/true, levels 2/3/4, weights 0/.5/1). Preserve the original cost fixture's map and source. For its level variants, construct a new `HostCoverageMap` from `HostSha256`, `Topology`, `Regions`, `Refinement.Select(r => r with { Level = level })` and `Authoring`, then build a new refinement. Default Typical level 2 and Limit level 4 retain their current geometry.

  For cost supports, load the actual `EquipmentGlb()` through `GltfLoader.LoadCoverageSource`, compile each command's existing inward ranges with its existing local frame, and build poses against the root's lifted/bent palette matrix. Use the complete rigid solid containing all plates, not an isolated replacement plate. Inverse binds are the fixture's two identity matrices. Curved cases use `CoverageFixture.Pose` and the existing plate/pose helpers. All 72 cases must construct successfully. A refused case is investigated before continuing, not omitted from the manifest.

  Write inputs as explicit binary32 hex words and doubles as binary64 hex words in fixed field order, never raw struct padding or decimal round trips. Store source/skin data, palette, inverse binds, model, commands, support source, topology/map identities, source and refined counts, `Before`, final vertices, recessions, corrections and failure. Store the actual capture commit in a separate manifest field. All replays use the frozen input bits, so later changes to procedural fixture helpers cannot redefine the baseline silently. Verify newly rebuilt live fixtures still match those frozen inputs in a second assertion.

  The writer is a single explicitly filtered `[Fact]` named `Write_exact_reference_data_when_explicitly_requested`, enabled only by `KE_WRITE_COVERAGE_EXACT=1`. It writes only this fixture directory. Run it once while the constructor is still unchanged:

```bash
KE_WRITE_COVERAGE_EXACT=1 build-slot --label ec-b1-capture -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~Write_exact_reference_data_when_explicitly_requested'
```

  Review the manifest and source diff before committing. This is correctness capture, with no timings, benchmark driver or measurement output.

- [ ] Run to pass and finish the task:

```bash
build-slot --label ec-b1-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b1-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageExactCaptureTests'
build-slot --label ec-b1-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageReferenceCases.cs Render3D/CoverageExactCaptureTests.cs
```

  Expected: zero-warning build, 72 replay rows passing, no ordinary test writes, format exit 0.

- [ ] Commit the listed files with `render3d(coverage): capture exact construction reference data`. This commit must precede every reference implementation commit. Root preserves it on the owning remote branch before dependent work.

### Task 2: Compile reference data and pin scalar float primitives

**Files**

- Create: `CoverageReferenceTopology.cs`, `CoverageReferenceSupport.cs`, `CoverageReferenceMath.cs`, `CoverageReferencePrecision.cs`, `CoverageFloatInterval.cs`, `CoverageReferenceFrames.cs`, `CoverageReferenceScratch.cs` under `KhaozEngine.Render3D/Coverage/`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageRefinement.cs:30-35,293-322`, expose a read-only internal canonical recipe
- Modify: `KhaozEngine.Render3D/Coverage/CoverageSupportSurface.cs:26-48,61-85`, build and retain immutable reference support data at compilation
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageReferencePrimitiveTests.cs`

**Interfaces**

- Consumes: Task 1 cases, `CoverageRefinement`, `CoverageAffine.TryFrom/TryInverse/Multiply`, source triangles, `PositionGroup`, `ShadingGroup` and `IsCoreVertex(int)`.
- Produces: `internal readonly record struct CoveragePositionRecipe(int A, int B, int C, int WeightA, int WeightB, int WeightC, int Denominator)` and `internal CoveragePositionRecipe CoverageRefinement.GetPositionRecipe(int positionGroup)`.
- Produces: `internal sealed class CoverageReferenceTopology`, constructor `(CoverageRefinement topology)`, immutable `Topology`, `Recipes`, `GroupRepresentatives`, `CoreFaceOffsets`, `CoreFaces`, `VertexFaceOffsets`, `VertexCorners`, `ShadingCornerOffsets`, `ShadingCorners`. Arrays are internal owned arrays, not public mutable collections.
- Produces: `internal sealed class CoverageReferenceSupport`, constructor `(CoverageSupportSurface support)`, `internal bool TryHeight(Vector2 point, out float height)` and `internal bool TryBoundary(Vector2 point, out float height, out float distance)`. `CoverageSupportSurface` exposes its owned cold data as `internal CoverageReferenceSupport ReferenceData { get; }`.
- Produces: `internal static class CoverageReferenceMath`, with `internal static float Dot3(Vector3 a, Vector3 b)`, `internal static float Dot2(Vector2 a, Vector2 b)`, `internal static Vector3 Cross(Vector3 a, Vector3 b)`, `internal static Vector3 Point(Vector3 p, in Matrix4x4 matrix)`, `internal static Vector3 Linear(Vector3 p, in Matrix4x4 matrix)`, `internal static float Edge(Vector3 a, Vector3 b, Vector3 p)` and `internal static float Height(Vector3 a, Vector3 b, Vector3 c, Vector2 p)`. Tests call these exact helpers, rather than defining a second production formula.
- Produces: `internal sealed class CoverageReferenceScratch`, constructor `(CoverageRefinement topology, int commandCapacity = CoverageScratch.MaxCommands)`. Own `ReferenceTopology`, exact-sized `Before`, `After`, `Published`, group positions and recessions, per-face correction and guard results, frame/order/region arrays. No call grows arrays.
- Produces: `internal readonly record struct CoverageReferenceFrame(int Command, Matrix4x4 HostToCoverage, Matrix4x4 HostToActualSheet, Matrix4x4 ActorLinear, Vector3 Recess, float ZScale, float CapMargin, float ClearanceCoverage, float ActualClearanceCoverage, float CorrectionRate)`.
- Produces: `internal static bool CoverageReferenceFrames.TryPrepare(CoverageRefinement topology, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, out CoverageFailure failure)`.
- Produces: `internal readonly record struct CoverageFloatInterval(double Min, double Max)`, `internal bool IsFinite { get; }`, `internal bool Contains(double value)`, point factory `internal static CoverageFloatInterval Point(float value)` and `Unbounded`. Static `Add`, `Subtract`, `Multiply`, `Divide`, `Min`, `Max`, `Hull` take two intervals and return one. Static `Sqrt`, `Abs`, `Negate`, `Store32` take one interval. `Clamp` takes one interval and two float limits. `CompareLess(CoverageFloatInterval a, CoverageFloatInterval b)` returns `internal enum CoverageIntervalTruth { False, True, Either }`. Enclosure-only `Reciprocal1` and `InverseSqrt2` implement the cited expansions.
- Produces: `internal readonly record struct CoverageVertexEnclosure(CoverageFloatInterval X, CoverageFloatInterval Y, CoverageFloatInterval Z)` and its `internal bool Contains(Vector3 value)`.
- Produces: `internal static class CoverageReferencePrecision` with `internal const int BasicUlps=1, DivideUlps=4, SqrtUlps=16, StoreUlps=1`, exact-power `internal const double` constants `MinNormal32`, `MinSubnormal32`, `MaxDivisor32`, `internal static double Budget(float clearance)`, `internal static double Radius(in CoverageVertexEnclosure device, in CoverageVertexEnclosure exactCenter)` and `internal static double ComposeTolerance(double hostRadius, double equipmentRadius)`. Implement the resolved NC1 formulas, never a coordinate-only tolerance.

- [ ] Write `Scalar_float_query_order_and_gather_tables_are_pinned` and `Coordinate_only_roundoff_does_not_bound_skinny_triangle_height`.

```csharp
Assert.Equal(12, data.Recipes.First(r => r.WeightA != 12).Denominator);
Assert.Equal(0, data.CoreFaceOffsets[0]);
Assert.Equal(data.CoreFaces.Length, data.CoreFaceOffsets[^1]);
Assert.Equal(data.VertexCorners.Length, data.VertexFaceOffsets[^1]);
Assert.True(EachRowIsStrictlySortedAndDuplicateFree(data.CoreFaceOffsets, data.CoreFaces));
Assert.Equal(0f, Height(a, b, c, new(.5f, .5f)));
Assert.Equal(.5f, Height(a, b, c, new(.5f, .5f + MathF.ScaleB(1f, -24))));
Assert.Equal(MathF.ScaleB(1f, -23), Edge(a, b, c));
Assert.True(MathF.ScaleB(1f, -23) > 1e-12f);
```

  Use the NC1 triangle for `a`, `b`, `c`. Add `Interval_operations_pin_ulps_domains_and_input_flushing`: assert constants `1,4,16,1`, `Budget(.02f)==.25*(double).02f`, outward endpoints bracket an independent binary64 result, and `Store32(Point(1f))` extends below/above `1f`. For `n=2^-140`, `large=2^100`, assert multiplication contains both `0` and `(double)n*large`. Assert division by `[-1,1]`, `2^-127` or `2^127`, square root of `[-1,1]`, and finite-binary32 overflow return unbounded. Test valid divisor endpoints `2^-126` and `2^126` with finite normal quotients, cancellation, signed zero and binade crossings. A product/sum can contain zero without an invalid domain. The skinny triangle's interval height must include `0` and `.5` when XY covers both adjacent queries. Independently evaluate the positive-normal Vulkan square-root expansion and assert its symmetric envelope is contained. Pin the resolved RV1 rule, including finite `[0,h]` square-root enclosures. Add `Shared_edge_and_boundary_ties_use_source_order`, with two coplanar triangles and a square rim: exact edge/vertex hits agree, the first triangle/edge by original source ordinal wins a tie, `distance == CollarWidth` produces spatial weight zero. Add `Reference_frames_do_not_call_exact_uncertainty_admission`: clone the `unabsorbed-error` scenario and assert frame admission itself succeeds. Later certification may still refuse it.

- [ ] Run to fail:

```bash
build-slot --label ec-b2-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferencePrimitiveTests'
```

  Expected: new scalar reference and interval types are missing.

- [ ] Implement the listed interfaces and the following arithmetic contract. Each scalar operation rounds to binary32 in the written order. No `Vector3.Lerp`, vectorized dot product, implicit double promotion, `FusedMultiplyAdd` or reciprocal approximation appears in kernel-reproduced calculations. Explicit helpers are `Dot3(a,b)=((a.X*b.X)+(a.Y*b.Y))+(a.Z*b.Z)`, `Dot2(a,b)=(a.X*b.X)+(a.Y*b.Y)`, and componentwise `Cross` with two products followed by one subtraction. Point transforms use `(((x*m1)+(y*m2))+(z*m3))+translation`. Linear transforms omit only the translation. Norm is `MathF.Sqrt(Dot(v,v))`, division is written `/`. The enclosure permits the cited finite rounding and contraction envelope. Reassociation outside this expression graph remains forbidden. RV2 is the later backend-lowering verification obligation.

  Copy the existing admission checks at `CoverageFrames.TryPrepare:169-207` in the same order and retain the `1e6` positive-affine condition cap. Sort commands by ID and retain the input index in each frame. Do not call `CoverageFrames.TryPrepare` and then discard its uncertainty refusal. Perform the CPU-only affine inversion/composition with the existing double `CoverageAffine`, and round each composed coefficient once when emitting the float frame. In the formulas below `A=model`, `E=EquipmentWorld`, `C=CoverageWorld`, `P=Support.SourceToCoverage`, and `S` is the float maximum sheet slope computed with the scalar helpers:

```text
H = round32(A * inverse(C))
J = round32((A * inverse(E)) * P)
D = round32(inverse(A).linear(C.rowZ))
ZScale = length(C.rowZ)
CapMargin = Clearance * length(inverse(C).columnZ)
delta = Clearance * .25f
ReservedClearance = Clearance + delta
ClearanceCoverage = (ReservedClearance * sqrt(1 + S*S)) * SpectralUpper(inverse(C))
ActualClearanceCoverage = (ReservedClearance * sqrt(1 + S*S)) * SpectralUpper(inverse(E) * P)
d = J.linear(D)
CorrectionRate = d.Z - S * sqrt(d.X*d.X + d.Y*d.Y)
```

  CPU affine inverses/compositions use `CoverageAffine` then round coefficients once. For scalar `SpectralUpper`, use those rounded inverse-map coefficients, and compute every remaining frame scalar in the written binary32 order. For slope, take `n=Cross(b-a,c-a)` on each rounded support triangle in source order, then `sqrt(n.X*n.X+n.Y*n.Y)/abs(n.Z)` and the maximum. Zero/non-finite denominator or result refuses the frame. All resulting frame values, including `CorrectionRate`, are point intervals in Task 6 because the same bits are uploaded.

  `SpectralUpper(M)` is the square root of the maximum absolute row sum of `M*M^T`, with rows accumulated left to right and off-diagonal terms made absolute before addition, as in the existing `SpectralUpper` algorithm. No exact-code `1+1e-12` factor, interval spread, `Slack`, uncertainty percentage or uncertainty refusal enters a reference frame. Non-finite results and nonpositive correction rates retain `UnsupportedTransform`. Preserve exact `CoverageWorld == Support.CoverageLocal * EquipmentWorld` admission before rounding.

  Cold data uses `Support.Positions`, which are rounded projections of the actual selected inward sheet, together with its original source triangle and boundary ordinals. Keep `SourceToCoverage`, selected source indices and closed-solid identity. It is a float representation of that sheet, not a fitted height plane. The oracle still certifies the original exact projected sheet and original solid. Use a 16 by 16 XY uniform grid of inclusive triangle AABBs, with candidates sorted by inward source ordinal. Grid acceleration must produce the same candidate set and first hit as a complete ordered scan. Exact outer maximum coordinates map to cell 15. Points outside the grid bounds miss.

  Float height lookup uses `Edge(a,b,p)=((b.X-a.X)*(p.Y-a.Y))-((b.Y-a.Y)*(p.X-a.X))`, inclusive oriented signs and no fuzzy inside tolerance. Exact float vertex hits return that vertex height. An edge hit uses its lexicographically ordered endpoints and the larger absolute XY delta, preferring X on equality, followed by `t=(p.axis-a.axis)/(b.axis-a.axis)` and `(1-t)*a.Z+t*b.Z`. Interior height is `((u*a.Z)+(v*b.Z)+(w*c.Z))/area`, left-associated, with `u=Edge(b,c,p)`, `v=Edge(c,a,p)`, `w=Edge(a,b,p)`.

  Boundary lookup gathers all boundary edges in ordinal order. For each, `t=clamp(Dot2(p-a,b-a)/Dot2(b-a,b-a),0,1)`, nearest XY is `a.xy+t*(b.xy-a.xy)`, distance squared is `Dot2(p-nearest,p-nearest)` and height is `a.Z+t*(b.Z-a.Z)`. Choose the strictly smaller squared distance, retain the earlier edge on equality, and take one square root after selection. Zero-length or non-finite query arithmetic returns false. Do not use the exact compatibility queries, interval midpoints or interval endpoints.

  Build the CSR lists in ascending triangle/corner order. A core face appears once in each distinct incident position group, even when that face repeats a group. `ShadingCorners` contains every indexed face-corner contribution to that shading group, not just the representative vertex's neighbors. This is the normal seam requirement missing from a plain vertex-to-face list.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b2-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b2-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReferencePrimitiveTests|FullyQualifiedName~CoverageExactCaptureTests'
build-slot --label ec-b2-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageReferenceTopology.cs Coverage/CoverageReferenceSupport.cs Coverage/CoverageReferenceMath.cs Coverage/CoverageReferencePrecision.cs Coverage/CoverageFloatInterval.cs Coverage/CoverageReferenceFrames.cs Coverage/CoverageReferenceScratch.cs Coverage/CoverageRefinement.cs Coverage/CoverageSupportSurface.cs
build-slot --label ec-b2-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageReferencePrimitiveTests.cs
```

  Expected: zero-warning build, primitive tests and all exact replays pass, both format checks exit 0.

- [ ] Commit the listed paths with `render3d(coverage): compile scalar reference geometry and gather tables`.

### Task 3: Interpolate and conform position groups with construction guards

**Files**

- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceConstructor.cs`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferenceScratch.cs`, arrays introduced in Task 2
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceConstructionTests.cs`
- Read only: `CoverageRefinement.cs:247-321`, `CoverageEvaluator.cs:82-195,199-220`, `CoverageCertificationTests.cs:157-200`

**Interfaces**

- Consumes: Task 2 data and frames, with reserved `ClearanceCoverage` and `ActualClearanceCoverage` from NC1.
- Produces: `internal static bool CoverageReferenceConstructor.TryInterpolate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource, CoverageReferenceScratch scratch, out CoverageFailure failure)`.
- Produces: `internal static bool CoverageReferenceConstructor.TryConformGroups(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, out CoverageFailure failure)`.
- Produces: `internal static bool CoverageReferenceConstructor.TryConstruct(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, Span<ModelVertex> finalVertices, out CoverageFailure failure)` and the internal test overload with a trailing `ReadOnlySpan<double> clearanceCoverageOverride`, where an empty span means the reserve and a full span sets each active slot's target clearance as the NC1 like-for-like ruling states. Task 4 supplies correction/transition and Task 5 supplies final normals before this entry point is complete.

- [ ] Write `Float_conformation_obeys_guard_codes_and_never_publishes_partial_output`, using the existing scenario mutations rather than calling the exact constructor for an expected code.

```csharp
Assert.Equal(CoverageFailureCode.CoreEscaped, ConstructFailure("escaped-core").Code);
Assert.Equal(CoverageFailureCode.CapContact, ConstructFailure("cap-contact").Code);
Assert.Equal(CoverageFailureCode.RecessionExceeded, ConstructFailure("budget").Code);
Assert.Equal(CoverageFailureCode.UnsupportedSupport, ConstructFailure("support-gap").Code);
Assert.Equal(CoverageFailureCode.UnsupportedTransform, ConstructFailure("nonfinite").Code);
Assert.All(output, v => Assert.Equal(sentinel, v)); // sentinel position (7,8,9)
Assert.Equal(1u, failure.CommandId);
Assert.Equal(1ul, failure.EquipmentInstanceId);
```

  Add `Point_target_reserves_quarter_clearance_in_the_coverage_mapping`: unit flat frames, `Clearance=.03125f`, `height=0f`, full-strength core, `q.Z=0f`. Assert exact `delta=.0078125f`, `ReservedClearance=.0390625f` and target Z `-.0390625f`. In a sloped/nonuniform frame assert the target equals the pinned mapped formula and cap margins still use authored `.03125f`. Include `Point_conformation_checks_the_full_target_at_zero_transition` with weight `0f` and recession budget `.1f`, expecting `RecessionExceeded`. Include `Reference_interpolation_welds_reversed_edges_at_levels_2_3_4` and `Original_protected_records_are_byte_exact`, asserting group position bits agree, generated weights use denominator 12, and original protected vertices copy all fields. Non-finite vertex attributes and unequal posed aliases return `UnsupportedTransform` and `FoldedPatch`, respectively.

- [ ] Run to fail:

```bash
build-slot --label ec-b3-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceConstructionTests'
```

  Expected: the constructor is absent.

- [ ] Implement the signatures and pin the float interpolation and pointwise formulas. Validate source/output lengths, disjoint spans and scratch identity before any write, with the same argument-exception policy as the exact evaluator. Work only in scratch until the complete constructor succeeds. Phase-local tests call the internal phase methods, not a partly working public API.

  Original vertices copy the complete source record. Generated positions use the canonical group recipe, never whichever material member visits first. Sort an edge's endpoints by source vertex index and reverse its integer numerator when swapping. Compute each edge component as `(a*(1-t))+(b*t)` with `t=k/12f`. Interior components are `(((a*wa)+(b*wb))+(c*wc))/12f`. Compute one position per group, then copy its bits to every member. Generated color and UV use the same weighted numerator order. Normal and tangent directions use that sum, scalar norm and normalization. Truly absent directions stay zero, nonzero sums that fail the existing `2^-25` surviving-contribution criterion refuse `UnsupportedTransform`. Tangent W is the weighted sign, with exact zero choosing `+1f`. No change to the public exact interpolation implementation.

  For each group's lowest-index positive-strength representative, use `q=H(position)`, and the frame/command for its region. A core group is one containing any core vertex. Check source caps before querying support. Missing height for core is `CoreEscaped`. Missing height for collar queries the actual boundary, with lookup failure `UnsupportedSupport`. Boundary distance `>= CollarWidth` returns the original position. Otherwise:

```text
u = clamp(1 - distance/CollarWidth, 0, 1)
spatial = (u*u) * (3 - 2*u)             // inside the sheet: spatial = 1
strength = core ? 1 : representative.Strength
target = height - ClearanceCoverage
request = max(0, q.Z - target)
full = (strength * spatial) * request
moved.component = position.component - full * D.component
```

  The target includes `.25f*Clearance` mapped exactly like clearance. There is no extra rounding bias on a positive request. A zero request stays zero. Check full world recession, lower and upper caps on the full moved position, and finite values before transition. Use `UnsupportedTransform` for non-finite arithmetic, `RecessionExceeded` for a finite world distance above `MaxWorldRecession`, and `CapContact` for cap failure. Store the full scalar recession even when the eventual transition is zero. Do not apply transition yet.

  Reduce group failures in ascending representative vertex order, retaining the first code and sorted command slot and counting repeats with that same pair. Refusals from admission precede interpolation, which precedes point guards. A later phase cannot overwrite an earlier refusal. This reproduces the role of `CoverageTally` without depending on a type housed in the exact evaluator that Part C will remove.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b3-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b3-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReferenceConstructionTests|FullyQualifiedName~CoverageReferencePrimitiveTests|FullyQualifiedName~CoverageExactCaptureTests'
build-slot --label ec-b3-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageReferenceConstructor.cs Coverage/CoverageReferenceScratch.cs
build-slot --label ec-b3-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageReferenceConstructionTests.cs
```

  Expected: zero warnings, all selected cases pass, format exit 0.

- [ ] Commit listed paths with `render3d(coverage): add float group conformation and guard refusals`.

### Task 4: Gather core-face correction and apply transition once per group

**Files**

- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceCorrection.cs`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferenceSupport.cs`, add float analytic overlay beside the Task 2 queries
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferenceConstructor.cs`, compose the correction and transition phases
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCorrectionTests.cs`
- Read only: `CoverageCoreCorrection.cs:11-158`, `CoverageFaceOverlay.cs:47-126`, `CoverageCoreCorrectionTests.cs:16-177`

**Interfaces**

- Consumes: full pointwise candidate and group recessions from Task 3, CSR core faces and float support from Task 2.
- Produces: `internal bool CoverageReferenceSupport.TryOverlayMaximum(ReadOnlySpan<Vector3> host, out float maximum)`.
- Produces: `internal static bool CoverageReferenceCorrection.TryApply(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, out CoverageFailure failure)`.
- Produces: `internal static void CoverageReferenceCorrection.ApplyTransition(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch)`.

- [ ] Write `Gathered_correction_moves_idle_core_groups_once_and_matches_exact_capture_margin` for levels `2`, `3`, `4`, seams both ways and weights `0f`, `.5f`, `1f`.

```csharp
Assert.True(corrections.Any(r => r > 0f));
Assert.True(idleCoreGroupMoved);
Assert.Equal(groupFinalBits[0], groupFinalBits[1]);
Assert.True(worldDistance <= command.Clearance * .25); // like-for-like mode, NC1 root ruling
Assert.Equal(0, protectedByteChanges);
Assert.Equal(0, correctionScatterWrites);
Assert.Equal(0, duplicateCoreFaceEntries);
Assert.Equal(beforeBits, zeroWeightFinalBits);
Assert.Equal(CoverageFailureCode.RecessionExceeded, zeroWeightInvalidTarget.Code);
Assert.Equal(CoverageFailureCode.CapContact, cappedCorrection.Code);
Assert.Equal(forwardFailure, reversedCommandFailure);
```

  Count `correctionScatterWrites` through a test-only observer on the reference phase, not an assumption about output equality. Add `Float_overlay_includes_edge_crossings_not_only_corners`: reuse `CoverageFaceOverlayTests` pair inputs whose maximum is at an edge intersection, assert the analytical expected maximum and that corner-only evaluation is smaller. Add `Correction_is_computed_from_immutable_pointwise_faces` by comparing ascending and reversed group evaluation orders bit for bit. This prevents accidental iterative relaxation.

- [ ] Run to fail:

```bash
build-slot --label ec-b4-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceCorrectionTests'
```

  Expected: missing correction implementation.

- [ ] Implement the signatures with one immutable pointwise candidate, one face-result array and one group-result array. A face never updates its vertices. Compute each core face's result once, then each position group gathers its maximum incident correction from the sorted CSR row. Core corners with strength other than `1f` refuse `FoldedPatch`, absent actual/nominal sheet membership refuses `CoreEscaped`, and non-finite or nonpositive-rate arithmetic refuses `UnsupportedTransform`.

  Pin float overlay to the existing analytic candidate set, not samples: included host corners, included support corners, and all nine edge pairs in ascending `(hostEdge,supportEdge)` order. Inclusive orientation tests use `Edge` from Task 2. Collinear overlap uses included endpoints. Opposite-sign crossings use `t=ca/(ca-cb)`, clamp to `[0,1]`, then `a+t*(b-a)` componentwise. Evaluate both affine heights with Task 2's ordered barycentric formula. Select the maximum `hostHeight-sheetHeight` across every inward triangle in source order. Zero/non-finite projected area or a non-finite crossing denominator refuses, without an interval ambiguity test.

  With `J=HostToActualSheet`, `H=HostToCoverage` and slope `S`, gather over the three full pointwise face vertices:

```text
loss(q) = abs((J(q)-H(q)).Z)
          + S * length((J(q)-H(q)).XY)
discrepancy = max(loss(vertex0), max(loss(vertex1), loss(vertex2)))
deficit = (overlayMaximum + ActualClearanceCoverage) + discrepancy
correction = deficit <= 0 ? 0 : deficit / CorrectionRate
groupCorrection = max(all incident core-face corrections, 0)
fullTotal = groupCorrection == 0 ? pointwiseFull : pointwiseFull + groupCorrection
fullPosition = before - fullTotal * D
```

  `ActualClearanceCoverage` contains `Clearance+delta`. Do not copy the exact constructor's conversion term or add rho. Task 6 encloses rounding, both position stores, the deficit and rate division. Task 7 checks the actual float candidate exactly with `+epsilon`. Insufficient headroom remains a certification failure. Add `Correction_uses_reserved_actual_clearance_without_extra_bias`, asserting the scalar deficit/rate and sum bits. Add a finite positive `CorrectionRate=2^-20` case, whose amplified interval is tested in Task 6. The exact path and its conversion bound remain unchanged.

  Validate full-position finite values, world Euclidean recession and both cap sides before transition. Reduce face failures in sorted command ID, triangle, corner order, followed by full-target group failures in representative order. Count repeated selected failures deterministically. Command submission order does not enter these keys.

  Transition uses `TransitionWeight` exactly once on the full scalar total: weight `0f` copies `Before` exactly, weight `1f` reuses the validated full-position bits, otherwise `applied=weight*fullTotal` then `before.component-applied*D.component`. Copy each group's position bits to all aliases. Do not interpolate between already rounded full positions. The full target's guard runs even for weight zero.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b4-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b4-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReferenceCorrectionTests|FullyQualifiedName~CoverageReferenceConstructionTests|FullyQualifiedName~CoverageExactCaptureTests'
build-slot --label ec-b4-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageReferenceCorrection.cs Coverage/CoverageReferenceSupport.cs Coverage/CoverageReferenceConstructor.cs
build-slot --label ec-b4-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageReferenceCorrectionTests.cs
```

  Expected: zero warnings, fidelity within the unchanged quarter-clearance bound for every group, all tests pass, format exit 0. A core-corrected miss blocks this task and returns its exact fixture/group evidence to root.

- [ ] Commit listed paths with `render3d(coverage): gather float core correction and transition weights`.

### Task 5: Gather normals and complete the reference constructor

**Files**

- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceNormals.cs`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferenceConstructor.cs`, call normals before publishing
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceNormalTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceFidelityTests.cs`
- Read only: `CoverageSurfaceNormals.cs:19-89`, `CoverageCertification.cs:173-205`, `CoverageNormalTests.cs:16-82`

**Interfaces**

- Consumes: Task 2 shading-group and vertex CSR rows, Task 4 final positions, `Before` records.
- Produces: `internal static bool CoverageReferenceNormals.TryUpdate(CoverageRefinement topology, CoverageReferenceScratch scratch, out CoverageFailure failure)`.
- Completes: Task 3 `CoverageReferenceConstructor.TryConstruct(...)`, with no certification and no certificate.

- [ ] Write `Vertex_gathers_preserve_shading_seams_tangent_handedness_and_untouched_bytes` and `Every_reference_position_is_within_exact_quarter_clearance` over all 72 captures.

```csharp
Assert.Equal(0, positionWeldBitDifferences);
Assert.True(Vector3.Distance(expectedNormal, actual.Normal) < 2e-5f);
Assert.True(Vector3.Distance(expectedTangent, actualTangent) < 2e-5f);
Assert.NotEqual(hardSeamLeft.Normal, hardSeamRight.Normal);
Assert.Equal(smoothSeamLeft.Normal, smoothSeamRight.Normal);
Assert.Equal(before.Tangent.W, actual.Tangent.W);
Assert.Equal(Vector4.Zero, absentTangent);
Assert.Equal(0, protectedByteChanges);
Assert.True(worldPositionError <= clearance * .25); // like-for-like mode, NC1 root ruling
Assert.True(reserveShift <= mappedReserveBound); // conform-only groups, reported for corrected groups
Assert.Equal(CoverageFailureCode.FoldedPatch, changedDegenerateFace.Code);
Assert.Equal(beforeBits, untouchedDegenerateFaceBits);
```

  Add `Reserve_can_consume_the_entire_fidelity_budget` with Task 3's flat unit case and zero-slack analytical comparator. Assert displacement exactly `.0078125`, equal to `.25*.03125`, without claiming strict headroom. Add `Reserve_mapping_is_not_a_universal_fidelity_proof`: set analytic `M*W=2`, zero slack and full weight, and assert predicted difference `2*delta`. This does not exempt any frozen fixture. Actual misses return to root without widening.

  Normal expectations use the independent `CoverageGeometryOracle.ExpectedNormal` and `ExpectedTangent`, with the existing `2e-5f` normal-only tolerance. Do not reuse that tolerance for position fidelity. For positions, subtract stored float coordinates in double, transform the difference by the model's linear coefficients in double and take its Euclidean norm. Select the owning command's `.02f * .25` bound for active fixture groups. Protected output must be byte-exact to the reference's unconformed interpolation, while the difference between exact and float generated interpolation is included in fidelity.

  Add `Collapsed_or_flipped_changed_patch_refuses_before_output_copy`, checking both a collapsed changed face and a face with negative dot against its original normal. Add `All_existing_construction_negatives_keep_their_codes`, covering candidate-test rows except `unabsorbed-error`, which was explicitly an uncertainty refusal, and rows previously deferred to exact certification. Test that removed uncertainty refusal separately in Task 8 rather than claiming parity for it.

- [ ] Run to fail:

```bash
build-slot --label ec-b5-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceNormalTests|FullyQualifiedName~CoverageReferenceFidelityTests'
```

  Expected: incomplete normal phase and full constructor.

- [ ] Implement `TryUpdate` with one independent gather per indexed vertex. Compute each changed face's float tangent-plane bases using the formulas at `CoverageSurfaceNormals.cs:42-54`, replacing its double `V` operations with Task 2 scalar helpers. Keep its `1e-12` relative degeneracy criterion. In addition, a changed face whose final cross product has nonpositive dot against its original cross product is `FoldedPatch`. Commanded projected faces use the existing `1e-9` nonvertical criterion and one orientation sign per command, reduced deterministically. An untouched degenerate uncommanded face contributes nothing and does not refuse.

  For a changed vertex normal, gather face-corner contributions from the entire `ShadingGroup`, in ascending `(triangle,corner)` order. For each authored normal `n0`, contribution before normalization is `au*dot(u,n0)+av*dot(v,n0)+an*dot(n,n0)`, left-associated. Normalize each nonzero contribution before adding to the float sum, then normalize the final sum. Tangents gather only that indexed vertex's corner list with `s*dot(bu,t0)+t*dot(bv,t0)+m*dot(bn,t0)`, preserving its original W. Source zero normal/tangent remains zero. Undisplaced vertices copy the complete `Before` record. Any non-finite value is `UnsupportedTransform`, invalid gradient or direction is `FoldedPatch`. Normal-stage failures retain the existing no-command attribution, with host identity and zero IDs/count/limit.

  The constructor's exact phase order is admission, reference interpolation, point groups, immutable face corrections, group maximum and full guard, transition, normal gather, then one final span copy. Failure leaves caller output untouched. There is no call to the exact evaluator, exact support queries, interval arithmetic or `CoverageSurfaceNormals.Update` in this constructor. Keep the old path fully present and unchanged.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b5-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b5-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests'
build-slot --label ec-b5-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageReferenceNormals.cs Coverage/CoverageReferenceConstructor.cs
build-slot --label ec-b5-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageReferenceNormalTests.cs Render3D/CoverageReferenceFidelityTests.cs
```

  Expected: zero warnings, full fidelity and refusal suite passes, format exit 0. Do not refresh captures or widen bounds to make this pass.

- [ ] Commit listed paths with `render3d(coverage): gather reference normals and enforce exact fidelity`.

### Task 6: Evaluate per-pose enclosures and expose Part C's internal API

**Files**

- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosure.cs`
- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosureScratch.cs`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferencePrecision.cs`, compose outward host/equipment maxima
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs`

**Interfaces**

- Consumes: Tasks 2 to 5's scalar operation graph, immutable topology/support data and rounded CPU frame uniforms. `composedPalette` is the exact binary32 upload produced by `SkinningMath.Compose`, not joint matrices composed a second time on the device.
- Produces: `internal sealed class CoverageReferenceEnclosureScratch`, constructor `(CoverageRefinement topology, int commandCapacity = CoverageScratch.MaxCommands)`. Preallocate source, refined, group, face and final interval arrays plus branch/guard state. Expose `internal ReadOnlySpan<CoverageVertexEnclosure> HostModelBounds`, `HostWorldBounds` and `internal double DeviceTolerance`, invalidated by the next evaluation. Store no variable-size equipment array.
- Produces: `internal static void CoverageReferenceEnclosure.Evaluate(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> composedPalette, ReadOnlySpan<ModelVertex> referenceFinal, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch referenceScratch, CoverageReferenceEnclosureScratch scratch)`.
- Produces: `internal static CoverageVertexEnclosure CoverageReferenceEnclosure.EquipmentWorldVertex(CoverageSourceTopology source, int vertex, in Matrix4x4 equipmentWorld)`. This value API exposes any full-solid vertex without per-call storage. Evaluate streams these values into its equipment maximum.
- Evaluate neither decides the budget refusal nor invokes certification. No finite proof returns unbounded boxes and `DeviceTolerance=PositiveInfinity`. Invalid lengths or scratch identity throw before mutation. Each evaluation resets active lengths, maxima and branch/guard state.

- [ ] Write `Every_fixture_float_vertex_is_inside_its_pose_enclosure` over all 72 frozen cases, every seam member and zero/half/full transition. Assert `HostModelBounds.Length==topology.VertexCount`, `HostModelBounds[i].Contains(referenceFinal[i].Position)` for every vertex, finite model/world bounds and finite epsilon. Compute world centers independently with outward binary64 arithmetic and assert their inclusion. Report RV1 conflicts or unbounded fixture rows, never omit them.

```csharp
Assert.Equal(topology.VertexCount, enclosure.HostModelBounds.Length);
Assert.True(enclosure.HostModelBounds[i].Contains(referenceFinal[i].Position));
Assert.True(enclosure.HostWorldBounds[i].X.IsFinite);
Assert.True(enclosure.HostWorldBounds[i].Y.IsFinite);
Assert.True(enclosure.HostWorldBounds[i].Z.IsFinite);
Assert.True(double.IsFinite(enclosure.DeviceTolerance));
Assert.True(CoverageReferencePrecision.Radius(deviceBox, zeroCenter) >= 5d);
Assert.True(CoverageReferencePrecision.ComposeTolerance(5d, 12d) >= 17d);
```

  Add `Enclosures_propagate_conditioning_flushing_and_branch_alternatives`. Assert the skinny-triangle query interval contains both analytical heights. Reducing positive correction rate from `2^-10` to `2^-20` increases correction width. A divisor interval crossing zero is unbounded. At an uncertain support edge or nearest-edge tie both candidate outputs belong to the joined box. An uncertain guard encloses covered and uncovered positions. A final-store case contains the adjacent permitted floats and CPU value. Tests must not replay only the CPU-selected branch.

  Add `World_tolerance_sums_host_and_full_equipment_bounds`. With independent host box `[-3,3] x [-4,4] x [0,0]` centered at zero and equipment radius `12`, assert host radius at least `5`, combined epsilon at least `17`, and excess limited to the specified outward binary64 steps. A large finite equipment vertex outside the inward-sheet subset must contribute transform error. Test nonuniform models, negative coordinates, large-translation cancellation and protected vertices. Reordered commands produce identical epsilon and ID-keyed bounds. Unbounded then valid evaluation reproduces the first valid bounds and epsilon exactly.

- [ ] Run to fail:

```bash
build-slot --label ec-b6-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests'
```

  Expected: pose enclosure types are absent.

- [ ] Implement the declared API as a second evaluation through Task 2's interval set. Expand skin-matrix blending, source point/direction transforms, canonical interpolation, conformation, analytic face overlays, core correction, transition, normal guards, final selection/store and world transforms in reference order. Uploaded frame fields are point inputs. Normal guard arithmetic affects covered/uncovered selection even when normals do not move positions. Keep the evaluator separate from float construction and do not change existing constructor refusal codes.

  Apply NC1's widening, subnormal operand hull, domains, candidate inclusion and branch joins. Evaluate full-equipment transforms from `Support.Source.Positions` and `EquipmentWorld`. Centers match exact geometry certification, using outward binary64 arithmetic without device widening. Compute component radii, maximum Euclidean host/equipment radii and `ComposeTolerance`. Expose host model bounds for future GPU-buffer tests and world bounds for epsilon composition. Implement no native tests or dev readback here.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b6-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b6-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests|FullyQualifiedName~CoverageReferencePrimitiveTests|FullyQualifiedName~CoverageReferenceFidelityTests'
build-slot --label ec-b6-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageReferenceEnclosure.cs Coverage/CoverageReferenceEnclosureScratch.cs Coverage/CoverageReferencePrecision.cs
build-slot --label ec-b6-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageReferenceEnclosureTests.cs
```

  Expected: zero warnings, every fixture's CPU output contained, every fixture bound finite, domain/conditioning/branch tests pass, format exit 0. These are acceptance gates, not proofs obtained during planning.

- [ ] Commit listed paths with `render3d(coverage): enclose reference poses for device equivalence`.

### Task 7: Add the minimal exact-certification clearance-padding seam

**Files**

- Modify: `KhaozEngine.Render3D/Coverage/CoverageCertification.cs:40-62,100-130,333-365`
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageOracleCertificationTests.cs`

**Interfaces**

- Consumes: existing exact `CoverageCertification.ValidateGeometry`, its prepared frames and exact predicates.
- Produces: internal overload `static bool CoverageCertification.ValidateGeometry(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after, in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, double worldClearancePadding, CoverageScratch scratch, out CoverageGeometryCertificate certificate, out CoverageFailure failure)`.
- Preserves: both existing public `ValidateGeometry` overloads and render-aware `Validate` with padding zero.

- [ ] Write `Positive_oracle_padding_only_strengthens_separation` and `Padding_never_changes_caps_budget_domain_or_protected_checks`.

```csharp
Assert.True(ValidateExact(surface, padding: 0, out _));
Assert.False(ValidateExact(surface, padding: .04, out var failure));
Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Code);
Assert.Equal(1u, failure.CommandId);
Assert.True(failure.Count > 0);
Assert.Equal(originalFailure, PaddedFailure("cap-contact", .001));
Assert.Equal(originalFailure, PaddedFailure("budget", .001));
Assert.Equal(originalFailure, PaddedFailure("escaped-core", .001));
Assert.Equal(originalFailure, PaddedFailure("rear-host", .001));
Assert.Equal(default, rejectedCertificate);
```

  Here `surface` is the accepted exact full-weight output for `CurvedPanel` with clearance `.02f`. Raising its required distance by `.04` exceeds its small construction headroom. Also cover protected-byte tampering, weld tampering, folded faces and an inside-solid scenario. Zero-padding certificate and failure fields must equal the existing public overload exactly. Negative, NaN or infinite padding throws `ArgumentOutOfRangeException` before scratch mutation. Add `Padding_also_strengthens_other_commanded_regions` using the existing two-region gap fixture, so the seam does not protect only own-region faces.

- [ ] Run to fail:

```bash
build-slot --label ec-b7-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageOracleCertificationTests'
```

  Expected: missing internal padding overload.

- [ ] Implement the overload by threading a scalar padding through the private geometry-certification call to `Equipment`. Leave `CoverageFrames.TryPrepare` inputs and all command fields unchanged. In `Equipment`, set `threshold=(own ? frame.Clearance : Math.Max(frame.ConstructionErrorBound, frame.RenderErrorBound)) + worldClearancePadding`, and use that same value in broad-phase expansion, initial nearest bound and the exact lower-bound comparison. Add no padding by mutating `CoverageCommandPose.Clearance` or recomputing frames from a modified command.

  Source confirms why this seam is required. `CoverageFrames.Derive` uses `command.Clearance` for both `CapMarginUpper` and `CapMargin` at lines 240-248 and for admission at lines 260-264. Passing `commands with { Clearance = ... }` to the current public overload therefore changes more than the separation predicate. The internal scalar seam strengthens distance by `+epsilon` in world Euclidean units without changing caps, budget, topology, containment or any other predicate.

  A reference triangle is within `epsilon` of the corresponding device triangle when every corresponding vertex is within `epsilon`, by barycentric convexity. Distance to a fixed solid is 1-Lipschitz, so certifying `Clearance + epsilon` gives at least authored `Clearance` after a host-only displacement of at most `epsilon`. Task 6 sums the maximum host and full-equipment world radii, so epsilon includes both moving surfaces. Subtracting epsilon from the required clearance would weaken the claim and is forbidden. This argument concerns separation only, not automatic robustness of every other predicate to perturbation.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b7-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b7-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageOracleCertificationTests|FullyQualifiedName~CoverageCertificationTests|FullyQualifiedName~CoverageExactCaptureTests'
build-slot --label ec-b7-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageCertification.cs
build-slot --label ec-b7-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageOracleCertificationTests.cs
```

  Expected: zero warnings, strengthened-separation negatives pass, zero-padding equivalence passes, format exit 0.

- [ ] Commit listed paths with `render3d(coverage): add isolated oracle clearance padding`.

### Task 8: Publish the headless oracle with negatives, warm allocation proof and guide

**Files**

- Create: `KhaozEngine.Render3D/Coverage/CoverageOracle.cs`
- Create: `KhaozEngine.Render3D/Coverage/CoverageOracleResult.cs`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageFailure.cs`, append code 15 and extend its constructor guard
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferencePrecision.cs`, add tolerance acceptance
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageOracleTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageOracleAllocationTests.cs`
- Modify: `docs/USING-KHAOZENGINE.md:6363-6368`, add a headless oracle subsection beside optional coverage
- Modify: `KhaozEngine.Render3D/README.md:151-160`, link the headless oracle contract without changing the still-existing runtime description
- Read only: `Scene3D.Coverage.cs:81-86`, `Models/SkinningMath.cs:18-76`, `test-shared/AllocationMeasurement.cs`, `test-shared/AllocationRegion.cs`

**Interfaces**

- Consumes: Task 5 constructor, Task 6 pose bounds/epsilon, Task 7 padded exact validator and Task 2 budget composition.
- Produces: `public enum CoverageOracleStage { None = 0, Construction = 1, Certification = 2, DeviceTolerance = 3 }`.
- Produces: `public readonly record struct CoverageOracleFailure(CoverageOracleStage Stage, CoverageFailure Failure)`.
- Produces: `public readonly record struct CoverageOracleCertificate(bool FullCoreClearance, double MinimumCoreClearance, double DeviceTolerance, int DisplacedVertices)`.
- Produces: `public sealed class CoverageOracle`, constructor `(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> inverseBind, int commandCapacity = CoverageScratch.MaxCommands)`, `public CoverageRefinement Topology { get; }` and `public bool TryCheck(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, out CoverageOracleCertificate certificate, out CoverageOracleFailure failure)`.
- Produces internally: `internal ReadOnlySpan<ModelVertex> CoverageOracle.LastConstructedVertices`, empty after construction refusal, and `internal CoverageReferenceEnclosureScratch CoverageOracle.LastEnclosure` for tests and Part C. Owned scratch is invalidated on the next check and is never a public drawable surface. Clear active enclosure lengths even on an early refusal.
- Produces: `internal static bool CoverageReferencePrecision.TryAcceptTolerance(CoverageRefinement topology, double epsilon, ReadOnlySpan<CoverageCommandPose> commands, out CoverageFailure failure)`. This deterministic budget/code seam requires finite nonnegative epsilon within every budget, otherwise reports `DeviceToleranceExceeded` with the NC1 attribution. Capacity admission rejects empty commands before this seam.

- [ ] Write `Every_frozen_fixture_reference_passes_the_public_headless_oracle`, `Oracle_negatives_preserve_stage_code_command_and_count`, and `Refusal_does_not_poison_the_next_check`.

```csharp
Assert.True(oracle.TryCheck(palette, model, commands, out var certificate, out var failure));
Assert.Equal(default, failure);
Assert.Equal(commands.All(c => c.TransitionWeight == 1f), certificate.FullCoreClearance);
Assert.True(certificate.DeviceTolerance >= 0);
Assert.All(commands, c => Assert.True(certificate.DeviceTolerance <= .25 * (double)c.Clearance));
Assert.Equal(15, (int)CoverageFailureCode.DeviceToleranceExceeded);
Assert.Equal(3, (int)CoverageOracleStage.DeviceTolerance);
Assert.Equal(CoverageOracleStage.Construction, escaped.Stage);
Assert.Equal(CoverageFailureCode.CoreEscaped, escaped.Failure.Code);
Assert.Equal(CoverageOracleStage.Construction, budget.Stage);
Assert.Equal(CoverageFailureCode.RecessionExceeded, budget.Failure.Code);
Assert.Equal(CoverageOracleStage.Certification, overTight.Stage);
Assert.Equal(CoverageFailureCode.EquipmentIntersection, overTight.Failure.Code);
Assert.Equal(1u, overTight.Failure.CommandId);
Assert.True(overTight.Failure.Count > 0);
Assert.Equal(default, failedCertificate);
Assert.Equal(firstSuccessCertificate, successAfterFailureCertificate);
Assert.Equal(firstSuccessBits, successAfterFailureBits);
```

  Add `Tolerance_budget_equality_passes_and_next_double_fails`. With clearance `.03125f`, assert `TryAcceptTolerance(...,.0078125,...)` succeeds. `Math.BitIncrement(.0078125)`, negative epsilon, `NaN` and `PositiveInfinity` fail with code `DeviceToleranceExceeded`, first violating command in ascending ID order and zero count/limit. Preserve every previous failure numeric value, accept code `15` in `CoverageFailure` and throw for code `16`.

  Add `Excessive_device_enclosure_refuses_before_exact_certification`: use a finite world translation of `2^20`, clearance `.02f` and generous caps/budget. Assert float construction succeeds, computed epsilon exceeds `.25*(double).02f`, public failure is `DeviceTolerance/DeviceToleranceExceeded`, certificate is default and an internal test observer confirms exact validation did not run. Keep instrumentation internal and allocate its delegate outside warm measurements. Do not inject a fabricated accepted epsilon into the public oracle.

  Support escape uses the existing `.9f` support translation. Budget excess uses `.1f` at full and zero transition weights. Define the over-tight **separation** case independently of construction: retain a small host fixture with a protected collar edge whose known positive gap to the real solid is `.01f`, place every core vertex behind the sheet, and submit clearance `.02f` with caps `[-2,2]` and budget `.8f`. Point construction and normal guards must succeed, while whole-face exact separation must fail `EquipmentIntersection`. Pin the rigid solid and collar-edge coordinates in the test and assert its `.01f` separation with `CoverageTriangleGeometry.SeparationLowerBound` before invoking the oracle. Do not use `Clearance=1e-6f` as this test, since the existing exact frame-admission refusal could mask separation.

  Add `Oracle_skinning_matches_staging_for_nonidentity_inverse_binds`: use inverse binds translating X by `-.25f` and `+.125f`, joint matrices translating X by `+.25f` and `-.125f`, and independently compose and skin through `SkinningMath.Compose`/`SkinVertex`. Feed those direct posed vertices to `CoverageReferenceConstructor.TryConstruct` and assert its final bits equal `oracle.LastConstructedVertices`. Mutating the constructor's caller-owned inverse-bind array afterwards must not change results. The zero-weight bone case retains identity skinning. A palette length mismatch throws `ArgumentException` before construction.

  Add `Partial_weight_certificate_does_not_claim_core_clearance`: at weights `0f` and `.5f`, expect `FullCoreClearance=false` and `double.IsNaN(MinimumCoreClearance)`, while weight zero retains every unconformed reference byte. Such a result is never documented as proving full-weight clearance.

- [ ] Run to fail:

```bash
build-slot --label ec-b8-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageOracleTests|FullyQualifiedName~CoverageOracleAllocationTests'
```

  Expected: public oracle types are absent.

- [ ] Implement the public signatures. The constructor snapshots inverse binds, validates count `1..128`, validates all four source bone indices against that count, and allocates composed matrices, posed-source vertices, reference scratch, enclosure scratch, final vertices and independent `CoverageScratch` for exact certification. Validate finite affine inverse binds. Wrong capacities or invalid source-skin indices are argument errors at creation. The object is reusable, single-caller and not thread safe. It owns no GPU resource and needs no `IDisposable`.

  Each call resets its certificate, exposed candidate length and enclosure active lengths/maxima. Require one through `commandCapacity` commands, matching staging. An empty or oversized command span reports `Construction/CapacityExceeded`, with zero command IDs, the supplied count and the configured limit. Compose every palette entry with `SkinningMath.Compose(palette[i], inverseBind[i])`, then skin the topology's original `SkinVertices` through `SkinningMath.SkinVertex` against that composed prefix, exactly as staging does. Data failures in finite transforms, skinning or reference construction report `Construction` with the unchanged `CoverageFailure`. A successful construction calls Task 6 with the exact composed upload palette, model, original commands and reference output. Every CPU position must be contained. A containment defect makes tolerance unbounded and independently fails correctness tests. Run `TryAcceptTolerance` next. Failure reports `DeviceTolerance` with default certificate and never invokes exact validation. Only finite accepted epsilon reaches Task 7 with original commands, reference `Before` and reference final output. Its failure reports `Certification` and the exact certifier's complete failure record. Success maps the geometry certificate into `CoverageOracleCertificate` and includes the epsilon used. No render context, pass capture, Scene3D, GPU object or runtime fallback is created.

  Exact certification still performs its own frame preparation. Its precondition or uncertainty refusals therefore report `Certification/UnsupportedTransform`, rather than being hidden or replaced by a guessed construction code. The removed reference uncertainty refusal has an explicit test documenting this distinction.

- [ ] Write the allocation assertion in `[Collection("AllocSensitive")]`, whose definition disables parallelization. Prebuild the fixture, command arrays, accepted pose, escaped pose, budget-negative commands, over-tight fixture and excessive-device-tolerance pose. Warm each distinct success and refusal path once outside the measured region. Use `AllocationMeasurement.Measure` with a precreated delegate containing one success, the three original refusals, device-tolerance refusal and another success. Store booleans and failures in preallocated state and assert afterwards:

```csharp
Assert.Equal(0, allocatedBytes);
Assert.Equal(new[] { true, false, false, false, false, true }, results);
Assert.Equal(CoverageOracleStage.Construction, failures[1].Stage);
Assert.Equal(CoverageOracleStage.Construction, failures[2].Stage);
Assert.Equal(CoverageOracleStage.Certification, failures[3].Stage);
Assert.Equal(CoverageOracleStage.DeviceTolerance, failures[4].Stage);
Assert.Equal(CoverageFailureCode.DeviceToleranceExceeded, failures[4].Failure.Code);
```

  The check includes skinning, construction, full interval evaluation, equipment transforms, tolerance acceptance and exact validation, including refusal handling and recovery. No assertions, formatting, delegate creation, array allocation or LINQ runs inside the measured delegate. This is an allocation correctness test, not a timing pilot or a loop of suite runs.

- [ ] Document the constructor, copied inverse-bind ownership, exact `TryCheck` signature, stage/code/command/count reporting, zero warm allocations, non-thread-safe reuse and the distinction between a headless certificate and drawable permission. Include one complete headless example that builds a refinement, supplies the same palette and actual command frames as the game and fails CI on a false result. State the reserve, cited operation table, interval domains, host-plus-equipment epsilon and `DeviceToleranceExceeded` stage/code exactly, the `Clearance + epsilon` rule, the partial-transition limitation, and the R2 sampled-pose limitation. The game's sampler, blend grid and asset inventory remain Part D. The dev-only async device verification mode remains Part C.

  Run the full Markdown name sweep for `CoverageOracle`, `CoverageEvaluator`, `CoverageCertification`, `TryConstructCandidate` and the claim that every submitted pose is certified. Change only the two living references listed for this task. Report other runtime descriptions to root for the already-planned Part C API/doc sweep. Do not rewrite historical designs or preemptively describe the old runtime as removed.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b8-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b8-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageOracle|FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests|FullyQualifiedName~CoverageCertificationTests'
build-slot --label ec-b8-engine-format -- dotnet format whitespace KhaozEngine.Render3D/KhaozEngine.Render3D.csproj --verify-no-changes --no-restore --include Coverage/CoverageOracle.cs Coverage/CoverageOracleResult.cs Coverage/CoverageFailure.cs Coverage/CoverageReferencePrecision.cs
build-slot --label ec-b8-test-format -- dotnet format whitespace KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --include Render3D/CoverageOracleTests.cs Render3D/CoverageOracleAllocationTests.cs
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

  Expected: zero-warning Release build, every selected correctness and allocation test passes, both format checks and all guards exit 0. No native backend or measurement claim is made by this headless slice. Root receives the commit, case counts, zero-byte result and any remaining numerical-contract objection.

- [ ] Commit listed paths with `render3d(coverage): expose the headless reference oracle`.

## Self-review

- [x] Spec coverage: Task 1 is the unchanged 72-case capture-first commit. Tasks 2 to 5 own scalar frames, reserve, interpolation, conformation, guards, correction and normals. Tasks 6 to 8 own enclosure APIs, exact padding and the public headless oracle. GPU implementation and game sampling remain Parts C and D.
- [x] NC1: fixed rho, gamma bounds, coordinate-only epsilon and unspecified correctionScale are removed. Reserve, cited k_op table, outward operations, input/output flushing, domains, branch joins, world/equipment radii, `+epsilon` and code 15 have owning tests.
- [x] Honest limits: RV1 is resolved by root from the Vulkan IEEE 754 zero and flushing rules, so square root over `[0,h]` encloses finitely. RV2 names strict lowering and confirmation of the conservative finite-domain derivations. Neither returns to fixed-rho design. All-fixture finite enclosures and quarter-clearance fidelity, measured like with like at the capture's effective clearance, remain acceptance assertions, not guarantees inferred from the reserve. The reserve's extra recession has its own mapped bound.
- [x] Step scan: every task has Files, Interfaces, failing assertions, red invocation, implementation signatures/formulas, zero-warning Release build, focused green invocation, scoped format and commit subject. Every local build/test uses build-slot. No loops, timings, native implementation or load are authorized.
- [x] Types: Task 6 consumes Task 2 interval/precision types and Tasks 3 to 5's graph. Task 8 consumes Task 6 bounds and Task 7 padding. Stage 3 and failure 15 are append-only. Cold scratch allocation and full resets cover warm reuse.
- [x] Review Focus: 1 has Task 2/6 conditioning/domain tests, 2 has Task 1/4/5/6 seam tests, 3 has Task 3/4/6/8 transition/selection tests, 4 has Task 6/8 state/equipment/order tests, 5 has Task 5/7/8 fidelity/padding/threshold tests.
- [x] Proportion: one extra task owns the independently reviewable interval evaluator. Numerical formulas and source qualifications specify choices the implementer cannot invent. No production method bodies, unrelated fixes, GPU work or release ritual were added.
- [x] Planning validation was source/spec reading and inspection of this plan. No builds, tests, dotnet, benchmarks, load, commits, branch changes, pushes, issues or network writes ran. Official specifications were read over the network. The Metal PDF was extracted in memory without saving another file.
