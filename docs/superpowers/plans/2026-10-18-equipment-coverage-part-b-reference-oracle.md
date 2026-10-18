# Equipment coverage Part B: CPU float reference and exact oracle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the exact construction evidence and provide a headless float reference and exact coverage oracle for asset CI and the Part C kernels.

**Architecture:** Keep the current exact constructor and capture its results before introducing a separate reference implementation. The reference owns immutable gather tables and bounded scratch. Owner ruling NC2 makes every GPU coverage kernel reproduce the reference bit for bit, so the device tolerance is zero. Every device-reproduced binary32 operation goes through one arithmetic home that also records the first subnormal operand or result, and a compile-time analyzer holds the reference to the pinned operation inventory. The public oracle skins with a pinned reference skinning, constructs with the reference, fails a sample at a device-exactness stage when any reference operand or result is subnormal, and otherwise certifies the reference output with the existing exact geometric predicates at the approved clearance. NC1's interval enclosure was built in Task 6, failed its fixture gate and is superseded.

**Tech Stack:** C# on .NET 10, System.Numerics value types as storage, xUnit, a Roslyn analyzer in the existing `KhaozEngine.CodeHealth.Analyzers`, existing expansion geometry predicates, no new dependencies.

**Spec:** docs/design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md

## Global Constraints

- "It is test and oracle infrastructure only, never a runtime path."
- "Kernels gather and never scatter floats."
- "Warm checks allocate no managed memory."
- "Each reference position stays within `Clearance x MarginFraction` of the exact capture."
- "A core-corrected group that exceeds it is investigated, never accepted by widening the bound."
- "Refusal codes match on the existing construction negatives."
- "A breach fails, and no bound is widened to pass."
- Reserve `delta = Clearance * .25f` in construction, as margin against a driver that breaks its precision rules (NC2). Device tolerance is zero. No interval enclosure, world epsilon, clearance padding or tolerance budget survives, and the oracle certifies at the approved `Clearance`.
- NC2 operation inventory for every device-reproduced value: binary32 add, subtract, multiply, divide and square root, the B27 exact predicate with the B30 crossing parameter, compare (including the finiteness test), select, abs, negate, min, max, clamp and binary32 store, in the pinned order of Tasks 2 to 5. CPU-side uniforms and static data are computed once on the CPU with scalar IEEE arithmetic and uploaded as binary32 or integers.
- "The oracle therefore fails a sample with a device-exactness code if any reference operand or result in it is subnormal."
- "Measurements and any release remain unauthorized until their own approvals."
- "The 2 ms for 32 typical actors CPU gate, the 15 percent native GPU gate and the zero-allocation gate are unchanged."
- `CoverageFrames.MarginFraction = .25`, `CoverageScratch.MaxCommands = 4`, support limits are 1,024 inward triangles and 256 boundary edges, and host limits are 65,536 vertices and 65,536 triangles.
- "Warnings are errors. Fix them at the source. Do not add blanket suppressions or disable the rule."
- "New behavior gets a headless test in the matching area test project."
- "Commit subjects use `area(scope): summary`."
- Implement later in `/Users/antonio/KhaozEngine/.worktrees/ec-oracle`, branch `feature/equipment-coverage-oracle`, created from `feature/equipment-coverage` containing `c525aa2bb52a75db2b4b9c0d17a31638b7938699`.
- Part A owns `KhaozEngine.Gpu*`. This plan changes none of those projects, no shaders, no Scene3D staging or drawing, and deletes no exact constructor or render certifier. Outside `KhaozEngine.Render3D` and `KhaozEngine.Render.Tests` it changes only `KhaozEngine.CodeHealth.Analyzers` and its tests (Task 6B) and adds one design document (Task 6D).
- Do not merge main, bump versions, pack, release, run pilots, launch clients or integrate either parallel branch in these tasks. The root orchestrator owns the later batch finish.

## Review Focus

> Amended 2026-10-18 for owner ruling NC2. Items 1 to 5 originally named the NC1 enclosure in Tasks 6 and 7.

1. Skinny support queries, near-zero correction divisors and overflow keep their construction guard codes. A subnormal operand or result anywhere in skinning or construction becomes a device-exactness failure at its first site, never a silent pass. Tasks 2, 6C and 8 test these.
2. Seams in opposite triangle order retain welded position bits, smooth normal gathers and separate hard-normal/tangent neighborhoods. Tasks 1, 4 and 5 test these, and Task 6A freezes their reference bytes.
3. Zero transition still checks the full target and preserves every original byte when valid. Tasks 3, 4 and 8 test this.
4. Refusal then success, reordered commands or a numerically worse equipment transform must not reuse candidates, subnormal traces or certificates. Tasks 6C and 8 test reset and order independence.
5. The reserve consumes the fidelity budget, never enlarges it. Task 5 retains exact quarter-clearance fidelity. Task 8 certifies at the approved clearance with no padding.
6. Bit exactness comes from the operation inventory and order, not from tolerances. Task 6A freezes 144 reference outputs, Task 6B rejects out-of-inventory code at compile time, Task 6C reroutes the device path with byte-identical outputs, and Task 6D states the contract Part C kernels meet.

## Numerical contract: NC1 reserve and fidelity, NC2 device exactness

> Amended 2026-10-19 for root ruling B38. Six of the 144 fixture constructions produce subnormal intermediates, so the subnormal rejection in this section, in Global Constraints and Review Focus item 1, and in the file table's `CoverageFailure.cs` row is superseded. The reference and every kernel flush each operand and result of magnitude at most `2^-126` to a zero of the same sign, uploaded inputs included, so behaviour is independent of hardware denormal mode and no sample is rejected for subnormals. There is no `SubnormalArithmetic` code and no `DeviceExactness` stage. The arithmetic home's trace is a diagnostic flush count.

The owner resolution in Part B item 3, Part C step 3 and evidence item 5 replaces fixed rho and fixed epsilon. Task 1 is unchanged. No constructor rounding bias, operation-count gamma bound or fixed world tolerance survives. NC1's reserve and fidelity rules below still stand. Its tolerance half was replaced on 2026-10-18 by owner ruling NC2, described after them.

### Reserve and fidelity

Use scalar binary32 in this order, with Task 2's slope and inverse-map ordering:

```text
delta = Clearance * .25f
ReservedClearance = Clearance + delta
ClearanceCoverage = (ReservedClearance * sqrt(1f + S*S)) * SpectralUpper(inverse(C))
ActualClearanceCoverage = (ReservedClearance * sqrt(1f + S*S)) * SpectralUpper(inverse(E) * P)
target = sheetHeight - ClearanceCoverage
```

Core correction also uses the reserved `ActualClearanceCoverage`. Authored `Clearance`, cap margins and recession caps do not change. Full-target guards precede transition. There is no extra bias on point recession, correction or their sum, and no guessed constructor conversion allowance. Under NC2 a conforming device reproduces these values exactly, so the reserve is margin, not a tolerance.

For the same active branch before correction, write `M = sqrt(1+S*S)*SpectralUpper(inverse(C))`, `W = length(A.linear(D))` and `alpha = strength*spatial*TransitionWeight`. Ignoring arithmetic differences only for this comparison, the pointwise difference from capture is `alpha*abs(delta*M-exactSlackCoverage)*W`. The capture's world slack is between zero and delta. The new reserve can therefore spend the entire fidelity budget even in a unit flat frame. Slope and anisotropic transforms can give `M*W > 1`. Float interpolation, branch changes and core correction add differences. This is not a proof of fidelity or spare roundoff headroom.

**Root ruling: compare like with like.** Fidelity measures float-versus-exact construction differences, so it runs the reference at the capture's own effective clearance. An internal test seam `CoverageReferenceConstructor.TryConstruct(..., ReadOnlySpan<double> clearanceCoverageOverride)` replaces each active slot's target clearance with `capture.ClearanceCoverage[slot] + capture.SlackCoverage[slot]`, the exact target depth, and changes nothing else. Production and oracle calls pass an empty span and use the reserve. In that like-for-like mode the assertion is `worldDistance(reference,frozenExact) <= .25*(double)Clearance` for every generated position and every corrected group. Report fixture, group, transition, capture slack and correction on failure. Do not add epsilon, float fudge or a normal tolerance, recapture or omit a row. A miss returns to root with evidence.

The reserve's deliberate extra recession is checked separately. For every conform-only group, `worldDistance(reserveRun,likeForLikeRun) <= up(alpha*abs(delta*M-capture.SlackCoverage[slot])*W)` with `M` and `W` from the same frame, plus the like-for-like bound. Core-corrected groups report this difference without asserting it, because correction depends nonlinearly on the target. This is the owner-accepted implication that skin may sit up to a quarter clearance deeper, measured perpendicular to the sheet.

### Device exactness NC2

> Superseded on 2026-10-18 by owner ruling NC2: the k_op table and its derivations, RV1, RV2, outward interval evaluation, epsilon composition and refusal, `CoverageFloatInterval` and `CoverageFloatIntervals` (B15), `CoverageVertexEnclosure`, `CoverageReferencePrecision`, `DeviceToleranceExceeded`, `CoverageOracleStage.DeviceTolerance` and the clearance-padding seam. The Task 6 enclosure was built and failed on real fixtures: 11 of 72 rows were unbounded, two finite rows were far above the reserve, and one vertex the reference moved by 4e-9 m received a 2.8 cm box. Intervals lose correlations, and branch joins hull covered and uncovered outcomes. The draft and its evidence are archived at `fdf86dedb` on `feature/equipment-coverage-enclosure-draft`, never to be merged. NC2 was chosen over a measured tolerance and a correlated enclosure.

Every coverage kernel computes the same binary32 bits as the reference, so the device tolerance is zero (spec Part B item 3). Every value is a function of the uploaded binary32 and integer inputs and the pinned operation order. Add, subtract and multiply are correctly rounded on all three backends in precise mode. Division and square root are correctly rounded in the reference, and a backend whose native operation is not correctly rounded corrects its result with an exact residual check. Exact predicates follow B27, and the crossing parameter follows B30. Subnormals are the one place conforming backends may differ, so the oracle fails a sample at its device-exactness stage when any reference operand or result in it is a nonzero binary32 of magnitude at most `2^-126` (root ruling B32: a result equal to the smallest normal may have rounded up from a tiny value, and some hardware detects tininess before rounding). The reserve stays as margin against a driver that breaks its precision rules.

Precision sources, retained because they decide where Part C needs the division and square-root correction:

- [Vulkan specification, Vulkan Environment for SPIR-V](https://docs.vulkan.org/spec/latest/appendices/spirvenv.html), **Precision and Operation of SPIR-V Instructions**, **Evaluation of Expressions**, **Precision of Individual Operations**, **ULP**, **Inherited From**, Table 3 **Precision of Core SPIR-V Instructions** and Table 4 **Precision of GLSL.std.450 Instructions**.
- [Metal Shading Language Specification 4.1, 2026-06-04](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf), **8.1 INF, NaN, and Denormalized Numbers**, **8.2 Rounding Mode**, **8.4 ULPs and Relative Error**, Table 8.1 **Accuracy of single-precision floating-point operations and functions** (pages 368 to 370) and **8.5 Edge Case Behavior in Flush to Zero Mode**. Fast maths is off, never use Table 8.2.
- [Direct3D 11.3 Functional Specification](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm), **3.1.2 Term: Unit-Last-Place (ULP)**, **3.1.3.1 Partial Listing of Honored IEEE-754 Rules** and **3.1.3.2 Complete Listing of Deviations or Additional Requirements vs. IEEE-754**. **22.10.18 rcp** and **22.10.19 rsq** are separately relaxed instructions.

| Operation | Vulkan | Metal with fast maths off | D3D11.3 | NC2 kernel rule |
| --- | --- | --- | --- | --- |
| Add, subtract, multiply | Correctly rounded | Correctly rounded | Correctly rounded (.5 ULP) | Native, precise mode, no contraction |
| Divide | 2.5 ULP for divisor magnitude `[2^-126,2^126]` | Correctly rounded | Reciprocal 1 ULP then multiply, or better | Native, then exact residual correction where not correctly rounded |
| Square root | Inherited from `1/inversesqrt(x)` | Correctly rounded | 1 ULP | Native, then exact residual correction where not correctly rounded |
| Exact 2D predicate | Not native | Not native | Not native | B27 sign and RN32 value by a method Part C proves exact |
| Compare, select, abs, negate, min, max, clamp | Exact on finite operands | Exact on finite operands | Exact on finite operands | Pinned signed-zero and selection semantics in Task 6D |
| Subnormal operand or result | May flush | May flush | Flushes | The oracle fails the sample, kernels need not agree |

The oracle failure surface reuses the two slots the plan once gave NC1, which were never implemented: `CoverageFailureCode.SubnormalArithmetic = 15` and `CoverageOracleStage.DeviceExactness = 3`. Stages run in the order construction, device exactness, certification, independent of their numbering. A construction refusal reports `Construction` even when a subnormal event preceded it, because the sample fails either way and the guard code is the useful fact. Device exactness reports its first event's phase, element, operation and value bits, the attributed command and the total event count. Certification runs only on a successful construction with no event, at the approved clearance, through the unchanged public `CoverageCertification.ValidateGeometry`.

Part B implements no GPU, shader, readback or Scene3D work. Task 6D writes the kernel contract Part C implements and proves on all three backends.

## File structure

> Amended 2026-10-18 for NC2. The interval, precision and enclosure rows are withdrawn. Task 2 landed the interval types in `CoverageFloatIntervals.cs` (B15) and Task 6A deletes it with `CoverageReferencePrecision.cs`. Task 4 landed the B27 predicates in `CoverageReferencePredicates.cs`.

All new engine files live in `KhaozEngine.Render3D/Coverage/` and use namespace `KhaozEngine.Render3D`. New tests use `KhaozEngine.Tests.Render3D`.

| File | Responsibility |
| --- | --- |
| `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCases.cs` | Cold, headless fixture adapters and the 72-case capture manifest |
| `KhaozEngine.Render.Tests/Render3D/CoverageExactCaptureTests.cs` | One opt-in capture writer and unconditional exact replay tests |
| `KhaozEngine.Render.Tests/Fixtures/coverage-exact-v1/*.json` | Canonical input and output float bits, topology identity and refusal evidence |
| `KhaozEngine.Render.Tests/Fixtures/coverage-reference-v1/outputs.sha256` | SHA-256 of all 144 reference outputs, 72 cases in production and like-for-like modes |
| `CoverageReferenceTopology.cs` | Canonical position recipes, binary32 recipe weights and sorted compact sparse-row gather tables |
| `CoverageReferenceSupport.cs` | Float sheet and boundary data with deterministic lookup and overlay primitives |
| `CoverageReferencePredicates.cs` | B27 exact signs and RN32 values, B30 scaled crossing sides |
| `CoverageReferenceArithmetic.cs` | The one home of every device-reproduced binary32 operation, with the per-construction subnormal trace |
| `Binary32KernelAttributes.cs` | `Binary32KernelAttribute` and `Binary32ArithmeticAttribute`, read by the analyzer by name |
| `CoverageReferenceMath.cs` | Composite scalar helpers (dot, cross, transforms, heights) written through the home |
| `CoverageReferenceSkinning.cs` | CPU palette composition and the pinned reference skinning the oracle and Part C kernels share |
| `CoverageExactnessSite.cs` | Phase, operation and site of the first subnormal event |
| `CoverageReferenceFrames.cs` | Command admission and CPU-composed frames without construction intervals or slack |
| `CoverageReferenceScratch.cs` | Fixed per-call arrays for interpolation, construction, reductions, normals and the arithmetic trace |
| `CoverageReferenceConstructor.cs` | Interpolation, point conformation, guard reduction and atomic output publication |
| `CoverageReferenceCorrection.cs` | Float face-overlay deficits and group-owned correction and transition |
| `CoverageReferenceNormals.cs` | Per-vertex normal and tangent gather with folded-patch guard |
| `CoverageOracle.cs` | Public headless owner of reference skinning, reference scratch and exact-certifier scratch |
| `CoverageOracleResult.cs` | Public stage, failure and certificate value types |
| `CoverageFailure.cs` | Append-only `SubnormalArithmetic` code and accepted-code range |
| `KhaozEngine.CodeHealth.Analyzers/Binary32Kernel*.cs` | Compile-time operation-inventory rules for attributed kernel members |
| `docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md` | The Part C kernel contract: inventory, order, correction, predicates and subnormal rule |
| `KhaozEngine.Render.Tests/Render3D/CoverageReference*Tests.cs` | Separate arithmetic, construction, correction, normal, skinning and output-manifest test cycles |
| `KhaozEngine.Render.Tests/Render3D/CoverageOracle*Tests.cs` | Public API, stages, negatives, reuse and allocation tests |

Existing engine changes are limited to exposing the immutable canonical position recipes in `CoverageRefinement.cs`, attaching cold float data in `CoverageSupportSurface.cs`, the B6, B14 and B17 exact-path refactors already landed in Tasks 1 to 3, marking the `CoverageTally` and `CoverageFrames.Refuse` members the reference calls as kernel members in Task 6C, and appending the failure code in `CoverageFailure.cs`. NC2 withdraws the internal threshold-padding overload, so `CoverageCertification.cs` is unchanged. Existing exact construction behavior remains unchanged.

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

> Note 2026-10-18, execution rulings. B13: the project-relative `--include` paths above match nothing, so that check silently passes. From Task 2 on, every task runs `dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include <repo-relative paths>` from the worktree root, and Tasks 6A to 8 below are written in that form. B26: no brief uses `git stash`. A red run that needs a temporary change saves the hunk as a patch file in the ledger directory and reverses it with `git apply -R`.

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

> Note 2026-10-18 (NC2): Task 2 landed at `7cdb8b747` with the interval types in `CoverageFloatIntervals.cs` (B15). Its interval, precision and enclosure items are superseded and Task 6A deletes them: `CoverageFloatInterval`, `CoverageFloatIntervals`, `CoverageVertexEnclosure`, `CoverageReferencePrecision`, the k_op constants, RV1, `Interval_operations_pin_ulps_domains_and_input_flushing`, the interval half of `Coordinate_only_roundoff_does_not_bound_skinny_triangle_height`, and the enclosure-envelope and RV2 sentences of the arithmetic contract step. RV2's lowering obligation moves to the Task 6D contract. Frame values are binary32 uniforms, not point intervals. The scalar formulas, frames, support queries and gather tables stand, with B16 and B27 applied. Task 6C routes their device-reproduced operations through the arithmetic home without changing a bit.

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

> Note 2026-10-18 (NC2): the sentences giving rounding, stores, the deficit and the rate division to a Task 6 enclosure, `+epsilon` to Task 7 and the `CorrectionRate=2^-20` case to an amplified interval are superseded. A conforming device reproduces these values exactly, and Task 8 certifies the reference output at the approved clearance. The `2^-20` case stays a construction test. B22 and B24 were superseded by B27 during execution, and B30 later replaced the crossing quotient in Task 6A.

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

### Task 6: Device-exact reference contract (NC2)

> Replaced 2026-10-18 by owner ruling NC2. The original Task 6 built per-pose interval enclosures and failed its fixture gate (see the NC2 note in the numerical contract). It is now four bounded tasks, 6A to 6D, each with its own review and commit. They run in order, because 6A's hash manifest guards 6C and 6B's analyzer enforces 6C.

**Root precondition, before 6A is dispatched.** The worktree still holds the blocked enclosure draft: modified `KhaozEngine.Render3D/Coverage/CoverageReferencePredicates.cs` and `CoverageReferenceSupport.cs`, untracked `CoverageEnclosureCorrection.cs`, `CoverageEnclosureMath.cs`, `CoverageEnclosureNormals.cs`, `CoverageEnclosureOverlay.cs`, `CoverageEnclosurePose.cs`, `CoverageEnclosurePredicates.cs`, `CoverageEnclosureSupport.cs`, `CoverageReferenceEnclosure.cs` and `CoverageReferenceEnclosureScratch.cs` in the same directory, and untracked `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCrossingTests.cs`, `CoverageReferenceEnclosureDiagnosticTests.cs` and `CoverageReferenceEnclosureTests.cs`. All of it is archived at `fdf86dedb`. Root confirms the archive holds each path byte for byte, removes these paths, keeps the untracked `.superpowers/` ledger, and records the clean `8765a4c27` state in the ledger. The 6A worker reads the draft only through `git show fdf86dedb:<path>`. Every task in this section adds `test -z "$(git status --porcelain -- KhaozEngine.Render3D KhaozEngine.Render.Tests KhaozEngine.CodeHealth.Analyzers KhaozEngine.CodeHealth.Analyzers.Tests docs)"` to the starting guard.

### Task 6A: Port B30, freeze the reference outputs and delete NC1 interval code

**Files**

- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferencePredicates.cs`, port `EdgeExpansion` and `CrossingParameter` from `fdf86dedb`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageReferenceSupport.cs:164-171`, the strictly opposite-sign branch calls `CrossingParameter`
- Delete: `KhaozEngine.Render3D/Coverage/CoverageFloatIntervals.cs`, `KhaozEngine.Render3D/Coverage/CoverageReferencePrecision.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/CoverageReferencePrimitiveTests.cs`, delete the interval and precision tests listed below
- Create: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCrossingTests.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceOutputTests.cs`
- Create: `KhaozEngine.Render.Tests/Fixtures/coverage-reference-v1/outputs.sha256`
- Modify: `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj:30`, add `<None Update="Fixtures/coverage-reference-v1/*.sha256" CopyToOutputDirectory="PreserveNewest" />` beside the exact-capture item
- Read only: `git show fdf86dedb:KhaozEngine.Render3D/Coverage/CoverageReferencePredicates.cs`, the same for `CoverageReferenceSupport.cs` and `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCrossingTests.cs`, and the ledger's `task-6r1-before-manifest.log`

**Interfaces**

- Produces: `internal static float CoverageReferencePredicates.CrossingParameter(Vector3 s, Vector3 t, Vector3 a, Vector3 b)`. It returns `RN32(ca*2^k) / (RN32(ca*2^k) - RN32(cb*2^k))` with `k` the common power of two that puts `max(|ca|,|cb|)*2^k` in `[1,2)`, for the exact sides `ca = Edge(s,t,a)` and `cb = Edge(s,t,b)`. Nonfinite inputs, a zero side or equal signs return `NaN`. The caller still clamps to `[0,1]` as before. Task 6C splits its final subtract and divide into the arithmetic home without changing bits.
- Produces: `internal static class CoverageReferenceOutputs` inside `CoverageReferenceOutputTests.cs`, with `internal static string Hash(ReadOnlySpan<ModelVertex> vertices)` and `internal static IEnumerable<(string Name, string Hash, CoverageFailure Failure)> ComputeAll()`. `Hash` is lowercase hexadecimal SHA-256 over every `CoverageReferenceBits.Fields(vertex)` value written by `BinaryWriter.Write(float)`, which is little-endian on every platform, in vertex then field order. `ComputeAll` visits `CoverageReferenceCases.All()` in manifest order and two modes, named `<case>-0.bin` for production (reserve, empty overrides) and `<case>-1.bin` for like-for-like (`ClearanceCoverage + SlackCoverage`, `CorrectionClearance`, `CorrectionConversion` from the capture). These are the ledger's draft names, so the hashes compare directly.

- [ ] Before any source change, write the manifest and `Every_fixture_reference_output_matches_the_hash_manifest`. The writer is one explicitly filtered `[Fact]`, `Write_reference_output_manifest_when_explicitly_requested`, enabled only by `KE_WRITE_COVERAGE_REFERENCE_HASHES=1`, which writes `outputs.sha256` as 144 lines of `<hash> <name>` in `ComputeAll` order. Run it once on the unchanged `8765a4c27` code:

```bash
KE_WRITE_COVERAGE_REFERENCE_HASHES=1 build-slot --label ec-b6a-manifest -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~Write_reference_output_manifest_when_explicitly_requested'
```

  Compare the written file line for line with the ledger's `task-6r1-before-manifest.log`, which the blocked draft produced from the same code. Any difference stops the task and returns both files to root. The unconditional test asserts:

```csharp
Assert.Equal(144, expected.Length);
Assert.Equal(expected.Select(e => e.Name), actual.Select(a => a.Name));
Assert.All(actual, a => Assert.True(a.Failure.IsSuccess, a.Name));
Assert.Equal(expected.Select(e => e.Hash), actual.Select(a => a.Hash)); // report the first differing name
```

  Also add `Reference_output_writer_is_disabled_without_explicit_environment_gate`, asserting an ordinary run writes nothing.

- [ ] Write the crossing tests. Port `Subnormal_opposite_determinants_use_the_scaled_rational_parameter` from the draft unchanged, with its `-74` and `-75` rows and the independent rational bits `0x3eaaaaab` (one third) and `0x3f2aaaab` (two thirds). Add `Crossing_parameter_equals_the_unscaled_quotient_for_normal_sides`: for four normal opposite-sign side pairs, including one whose ratio crosses a binade, assert `CrossingParameter` bits equal `ca.Value / (ca.Value - cb.Value)` computed from `CoverageReferencePredicates.Edge`. Add `Crossing_parameter_rejects_zero_and_same_sign_sides`: zero, equal-sign and nonfinite inputs return `NaN`. Do not port the draft's environment-gated snapshot fact, which the manifest replaces.

- [ ] Run to fail:

```bash
build-slot --label ec-b6a-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceCrossingTests|FullyQualifiedName~CoverageReferenceOutputTests'
```

  Expected: compile failure, `CrossingParameter` is missing. The manifest test alone passes on the unchanged code.

- [ ] Port the B30 change exactly as the draft diff `git diff 8765a4c27 fdf86dedb -- KhaozEngine.Render3D/Coverage/CoverageReferencePredicates.cs KhaozEngine.Render3D/Coverage/CoverageReferenceSupport.cs` shows: extract `EdgeExpansion`, add `CrossingParameter` with its exponent search on the exact expansions, and replace the overlay's local quotient. Keep the non-finite quotient refusal.

- [ ] Delete `CoverageFloatIntervals.cs` and `CoverageReferencePrecision.cs`. Their only users at `8765a4c27` are each other and `CoverageReferencePrimitiveTests.cs` (verified with `git grep -n -E "CoverageFloatInterval|CoverageReferencePrecision|CoverageVertexEnclosure|CoverageIntervalTruth" -- '*.cs'`, which must print nothing afterwards). In that test file delete `Interval_operations_pin_ulps_domains_and_input_flushing`, `Arithmetic_endpoints_bracket_independent_binary64_results`, `Square_root_enclosure_at_zero_and_subnormals_is_finite`, `Division_contains_independent_reciprocal_multiply_expansion`, `Square_root_contains_independent_symmetric_inherited_expansion`, `Radius_composes_host_and_equipment_error_outward`, the `I`, `F` and `P` aliases and the interval half of `Coordinate_only_roundoff_does_not_bound_skinny_triangle_height`. That test keeps its scalar assertions (`Height` at `p` and `q` is `0` and `.5`, `Edge(a,b,c).Value` is `2^-23`) and becomes `Skinny_triangle_heights_differ_by_half_between_adjacent_queries`. Every other primitive test stays.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b6a-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b6a-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests'
build-slot --label ec-b6a-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include KhaozEngine.Render3D/Coverage/CoverageReferencePredicates.cs KhaozEngine.Render3D/Coverage/CoverageReferenceSupport.cs KhaozEngine.Render.Tests/Render3D/CoverageReferencePrimitiveTests.cs KhaozEngine.Render.Tests/Render3D/CoverageReferenceCrossingTests.cs KhaozEngine.Render.Tests/Render3D/CoverageReferenceOutputTests.cs
```

  Expected: zero-warning build, all 144 manifest rows unchanged after B30, crossing tests and every earlier reference test pass, format exit 0. A changed hash blocks the task and returns its name and first differing vertex to root. Never rewrite the manifest to pass.

- [ ] Commit the listed paths, including both deletions, with `render3d(coverage): scale reference crossing sides and freeze reference outputs`.

### Task 6B: Enforce the binary32 operation inventory at compile time

**Files**

- Create: `KhaozEngine.CodeHealth.Analyzers/Binary32KernelAnalyzer.cs`
- Create: `KhaozEngine.CodeHealth.Analyzers/Binary32KernelDiagnostics.cs`
- Modify: `KhaozEngine.CodeHealth.Analyzers/README.md`, add the four diagnostics and the attribute contract
- Modify: `KhaozEngine.CodeHealth.Analyzers.Tests/AnalyzerHarness.cs:31-53`, add an analyzer-agnostic runner and keep `Run` unchanged
- Create: `KhaozEngine.CodeHealth.Analyzers.Tests/Binary32KernelAnalyzerTests.cs`
- Read only: `FileSizeAnalyzer.cs`, `FileSizeDiagnostics.cs`, `Directory.Build.props:55-66`

The analyzer already runs in every engine project through `Directory.Build.props`, so these rules apply to Render3D without a project change. They fire only where the attributes are used, so no other project or consumer changes behaviour.

**Interfaces**

- Produces: `public sealed class Binary32KernelAnalyzer : DiagnosticAnalyzer` and `public static class Binary32KernelDiagnostics` with `KEB32001` to `KEB32004`, category `KhaozEngine.CodeHealth`, severity Error, each message carrying its own remediation, as `FileSizeDiagnostics` does.
- Produces: `public static Task<ImmutableArray<Diagnostic>> AnalyzerHarness.RunAnalyzer(DiagnosticAnalyzer analyzer, string idPrefix, params (string Path, string Content)[] sources)`.
- Contract: a kernel member is a method, constructor, accessor, local function or lambda whose own symbol or any containing method or type carries an attribute whose simple name is `Binary32KernelAttribute`, in any namespace, like the compiler's `IsExternalInit` pattern. A home type carries `Binary32ArithmeticAttribute` and is exempt from all four rules. It is the single hand-reviewed place where binary32 operations are written. A compilation with neither attribute name is skipped at compilation start.

Rules inside kernel members:

| Id | Fires on |
| --- | --- |
| KEB32001 | A binary, unary, increment or compound-assignment operator whose operand type is `float`, `double` or `decimal`, including comparisons and equality |
| KEB32002 | A non-constant numeric conversion whose source or target is `float`, `double` or `decimal`, implicit or explicit. Compile-time constant conversions such as `float x = 1` are allowed |
| KEB32003 | An invocation or user-defined operator of `System.Math` or `System.MathF` with a floating parameter, of any `System.Single`, `System.Double`, `System.BitConverter`, `System.Numerics` or `System.Runtime.Intrinsics` member, including `Vector3` and `Matrix4x4` operators |
| KEB32004 | An invocation of a method, or a reference to a non-auto property declared in source, that is neither a kernel member nor a home member. Allowed without marking: object creation, field and auto-property access, indexers of arrays, spans and `IReadOnlyList<T>`, and the copy and guard APIs `AsSpan`, `Slice`, `CopyTo`, `TryCopyTo`, `Clear`, `Fill`, `Overlaps`, `ReferenceEquals`, `ArgumentNullException.ThrowIfNull` and exception construction |

Integer arithmetic, integer conversions, `bool` logic and conditional selection of floats (`c ? a : b`, a select) stay legal. KEB32004 closes the call graph: a kernel member reaches float arithmetic only through the home or through another kernel member, which the same rules check.

- [ ] Write `Binary32KernelAnalyzerTests` with one source snippet per row, declaring both attributes in a test namespace. Concrete assertions:

```csharp
Assert.Equal(new[] { "KEB32001" }, Ids("[Binary32Kernel] static float F(float a, float b) => a + b;"));
Assert.Equal(new[] { "KEB32001" }, Ids("[Binary32Kernel] static bool F(float a) => a < 0;"));
Assert.Equal(new[] { "KEB32002" }, Ids("[Binary32Kernel] static float F(double a) => (float)a;"));
Assert.Equal(new[] { "KEB32002" }, Ids("[Binary32Kernel] static float F(int k) => Home.Div(k, 12f);"));
Assert.Empty(Ids("[Binary32Kernel] static float F() { float x = 1; return x; }"));
Assert.Equal(new[] { "KEB32003" }, Ids("[Binary32Kernel] static float F(float a) => MathF.Sqrt(a);"));
Assert.Equal(new[] { "KEB32003" }, Ids("[Binary32Kernel] static float F(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);"));
Assert.Equal(new[] { "KEB32003" }, Ids("[Binary32Kernel] static Vector3 F(Vector3 a, Vector3 b) => a - b;"));
Assert.Equal(new[] { "KEB32003" }, Ids("[Binary32Kernel] static bool F(float a) => float.IsFinite(a);"));
Assert.Equal(new[] { "KEB32004" }, Ids("static float G(float a) => a; [Binary32Kernel] static float F(float a) => G(a);"));
Assert.Empty(Ids("[Binary32Arithmetic] static class Home { internal static float Add(float a, float b) => a + b; }"
    + " [Binary32Kernel] static float F(float a) => Home.Add(a, a);"));
Assert.Empty(Ids("[Binary32Kernel] static float F(float a, bool c) => c ? a : 0f;"));
Assert.Empty(Ids("[Binary32Kernel] static int F(int i) => (i * 3) + Math.Max(i, 2);"));
Assert.Equal(new[] { "KEB32001" }, Ids("[Binary32Kernel] class K { float F(float a) { float L(float x) => x * x; return L(a); } }"));
Assert.Empty(Ids("static float F(float a) => a * a;")); // unattributed code is untouched
```

  Also assert each message names its rule and the remedy, and that a compilation without either attribute name produces no diagnostics and registers no operation actions.

- [ ] Run to fail:

```bash
build-slot --label ec-b6b-red -- dotnet test KhaozEngine.CodeHealth.Analyzers.Tests/KhaozEngine.CodeHealth.Analyzers.Tests.csproj -c Release --filter 'FullyQualifiedName~Binary32KernelAnalyzerTests'
```

  Expected: compile failure, the analyzer types are missing.

- [ ] Implement with `RegisterCompilationStartAction`, bailing out when `compilation.GetSymbolsWithName` finds neither attribute name, then `RegisterOperationAction` for binary, unary, increment, compound assignment, conversion, invocation and property reference. Resolve kernel membership by walking `ContainingSymbol` from the operation's owning symbol and cache it per symbol. Keep the analyzer free of state shared across compilations. Add the README table rows and the attribute contract paragraph. The help link points to `docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md`, written in Task 6D.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b6b-build -- dotnet build KhaozEngine.CodeHealth.Analyzers.Tests/KhaozEngine.CodeHealth.Analyzers.Tests.csproj -c Release
build-slot --label ec-b6b-tests -- dotnet test KhaozEngine.CodeHealth.Analyzers.Tests/KhaozEngine.CodeHealth.Analyzers.Tests.csproj -c Release --no-build
build-slot --label ec-b6b-render-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b6b-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include KhaozEngine.CodeHealth.Analyzers/Binary32KernelAnalyzer.cs KhaozEngine.CodeHealth.Analyzers/Binary32KernelDiagnostics.cs KhaozEngine.CodeHealth.Analyzers.Tests/AnalyzerHarness.cs KhaozEngine.CodeHealth.Analyzers.Tests/Binary32KernelAnalyzerTests.cs
```

  Expected: zero warnings, every analyzer test passes including the existing file-size tests, Render3D still builds with no new diagnostic (nothing is attributed yet), format exit 0.

- [ ] Commit the listed paths with `codehealth(analyzers): enforce the binary32 kernel operation inventory`.

### Task 6C: Route the device path through one arithmetic home and trace subnormals

**Files**

- Create: `KhaozEngine.Render3D/Coverage/Binary32KernelAttributes.cs`, internal `Binary32KernelAttribute` (class and method targets) and `Binary32ArithmeticAttribute` (class target)
- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceArithmetic.cs`
- Create: `KhaozEngine.Render3D/Coverage/CoverageExactnessSite.cs`, internal in this task, made public in Task 8
- Create: `KhaozEngine.Render3D/Coverage/CoverageReferenceSkinning.cs`
- Modify: `CoverageReferenceMath.cs`, `CoverageReferenceSupport.cs`, `CoverageReferencePredicates.cs`, `CoverageReferenceConstructor.cs`, `CoverageReferenceCorrection.cs`, `CoverageReferenceNormals.cs`, `CoverageReferenceTopology.cs`, `CoverageReferenceScratch.cs`, `CoverageReferenceFrames.cs` under `KhaozEngine.Render3D/Coverage/`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageEvaluator.cs:199`, mark `CoverageTally.Note` and both `Succeeded` overloads `[Binary32Kernel]`, no body change
- Modify: `KhaozEngine.Render3D/Coverage/CoverageCommandPose.cs:229-233`, mark both `CoverageFrames.Refuse` overloads `[Binary32Kernel]`, no body change
- Create: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceArithmeticTests.cs`, `KhaozEngine.Render.Tests/Render3D/CoverageReferenceSkinningTests.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/CoverageReferenceOutputTests.cs`, and every `CoverageReference*Tests.cs` call site of a changed internal signature, mechanically, each with a fresh `new CoverageReferenceArithmetic()`
- Read only: `Models/SkinningMath.cs:20-69`

**Operations found outside the inventory at `8765a4c27`, and their disposition**

| Site | Operation | Disposition |
| --- | --- | --- |
| `CoverageReferenceSupport.cs:225` (`Cell`, called at `:75`, `:207-208`) | float to int truncation, then integer clamp | Replace with a compare count: `x = ((value-min)/(max-min))*16f`, then `cell` is the number of `k` in `1f..15f` with `x >= k`, from a `static readonly float[]` of literals. Equal to the old cell for every input under .NET's saturating conversion, including `NaN` and both infinities |
| `CoverageReferenceConstructor.cs:202` | int to float (`numerator / 12f`) | Topology precomputes the canonical edge parameter `t` per position group on the CPU, as static data |
| `CoverageReferenceConstructor.cs:247-250` (`Sum`, `Value`, `Magnitude`) | int to float (`a * wa`, recipe and record weights) | Topology precomputes binary32 weights per position group and per refined vertex, as static data. Bits equal `(float)weight` |
| `CoverageReferenceConstructor.cs:106`, `CoverageReferenceCorrection.cs:32-33` | double to float (like-for-like overrides) | Test seam. `TryConstruct` converts the overrides to binary32 once, on the CPU, before any kernel phase. Kernel phases take `ReadOnlySpan<float>`. Production passes empty spans |
| `CoverageReferenceNormals.cs:33`, `:104-105`, `CoverageReferenceSupport.cs:27` | `System.Numerics` `Vector3` subtraction | `CoverageReferenceMath.Sub3`, componentwise through the home. Same bits, now in the pinned scalar graph |
| `CoverageReferenceConstructor.cs:55`, `:182`, `:218`, `:238`, `CoverageReferenceNormals.cs:55`, `:85`, `:147` | `Vector3` equality and inequality | `CoverageReferenceMath.Equal3`, componentwise IEEE equality through the home, so `-0` equals `+0` as before |
| `CoverageRefinement.Finite` and `float.IsFinite` throughout the five device-path files, and the non-auto `CoverageReferenceScalar.IsFinite` | finiteness classification | `IsFinite` in the home, an explicit inventory entry under compare. Kernels test exponent bits |
| `MathF.Max`, `MathF.Min`, `Math.Clamp`, `MathF.Abs`, `MathF.Sqrt` sites (`CoverageReferenceConstructor.cs:162`, `:166`, `:242`, `:250`, `CoverageReferenceCorrection.cs:53`, `:110`, `:113`, `:143`, `CoverageReferenceSupport.cs:109`, `:117`, `:198`, `:220-221`, `CoverageReferenceNormals.cs:35`, `:40`, `:111-112`, `:132`, `CoverageReferenceMath.cs:40`) | in the inventory, but .NET signed-zero semantics are not GPU semantics | Through the home, which keeps .NET semantics. Task 6D pins them for kernels |
| B30 `CrossingParameter` final `x - y` and `x / (x - y)` | binary32 device operations inside the predicate type | The predicate type returns the two scaled RN32 sides. The home subtracts and divides |
| `Models/SkinningMath.cs:20`, `:29-35`, `:52-55`, `:61-65`, which the old Task 8 used for oracle skinning | `Matrix4x4` and `Vector3` operators, `Transform`, `TransformNormal`, `Length`, float to int index conversion | Not a pinned order, and the .NET implementations are vectorized and can fuse multiply-add on FMA hardware. Replace for the oracle with `CoverageReferenceSkinning`. `SkinningMath` stays unchanged for uncovered skinning |

CPU-side values stay CPU-side and are documented in Task 6D: `CoverageReferenceFrames.Derive` and `SpectralUpper` (`CoverageReferenceFrames.cs:34-79`, double `CoverageAffine` composition rounded once, then binary32 frame scalars), the `CoverageReferenceSupport` constructor (`:11-52`, slope, grid bounds and binning), the rounded support positions (`CoverageSupportSurface.cs:35-36`) and the compile-time constant `1f / (1L << 50)` (`CoverageReferenceConstructor.cs:9`). Each is computed once per frame, equipment or host with scalar IEEE arithmetic, which RyuJIT does not contract, and uploaded as binary32. The B27 predicate's binary64 expansions (`CoverageReferencePredicates.cs:18-86`) are the exact-predicate inventory entry itself. No `FusedMultiplyAdd`, reciprocal or square-root estimate, transcendental function or other double arithmetic appears in the device path.

**Interfaces**

- Produces: `[Binary32Arithmetic] internal sealed class CoverageReferenceArithmetic` with `Reset()`, `At(CoverageExactnessPhase phase, int element, int slot = -1)`, `int EventCount`, `CoverageExactnessSite FirstSite`, `int FirstSlot`, binary32 `Add`, `Sub`, `Mul`, `Div`, `Sqrt`, `Abs`, `Neg`, `Min`, `Max`, `Clamp(float value, float min, float max)`, `bool` `Less`, `LessOrEqual`, `Greater`, `GreaterOrEqual`, `Equal`, `NotEqual`, `IsFinite`, and the predicate entries `CoverageReferenceScalar Edge(Vector3 a, Vector3 b, Vector3 p)`, `CoverageReferenceScalar Dot(Vector3 a, Vector3 b, Vector3 c, Vector3 d)` and `float CrossingParameter(Vector3 s, Vector3 t, Vector3 a, Vector3 b)`. Each method checks every binary32 operand and result it reads or produces (predicates read only X and Y). A value `x` is an event when `x != 0` and its exponent bits are zero. Every event increments `EventCount`, and the first also records the current site, operation and the event value's bits. Semantics equal C# binary32 operators, `MathF.Min`, `MathF.Max`, `Math.Clamp`, `MathF.Abs`, `MathF.Sqrt` and `float.IsFinite`, so no output bit changes. No allocation, no static mutable state.
- Produces: `internal enum CoverageExactnessPhase { None = 0, Skinning = 1, SourceValidation = 2, GroupInterpolation = 3, AttributeInterpolation = 4, Conformation = 5, FaceCorrection = 6, GroupCorrection = 7, FullTarget = 8, Transition = 9, FaceValidation = 10, NormalGather = 11 }`, `internal enum CoverageExactnessOperation { None = 0, Add = 1, Subtract = 2, Multiply = 3, Divide = 4, SquareRoot = 5, Abs = 6, Negate = 7, Min = 8, Max = 9, Clamp = 10, Compare = 11, FiniteTest = 12, Predicate = 13, CrossingSide = 14 }` and `internal readonly record struct CoverageExactnessSite(CoverageExactnessPhase Phase, int Element, CoverageExactnessOperation Operation, uint ValueBits)`. The element is the source vertex for `Skinning` and `SourceValidation`, the position group for `GroupInterpolation`, `GroupCorrection` and `Transition`, the refined vertex for `AttributeInterpolation`, `Conformation`, `FullTarget` and `NormalGather`, and the triangle for `FaceCorrection` and `FaceValidation`. The slot is the sorted command slot where the phase has one, otherwise `-1`.
- Produces: `CoverageReferenceScratch.Arithmetic` (the device trace) and `CoverageReferenceScratch.UniformArithmetic`, a second instance for CPU-side frame scalars whose trace nobody reads. `TryConstruct` calls `Arithmetic.Reset()` before frame preparation.
- Produces: `internal static bool CoverageReferencePredicates.TryScaledSides(Vector3 s, Vector3 t, Vector3 a, Vector3 b, out float x, out float y)`, replacing `CrossingParameter` there. The home's `CrossingParameter` returns `NaN` when it is false and otherwise `Div(x, Sub(x, y))`.
- Produces: `internal static Matrix4x4 CoverageReferenceSkinning.Compose(in Matrix4x4 jointWorld, in Matrix4x4 inverseBind)`, a CPU uniform with `M[i,j] = (((B[i,1]*J[1,j]) + (B[i,2]*J[2,j])) + (B[i,3]*J[3,j])) + (B[i,4]*J[4,j])`, where `B` is the inverse bind and `J` the joint world, the System.Numerics `inverseBind * jointWorld` meaning in scalar binary32 with no operator overloads.
- Produces: `internal sealed class CoverageReferenceSkin`, constructor `(CoverageSourceTopology source, int boneCount)`, which validates every vertex with `SkinningMath.AreBoneIndicesValid` and stores four integer bone indices per source vertex. Invalid indices throw `ArgumentException`.
- Produces: `[Binary32Kernel] internal static bool CoverageReferenceSkinning.TrySkin(CoverageReferenceArithmetic f, CoverageReferenceSkin skin, CoverageSourceTopology source, ReadOnlySpan<Matrix4x4> composed, Span<ModelVertex> posed, out CoverageFailureCode code)`. Pinned order per source vertex: `total = ((w.X + w.Y) + w.Z) + w.W`. If `total < 1e-8f` the skin is the identity, otherwise each of the twelve affine coefficients is `((B0*w.X + B1*w.Y) + B2*w.Z) + B3*w.W`. Position is `CoverageReferenceMath.Point`. Normal is `Linear`, then `len = Sqrt(Dot3(n,n))` and `len > 1e-8f ? n/len : v.Normal`, componentwise. A tangent with `Dot3(t,t) > 1e-12f` takes the same transform and fallback and keeps its W, otherwise it stays zero. Color and UV copy. A nonfinite result returns `UnsupportedTransform`. This mirrors `SkinningMath.SkinVertex` semantics, not its bits.
- Changes: every kernel phase, `CoverageReferenceMath` helper and `CoverageReferenceSupport` query takes the arithmetic instance as its first parameter. `CoverageReferenceMath` gains `Sub3`, `Equal3`, `IsFinite3` and `IsFiniteVertex`. Kernel members are `CoverageReferenceMath`, `CoverageReferenceCorrection`, `CoverageReferenceNormals` (type level), the `CoverageReferenceConstructor` phases (`TryInterpolate`, `TryConformGroups`, `Conform`, `Position`, `Edge`, `TryVertex`, `Direction`, `Sum`, `Value`, `Magnitude`, method level), the `CoverageReferenceSupport` queries (`TryHeight`, `TryBoundary`, `TryOverlayMaximum`, `OverlayPair`, `OverlayInside`, `OverlayAccumulate`, `Bounds`, `Cell`), `CoverageReferenceSkinning.TrySkin`, `CoverageTally.Note` and `Succeeded`, and the `CoverageFrames.Refuse` overloads. Host orchestration stays unattributed: `TryConstruct`, argument validation, frame preparation, the support constructor and `Compose`.

- [ ] Write the arithmetic tests in `CoverageReferenceArithmeticTests`:

```csharp
Assert.Equal(1f, f.Add(1f, MathF.ScaleB(1, -24)));                       // ties to even
Assert.Equal(BitConverter.SingleToUInt32Bits(a * b), BitConverter.SingleToUInt32Bits(f.Mul(a, b))); // table rows
Assert.Equal(0x00000000u, BitConverter.SingleToUInt32Bits(f.Max(-0f, 0f)));
Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(f.Min(0f, -0f)));
Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(f.Clamp(-0f, 0f, 1f)));
Assert.True(f.Equal(-0f, 0f));
Assert.Equal(0, f.EventCount);                                           // nothing above was subnormal
f.At(CoverageExactnessPhase.Conformation, 7, 1);
_ = f.Mul(MathF.ScaleB(1, -130), 1f);
Assert.Equal(new CoverageExactnessSite(CoverageExactnessPhase.Conformation, 7, CoverageExactnessOperation.Multiply,
    BitConverter.SingleToUInt32Bits(MathF.ScaleB(1, -130))), f.FirstSite);
Assert.Equal(1, f.FirstSlot);
f.At(CoverageExactnessPhase.NormalGather, 3);
_ = f.Mul(MathF.ScaleB(1, -63), MathF.ScaleB(1, -64));                   // result 2^-127
Assert.Equal(2, f.EventCount);
Assert.Equal(CoverageExactnessPhase.Conformation, f.FirstSite.Phase);   // first event kept
f.Reset();
Assert.Equal((0, default(CoverageExactnessSite), -1), (f.EventCount, f.FirstSite, f.FirstSlot));
```

  The table rows compare `Add`, `Sub`, `Mul`, `Div` and `Sqrt` bits with the raw C# operators and `MathF.Sqrt` over signed, cancelling, binade-crossing and overflowing pairs. `Smallest_normal_is_an_event_and_zero_is_not` (B32) asserts events for `2^-126` and `-2^-126` and none for `+0`, `-0` and the next normal `2^-126 + 2^-149`, as operands and results. `Predicate_inputs_and_rounded_values_are_traced` gives `Edge` a subnormal X coordinate, then inputs `(0,0)`, `(2^-70,0)`, `(0,2^-70)` whose exact determinant `2^-140` rounds to a subnormal, and expects `Predicate` events. `Grid_cell_compare_count_matches_truncating_cell` checks the new `Cell` against `Math.Clamp((int)(((v-min)/(max-min))*16), 0, 15)` written in the test, at every `k*(max-min)/16+min` boundary and both float neighbours, at `min`, `max`, outside both ends, `NaN`, both infinities and a degenerate `max == min`. `Device_path_members_are_binary32_kernels` reflects over the kernel member list above and asserts each carries `Binary32KernelAttribute`, and that `CoverageReferenceArithmetic` carries `Binary32ArithmeticAttribute`.

- [ ] Write `CoverageReferenceSkinningTests`. `Compose_pins_scalar_row_vector_order` compares bits with the formula written inline for three fixed matrix pairs, including a translation-heavy pair, and checks closeness to `SkinningMath.Compose` within `1e-6` relative. `Reference_skinning_pins_blend_transform_and_normalize_order` uses one two-bone vertex and compares every output bit with an inline independent scalar evaluation, then checks zero total weight keeps the source position, a near-zero normal falls back to the source normal, a zero tangent stays `Vector4.Zero`, and closeness to `SkinningMath.SkinVertex` within `1e-5`. `Reference_skin_rejects_invalid_bone_indices` expects `ArgumentException` for an index equal to the bone count.

- [ ] Extend `CoverageReferenceOutputTests`. `Fixture_reference_constructions_trace_no_subnormal_events` runs all 144 constructions and asserts `EventCount == 0` with the case name. `Subnormal_source_attribute_reports_its_first_consuming_operation` clones the `curved-ordinary-seams-l2-w1` posed input, sets source vertex 0's `Color.X` to `2^-130`, and expects the first site `(SourceValidation, 0, FiniteTest, bits(2^-130))` with construction still succeeding. `Tracing_does_not_change_outputs` runs one case twice through `Reset` and asserts identical bytes and identical traces. The existing manifest test is the byte-identity proof for the whole rewrite.

- [ ] Run to fail:

```bash
build-slot --label ec-b6c-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceArithmeticTests|FullyQualifiedName~CoverageReferenceSkinningTests|FullyQualifiedName~CoverageReferenceOutputTests'
```

  Expected: compile failure, the home, site and skinning types are missing.

- [ ] Implement. Add the attributes and the home first, then rewrite one kernel file at a time in place, keeping every expression tree exactly as it is: `(a * b) + c` becomes `f.Add(f.Mul(a, b), c)`, never a reassociated or fused form. Set `f.At(...)` at the head of each element loop with the phase table above. Precompute the topology weights and edge parameters, split the override conversion into `TryConstruct`'s CPU prelude, split the B30 sides, and add the reference skinning. The 6B analyzer must report nothing, so every remaining raw floating operator, conversion or call in a kernel member is a build error to fix at its source. Never add a suppression or widen the allow list. If a hash changes, find the reassociated or reordered expression and restore it. A hash that cannot be kept stops the task and returns to root with its name and first differing vertex.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b6c-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b6c-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests|FullyQualifiedName~CoverageCandidateConstructionTests|FullyQualifiedName~CoverageCertificationTests|FullyQualifiedName~CoverageCoreCorrectionTests'
build-slot --label ec-b6c-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include <every path changed in this task, repo-relative>
```

  Expected: zero warnings and zero KEB32 diagnostics, all 144 hashes unchanged, no fixture subnormal events, every earlier reference, exact-capture and exact-path test passes, format exit 0.

- [ ] Commit the listed paths with `render3d(coverage): route reference arithmetic through a traced binary32 home`.

### Task 6D: Write the Part C kernel contract

**Files**

- Create: `docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md`
- Read only: the amendment R spec (Part B items 3, 4 and 6, Part C per-frame GPU steps, evidence 5), Tasks 2 to 6C of this plan, the ledger rulings B16, B19, B25, B27 and B30, and the reference sources as committed by Task 6C

The contract is the document Part C implements and proves. It states rules and cites the reference by file and member, never by line, so it survives edits. It does not restate the spec. Root links it from the spec on the integration branch.

- [ ] Write the document with these sections, each concrete enough to implement from:

  1. **Authority.** The C# reference is the executable contract. Every coverage kernel output equals the reference output bit for bit on every fixture on Vulkan, Metal and Direct3D11. A mismatch fails and nothing is loosened. The device tolerance is zero. The `MarginFraction x Clearance` reserve is margin against a driver that breaks its precision rules, not a tolerance.
  2. **Inputs.** The CPU-side uniforms and static data, each with its producing member: command frames (`CoverageReferenceFrames`, double `CoverageAffine` composition rounded once, binary32 frame scalars), the composed palette (`CoverageReferenceSkinning.Compose`, which Part C staging must use for covered actors instead of `SkinningMath.Compose`), integer bone indices (`CoverageReferenceSkin`), support positions, slope, grid bounds and cell lists (`CoverageReferenceSupport`), and topology recipes, binary32 weights, edge parameters and gather rows (`CoverageReferenceTopology`). They are computed with scalar IEEE arithmetic on the CPU, never System.Numerics operators, so every CPU computes the same bits.
  3. **Operation inventory.** One table row per `CoverageReferenceArithmetic` member: add, subtract, multiply, divide, square root, abs, negate, min, max, clamp, the six comparisons, the finiteness test, the B27 predicate entries and the B30 crossing parameter, plus select and binary32 store. For each: the exact semantics and the kernel form. Comparisons are IEEE ordered and `-0 == +0`. Abs and negate act on the sign bit. Min and max follow .NET on finite operands, so `max(-0,+0) = +0` and `min(+0,-0) = -0`, and kernels implement them by compare and select with a sign test, never with native `min`, `max`, `fmin` or `fmax`. Clamp is `x < lo ? lo : (x > hi ? hi : x)`, which keeps `-0` at a `+0` lower bound. The finiteness test is an exponent-field bit test, never `isnan` or `isinf`. Stores preserve bits. Constants are the listed binary32 literals with their bit patterns. Integer index arithmetic and select are unrestricted.
  4. **Pinned order.** Every expression tree, transcribed from the Task 6C sources: `Dot3`, `Dot2`, `Cross`, `Point`, `Linear`, `Height` and `EdgeHeight`, skinning, edge and interior interpolation, attribute and direction interpolation, the collar smoothstep and point conformation, the grid cell compare count, the boundary query, the overlay crossing and accumulate, discrepancy, deficit and correction, the full-target check, transition, face validation, the normal basis, `Unit`, `Combine` and the gathers. Kernels write the same trees. No reassociation, contraction or algebraic simplification, including `x + 0` and `x * 1`. The like-for-like overrides are a test seam that kernels omit. Production correction is `deficit / CorrectionRate`, equal to the reference's `(deficit + 0) / CorrectionRate` because the deficit is positive there.
  5. **Compilation.** GLSL `precise` on every intermediate of these kernels, Metal fast maths off for the coverage library, and FXC with IEEE strictness (`D3DCOMPILE_IEEE_STRICTNESS`) and no `rcp` or `rsq` lowering of `/` and `sqrt`. Part C verifies the emitted code keeps the order, which was the old RV2 lowering obligation.
  6. **Division and square root.** Where a backend's native result is not correctly rounded (the table in this plan's NC2 section), the kernel takes the native value and selects the correctly rounded neighbour with an exact residual sign test built from correctly rounded add, subtract and multiply (TwoSum and Veltkamp-Dekker TwoProduct) or 32-bit integer arithmetic. For normal operands and results a binary32 quotient or square root is never exactly halfway between neighbours, so no tie rule is needed, but the tests still cover near-midpoint cases. Part C proves per backend that the native error bound keeps the correct result inside the searched neighbours, and preserves overflow to infinity. Operands are never subnormal after the section 8 flush, and a result that would be subnormal is flushed after correction.
  7. **Exact predicates and the crossing parameter.** B27: each predicate returns the exact sign and the exact value rounded once to nearest binary32, ties to even, of its binary32 inputs, so any exact method matches. Float error-free expansions are allowed only on a backend where correctly rounded add, subtract and multiply without contraction are verified, otherwise 32-bit integer multiword arithmetic. B30: `t = RN32(ca*2^k) / (RN32(ca*2^k) - RN32(cb*2^k))`, with `k` putting `max(|ca|,|cb|)*2^k` in `[1,2)`, then clamp to `[0,1]`.
  8. **Flush rule (B38, superseding the subnormal rejection).** Every operand and every result of every inventory operation whose magnitude is at most `2^-126`, `2^-126` included, becomes a zero of the same sign. Uploaded inputs are flushed the same way on first read, the B27 predicate's RN32 value is flushed while its exact sign stays exact, and the B30 scaled sides are flushed. Kernels implement the flush explicitly in code, an exponent-field compare and select applied after each operation, so behaviour never depends on a device's hardware denormal mode, whether it preserves or flushes. Division and square root are corrected to the correctly rounded value first, then flushed. No sample is rejected for subnormals. The reference's flush-event trace is a diagnostic count, never a failure. The dev-only real-GPU verification mode logs the first differing vertex whatever the cause.
  9. **Control flow.** The B16 grid query definition, the B19 alias policy, the B25 correction order, guard reduction keys and order (`CoverageTally`), first-code attribution, refusal codes and the atomic all-or-nothing publication, all as in the reference.
  10. **Verification.** Part C native equivalence tests run all 72 fixtures in production mode on all three CI backends and compare every output word with the reference, using `outputs.sha256` from Task 6A as the frozen expectation. Unit kernels test the inventory rows, the division and square-root correction at binade boundaries and near midpoints, and the predicates against a BigInteger oracle including a binary32 midpoint.

- [ ] Run the document guards and finish:

```bash
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

  Expected: every guard exits 0. Root reviews the contract against Task 6C's committed sources before Part C planning uses it.

- [ ] Commit the document with `docs(coverage): state the bit-exact coverage kernel contract`.

### Task 7: Removed by NC2

> Removed 2026-10-18. Task 7 added an internal `CoverageCertification.ValidateGeometry` overload with a world clearance padding, used only to certify at `Clearance + epsilon` under NC1. NC2 makes epsilon zero and the oracle certifies at the approved clearance through the unchanged public overload. No other need was found: the over-tight separation negative in Task 8 uses its own fixture and the approved clearance, the reserve is applied in construction, not certification, and exact certification's cap, budget and admission checks need no threshold change. `CoverageCertification.cs` stays unchanged. The number is kept so later references stay stable.

### Task 8: Publish the headless oracle with negatives, warm allocation proof and guide

> Amended 2026-10-19 for root ruling B38, overriding the text below where they differ. The oracle has two stages, run in order: `public enum CoverageOracleStage { None = 0, Construction = 1, Certification = 2 }`. Do not add `CoverageFailureCode.SubnormalArithmetic` and do not modify `CoverageFailure.cs` for it. `CoverageOracleFailure` carries no `Site`. Replace `Subnormal_operand_fails_at_device_exactness_before_certification` with `Subnormal_input_is_flushed_and_certifies_like_its_signed_zero`: the same subnormal palette coefficient produces exactly the output and certificate of the same pose with that coefficient replaced by a zero of its sign, and the oracle passes or fails on certification alone. Drop the `DeviceExactness` row from the allocation test's failure list. The guide documents two stages and the flush rule. Asserting code 15 or stage 3 is removed.

> Amended 2026-10-18 for NC2. The oracle no longer evaluates enclosures, composes a tolerance or pads certification. It skins with the pinned reference skinning, constructs, checks device exactness and certifies at the approved clearance.

**Files**

- Create: `KhaozEngine.Render3D/Coverage/CoverageOracle.cs`
- Create: `KhaozEngine.Render3D/Coverage/CoverageOracleResult.cs`
- Modify: `KhaozEngine.Render3D/Coverage/CoverageFailure.cs`, append `SubnormalArithmetic = 15` and extend the constructor guard
- Modify: `KhaozEngine.Render3D/Coverage/CoverageExactnessSite.cs`, make the two enums and the site public with XML documentation, no value change
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageOracleTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/CoverageOracleAllocationTests.cs`
- Modify: `docs/USING-KHAOZENGINE.md:6363-6368`, add a headless oracle subsection beside optional coverage
- Modify: `KhaozEngine.Render3D/README.md:151-160`, link the headless oracle contract without changing the still-existing runtime description
- Read only: `Scene3D.Coverage.cs:81-86`, `CoverageCertification.cs:30-41`, `test-shared/AllocationMeasurement.cs`, `test-shared/AllocationRegion.cs`

**Interfaces**

- Consumes: Task 5 constructor, Task 6C arithmetic trace, `CoverageReferenceSkin`, `CoverageReferenceSkinning.Compose` and `TrySkin`, and the unchanged public `CoverageCertification.ValidateGeometry(topology, before, after, actorModel, commands, scratch, out CoverageGeometryCertificate, out CoverageFailure)`.
- Produces: `public enum CoverageOracleStage { None = 0, Construction = 1, Certification = 2, DeviceExactness = 3 }`. Stages run construction, device exactness, certification.
- Produces: `CoverageFailureCode.SubnormalArithmetic = 15`, appended after `UnsupportedAdapter = 14` without renumbering. These are the slots the plan once gave `DeviceTolerance` and `DeviceToleranceExceeded`, which were never implemented.
- Produces: `public readonly record struct CoverageOracleFailure(CoverageOracleStage Stage, CoverageFailure Failure, CoverageExactnessSite Site)`. `Site` is default except at `DeviceExactness`.
- Produces: `public readonly record struct CoverageOracleCertificate(bool FullCoreClearance, double MinimumCoreClearance, int DisplacedVertices)`, mapped from `CoverageGeometryCertificate`.
- Produces: `public sealed class CoverageOracle`, constructor `(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> inverseBind, int commandCapacity = CoverageScratch.MaxCommands)`, `public CoverageRefinement Topology { get; }` and `public bool TryCheck(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, out CoverageOracleCertificate certificate, out CoverageOracleFailure failure)`.
- Produces internally: `internal ReadOnlySpan<ModelVertex> CoverageOracle.LastConstructedVertices`, empty after a construction refusal, and `internal int CoverageOracle.CertificationRunsForTests`, an integer incremented when exact validation runs (B11, no delegate). Owned scratch is invalidated on the next check and is never a public drawable surface.
- Device-exactness failure record: `new CoverageFailure(SubnormalArithmetic, host, equipmentInstanceId, commandId, count: EventCount, limit: 0)`, where the equipment and command identify the first event's slot and are zero for a phase without one.

- [ ] Write `Every_frozen_fixture_reference_passes_the_public_headless_oracle`, `Oracle_negatives_preserve_stage_code_command_and_count`, `Refusal_does_not_poison_the_next_check` and `Failure_numbering_is_append_only`.

```csharp
Assert.True(oracle.TryCheck(palette, model, commands, out var certificate, out var failure));
Assert.Equal(default, failure);
Assert.Equal(commands.All(c => c.TransitionWeight == 1f), certificate.FullCoreClearance);
Assert.Equal(15, (int)CoverageFailureCode.SubnormalArithmetic);
Assert.Equal(3, (int)CoverageOracleStage.DeviceExactness);
Assert.Equal(CoverageOracleStage.Construction, escaped.Stage);
Assert.Equal(CoverageFailureCode.CoreEscaped, escaped.Failure.Code);
Assert.Equal(CoverageOracleStage.Construction, budget.Stage);
Assert.Equal(CoverageFailureCode.RecessionExceeded, budget.Failure.Code);
Assert.Equal(CoverageOracleStage.Certification, overTight.Stage);
Assert.Equal(CoverageFailureCode.EquipmentIntersection, overTight.Failure.Code);
Assert.Equal(1u, overTight.Failure.CommandId);
Assert.True(overTight.Failure.Count > 0);
Assert.Equal(default, overTight.Site);
Assert.Equal(default, failedCertificate);
Assert.Equal(firstSuccessCertificate, successAfterFailureCertificate);
Assert.Equal(firstSuccessBits, successAfterFailureBits);
```

  The fixture test runs all 72 cases through `TryCheck` with each case's palette, inverse binds, model and commands. A case that fails at any stage is reported with its stage, code and site and returns to root. It is never omitted or relaxed. `Failure_numbering_is_append_only` asserts every earlier `CoverageFailureCode` value is unchanged, `CoverageFailure` accepts code `15` and throws `ArgumentOutOfRangeException` for `16`.

  Support escape uses the existing `.9f` support translation. Budget excess uses `.1f` at full and zero transition weights. Define the over-tight **separation** case independently of construction: retain a small host fixture with a protected collar edge whose known positive gap to the real solid is `.01f`, place every core vertex behind the sheet, and submit clearance `.02f` with caps `[-2,2]` and budget `.8f`. Point construction and normal guards must succeed, while whole-face exact separation must fail `EquipmentIntersection`. Pin the rigid solid and collar-edge coordinates in the test and assert its `.01f` separation with `CoverageTriangleGeometry.SeparationLowerBound` before invoking the oracle. Do not use `Clearance=1e-6f` as this test, since exact frame admission would mask separation.

- [ ] Write `Subnormal_operand_fails_at_device_exactness_before_certification`. Take `cost-typical-l2-lifted-w1`, whose inverse binds are identities, so `Compose` keeps a joint coefficient exactly. Set joint 1's `M12` to `2^-130`, a shear far below any guard, and first assert the case still constructs with that palette. Let `s` be the lowest source vertex whose total weight is at least `1e-8f` and whose four bone indices name joint 1, since the blend reads all four bones whatever their weight. Expect `Stage == DeviceExactness`, `Failure.Code == SubnormalArithmetic`, `Failure.Count >= 1`, `Site == (Skinning, s, Multiply, bits(2^-130))`, a default certificate and an unchanged `CertificationRunsForTests`. `Construction_refusal_outranks_a_subnormal_event` combines that palette with the escaped-core commands and expects `Construction/CoreEscaped` with a default site. `Subnormal_free_success_reports_no_site` checks a passing case has `failure == default`.

- [ ] Write `Unabsorbed_error_scenario_reports_the_exact_certifier_refusal`, the B2 negative. The `unabsorbed-error` scenario (`Clearance = 1e-6f`) has no reference uncertainty refusal, so construction succeeds (`LastConstructedVertices` is not empty) and no subnormal event occurs. Exact frame admission then refuses, so assert `Stage == Certification`, `Failure.Code == UnsupportedTransform`, `Failure.CommandId == 1u` and a default certificate. It never passes silently.

- [ ] Write `Oracle_skinning_is_the_pinned_reference_skinning`: use inverse binds translating X by `-.25f` and `+.125f` and joint matrices translating X by `+.25f` and `-.125f`. Compose each with `CoverageReferenceSkinning.Compose`, skin with `TrySkin` and a fresh arithmetic, feed the posed vertices to `CoverageReferenceConstructor.TryConstruct`, and assert its final bits equal `oracle.LastConstructedVertices`. Mutating the caller's inverse-bind array afterwards must not change results. The zero-weight bone case keeps identity skinning. A palette length mismatch throws `ArgumentException` before construction.

- [ ] Write `Partial_weight_certificate_does_not_claim_core_clearance`: at weights `0f` and `.5f`, expect `FullCoreClearance=false` and `double.IsNaN(MinimumCoreClearance)`, while weight zero retains every unconformed reference byte. Such a result is never documented as proving full-weight clearance.

- [ ] Run to fail:

```bash
build-slot --label ec-b8-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageOracleTests|FullyQualifiedName~CoverageOracleAllocationTests'
```

  Expected: compile failure, the public oracle types are absent.

- [ ] Implement the public signatures. The constructor snapshots inverse binds, validates count `1..128` and finite affine inverse binds, builds `CoverageReferenceSkin` against that count, and allocates composed matrices, posed-source vertices, reference scratch, final vertices and an independent `CoverageScratch` for exact certification. Wrong capacities or invalid bone indices are argument errors at creation. The object is reusable, single-caller and not thread safe. It owns no GPU resource and needs no `IDisposable`.

  Each call resets its certificate, exposed candidate length and arithmetic trace. Require one through `commandCapacity` commands, matching staging. An empty or oversized command span reports `Construction/CapacityExceeded`, with zero command IDs, the supplied count and the configured limit. Compose every palette entry with `CoverageReferenceSkinning.Compose(palette[i], inverseBind[i])` on the CPU, then skin through `TrySkin` with the scratch's traced arithmetic. A skinning refusal reports `Construction` with `UnsupportedTransform`. Construct with the reference in production mode. A construction refusal reports `Construction` with the unchanged `CoverageFailure`. A nonzero `EventCount` then reports `DeviceExactness` with the failure record above and `FirstSite`, and never runs exact validation. Otherwise call the public `ValidateGeometry` with the original commands, the reference `Before` and the reference final output. Its failure reports `Certification` with the exact certifier's complete failure record. Success maps the geometry certificate. No render context, pass capture, Scene3D, GPU object or runtime fallback is created.

  Exact certification still performs its own frame preparation, so its precondition or uncertainty refusals report `Certification/UnsupportedTransform` rather than a guessed construction code. The B2 test documents this.

- [ ] Write the allocation assertion in `[Collection("AllocSensitive")]`, whose definition disables parallelization. Prebuild the fixture, command arrays, accepted pose, escaped pose, budget-negative commands, over-tight fixture and subnormal palette. Warm each distinct success and refusal path once outside the measured region. Use `AllocationMeasurement.Measure` with a precreated delegate containing one success, the three original refusals, the device-exactness refusal and another success. Store booleans and failures in preallocated state and assert afterwards:

```csharp
Assert.Equal(0, allocatedBytes);
Assert.Equal(new[] { true, false, false, false, false, true }, results);
Assert.Equal(CoverageOracleStage.Construction, failures[1].Stage);
Assert.Equal(CoverageOracleStage.Construction, failures[2].Stage);
Assert.Equal(CoverageOracleStage.Certification, failures[3].Stage);
Assert.Equal(CoverageOracleStage.DeviceExactness, failures[4].Stage);
Assert.Equal(CoverageFailureCode.SubnormalArithmetic, failures[4].Failure.Code);
```

  The check includes composition, traced skinning, construction, the exactness decision and exact validation, including refusal handling and recovery. No assertions, formatting, delegate creation, array allocation or LINQ runs inside the measured delegate. This is an allocation correctness test, not a timing pilot or a loop of suite runs.

- [ ] Document the constructor, copied inverse-bind ownership, exact `TryCheck` signature, the three stages in execution order, stage, code, command, count and site reporting, zero warm allocations, non-thread-safe reuse and the distinction between a headless certificate and drawable permission. Include one complete headless example that builds a refinement, supplies the same palette and actual command frames as the game and fails CI on a false result. State that certification uses the approved clearance with zero device tolerance, that the reserve is margin, that the guarantee holds on devices meeting the kernel contract (link Task 6D's document), that a sample with a subnormal operand or result fails at device exactness, and that the certificate covers the exact palette, model and commands supplied, so the game's sampler must produce them deterministically on every machine that plays them. State the partial-transition limitation and the R2 sampled-pose limitation. The game's sampler, blend grid and asset inventory remain Part D. The dev-only real-GPU verification mode remains Part C.

  Run the full Markdown name sweep for `CoverageOracle`, `CoverageEvaluator`, `CoverageCertification`, `TryConstructCandidate` and the claim that every submitted pose is certified. Change only the two living references listed for this task. Report other runtime descriptions to root for the already-planned Part C API and doc sweep. Do not rewrite historical designs or preemptively describe the old runtime as removed.

- [ ] Run to pass and finish:

```bash
build-slot --label ec-b8-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --label ec-b8-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageOracle|FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests|FullyQualifiedName~CoverageCertificationTests'
build-slot --label ec-b8-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include KhaozEngine.Render3D/Coverage/CoverageOracle.cs KhaozEngine.Render3D/Coverage/CoverageOracleResult.cs KhaozEngine.Render3D/Coverage/CoverageFailure.cs KhaozEngine.Render3D/Coverage/CoverageExactnessSite.cs KhaozEngine.Render.Tests/Render3D/CoverageOracleTests.cs KhaozEngine.Render.Tests/Render3D/CoverageOracleAllocationTests.cs
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

  Expected: zero-warning Release build with zero KEB32 diagnostics, every selected correctness and allocation test passes, the format check and all guards exit 0. No native backend or measurement claim is made by this headless slice. Root receives the commit, case counts, the zero-byte result and any remaining numerical-contract objection.

- [ ] Commit the listed paths with `render3d(coverage): expose the headless reference oracle`.

## Self-review

> Amended 2026-10-18 for NC2. The NC1 entries described the enclosure, padding and epsilon tasks that NC2 replaced.

- [x] Spec coverage: Task 1 is the unchanged 72-case capture-first commit. Tasks 2 to 5 own scalar frames, reserve, interpolation, conformation, guards, correction and normals. Task 6A ports B30 and freezes 144 reference outputs. Task 6B enforces the operation inventory at compile time. Task 6C routes the device path through one traced arithmetic home and adds the pinned reference skinning. Task 6D states the Part C kernel contract. Task 7 is removed. Task 8 owns the public headless oracle with its three stages. GPU implementation, native equivalence and the dev real-GPU mode remain Part C, and game sampling remains Part D.
- [x] NC2: no enclosure, epsilon, padding, k_op table or tolerance budget survives. The reserve stays as margin. Every out-of-inventory site found at `8765a4c27` has a disposition in Task 6C. Subnormal events have one home, one trace and one oracle stage, `DeviceExactness = 3` with `SubnormalArithmetic = 15`, reusing the never-implemented NC1 slots.
- [x] Honest limits: bit exactness is proved per backend only by Part C's native tests, which the frozen manifest feeds. The oracle certifies the exact palette, model and commands supplied, so cross-machine determinism of the game's sampler is a Part D obligation. Division and square-root correction and the exact predicate method are Part C proof obligations stated in Task 6D. Quarter-clearance fidelity, measured like with like, remains an acceptance assertion.
- [x] Step scan: every new task has Files, Interfaces, failing assertions, a red invocation, implementation rules, a zero-warning Release build, a focused green invocation, the B13 format form and a commit subject. Every local build and test uses build-slot. No loops, timings, native implementation or load are authorized. No step uses `git stash` (B26).
- [x] Types: Task 6C consumes 6A's manifest and 6B's analyzer. Task 8 consumes 6C's trace, site and skinning. Stage 3 and failure 15 are append-only. Cold scratch allocation and full resets cover warm reuse.
- [x] Review Focus: 1 has Task 2, 6C and 8 tests, 2 has Task 1, 4, 5 and 6A tests, 3 has Task 3, 4 and 8 tests, 4 has Task 6C and 8 reset and order tests, 5 has Task 5 and 8 tests, 6 has Tasks 6A to 6D.
- [x] Proportion: the analyzer is the one new enforcement mechanism, chosen because a lexical sweep cannot see raw float operators and a bit-exact contract needs the order enforced, not reviewed. Removing the NC1 interval code leaves no speculative code. No production method bodies, unrelated fixes, GPU work or release ritual were added.
- [x] Planning validation for the NC2 amendment was source, spec, ledger and draft-branch reading. No builds, tests, dotnet, benchmarks, load, commits, branch changes, pushes, issues or network writes ran.
