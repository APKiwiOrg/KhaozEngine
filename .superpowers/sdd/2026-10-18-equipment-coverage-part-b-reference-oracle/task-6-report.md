# Part B Task 6 report

Status: BLOCKED. No commit. HEAD remains `8765a4c27`.

## Scope and starting guard

Worked only in `/Users/antonio/KhaozEngine/.worktrees/ec-oracle` on `feature/equipment-coverage-oracle`. The requested starting command exited 0 and printed exactly the required branch and `8765a4c27`, with no status entries.

Read the task brief, plan-context Global Constraints and NC1, the B rulings through B29, amendment R Part B items 3 and 6, the relevant prior report sections, engine AGENTS.md and its engine code and test contracts. Did not read the whole plan. No subagents, reviewers, stash, push, merge, rebase, branch change, tag, other-worktree write, version change, package, native test, pilot, client launch or stress run occurred.

## Why work stopped

The user explicitly required stopping and reporting BLOCKED when the frozen-fixture finiteness gate fails, with the first unbounded quantity and its cause. A numerical feasibility test found that failure before production implementation. The test is deliberately narrower than a full enclosure evaluation. It starts from CPU pointwise-conformed positions and CPU-rounded coverage transforms as point boxes, rather than adding the upstream skinning, interpolation and transform widths. This is a necessary-condition probe, not a replacement evaluator or a claim of device equivalence. Wider upstream boxes cannot make the prescribed independent interval denominator exclude zero when its point-box subcase already includes zero.

All 72 frozen rows were visited in one test invocation. Three rows fail the necessary condition:

- `cost-limit-l2-lifted-w0`
- `cost-limit-l2-lifted-w05`
- `cost-limit-l2-lifted-w1`

All three have the same first unbounded operation in the point-box probe, in sorted slot 1, command 2, core face 559, support source triangle 4, host edge 2 and support edge 2. The input geometry after production-reserve pointwise conformation is:

```text
a = <0.025000006, 0.025000006, -0.05>
b = <0, 0, -0.005079873>
s = <0, 0, 0.02>
t = <-0.25, -0.25, 0.04>
```

The XY coordinates obey `x == y` on all four endpoints. B27's exact predicate confirms all four side signs are zero. The CPU therefore takes the collinear skip. B29's widened predicate-value intervals are:

```text
ca = [-6.071532579509132E-18, +6.071532579509132E-18]
cb = [-1.1754944909521345E-38, +1.1754944909521345E-38]
ac = [-6.50521355190091E-19, +6.50521355190091E-19]
ad = [-6.938894731087842E-18, +6.938894731087842E-18]
ca - cb = [-6.07153299309944E-18, +6.07153299309944E-18]
ca / (ca - cb) = [-Infinity, +Infinity]
exactSigns = (0, 0, 0, 0)
```

The **first unbounded quantity is the crossing quotient**, not the determinant or subtraction. No same-side skip is certain from these intervals. Every side includes zero, so the literal B29 rule joins the crossing alternative as well as collinear and endpoint alternatives. NC1 makes division by a zero-containing divisor unbounded. The user also forbids recovering a finite result by clamping an unbounded quotient. A complete evaluator following those rules must propagate unbounded boxes and `DeviceTolerance = PositiveInfinity`.

A full graph could become unbounded earlier. The probe identifies a necessary failing site without claiming to implement or evaluate that full graph.

This is not remedied by a tighter binary64 exact determinant. Even if both collinear determinants were enclosed ideally by `[0,0]`, NC1's store operation includes the positive `2^-126` flush term and one binary32 ULP. The widened values both straddle zero and their subtraction does too. The positive branch test also verifies the axis-aligned exact-collinear case, where the outward binary64 endpoints round to zero before store widening.

Weight zero does not evade the full-target calculation. Full-target guards precede transition by NC1. No fixture was omitted, recaptured or special-cased. No ULP count, reserve budget or fidelity rule was changed. No CPU-selected collinear skip was replayed as an enclosure proof.

## Work product and line ranges

No production code was implemented. `CoverageReferenceEnclosure.cs` and `CoverageReferenceEnclosureScratch.cs` remain absent. `CoverageReferencePrecision.cs`, the float constructor and every refusal code remain unchanged.

The only source-tree draft is the untracked `KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs`:

- Lines 16 to 66 test the necessary finite crossing-denominator condition on every frozen row in production reserve mode, and print the first unbounded site per row.
- Lines 69 to 85 check a side interval straddling zero against independent analytic endpoint `z=0` and crossing `z=.2` alternatives. They also exercise a collinear zero-containing divisor. This fact passes.
- Lines 87 to 107 implement the test-local B29 determinant enclosure and binary64 outward arithmetic. No device add, subtract or multiply error is inserted inside the exact predicate.

The full draft and a re-applicable patch are saved in this ledger as `task-6-feasibility-source.cs.txt` and `task-6-b29-reproducer.patch`. The initial production-API containment tests, including every final vertex, finite bounds, independent outward world centers and tolerance composition, are saved as `task-6-api-tests.patch`. B26's patch and reverse-apply workflow was used to replace those uncompilable red tests with the isolated numerical reproducer. No stash was used.

The initial API tests were not weakened to pass. They are preserved for continuation after root resolves the numerical contract. Conditioning, full equipment, command reorder, reset, normal-guard, final-store and allocation acceptance tests remain incomplete. The diagnostic file is not the Task 6 implementation.

## Interfaces

None delivered. The required `Evaluate`, `EquipmentWorldVertex`, enclosure scratch constructor, `HostModelBounds`, `HostWorldBounds` and `DeviceTolerance` interfaces remain open. There is no partial evaluator or throwing API stub that later tasks could mistake for a completed implementation.

## Widening table and B29 probe

The probe reuses the unchanged Task 2 operation set. The following describes what the probe actually evaluates, not an unimplemented full graph:

| Operation | NC1 k_op | Applied in this probe |
| --- | --- | --- |
| Exact predicate's internal bilinear arithmetic | No intermediate device widening | Binary64 endpoint subtraction and four-product extrema are rounded outward using `Math.BitDecrement` and `Math.BitIncrement` |
| RN32 predicate result and final store | 1 | Monotone RN32 hull of the outward determinant endpoints, then `CoverageFloatIntervals.Store32`, including `2^-126` and operand flushing |
| Crossing denominator subtraction | 1 | `CoverageFloatIntervals.Subtract(ca, cb)` |
| Crossing division | 4 | `CoverageFloatIntervals.Divide`, retaining its one-ULP reciprocal and multiplication expansion hull and divisor-domain refusal |
| Crossing point multiply and add | 1 each | The independent positive branch fact evaluates `z = 0 + t * (1 - 0)` with Task 2 operations |
| Clamp, compare and hull | 0 | Exact finite selections, with Task 2 operand flushing. Same-side skips require a strictly certain common sign. The positive fact joins the endpoint alternative |
| Square root | 16 | Not evaluated by the crossing probe. Task 2's implementation is unchanged |

For each exact-predicate site, the probe encloses `(b.x-a.x)*(p.y-a.y) - (b.y-a.y)*(p.x-a.x)` with outward binary64 interval arithmetic. Both binary64 endpoints are rounded to binary32, giving an outward hull of RN32's monotone image. The hull then goes through NC1 store widening. Only an interval entirely above or below zero has a certain nonzero sign. This follows B29's specified pointwise value enclosure and does not change the B27 exact predicate implementation.

The `.NET` endpoint APIs were checked against the [official BitIncrement reference](https://learn.microsoft.com/dotnet/api/system.math.bitincrement?view=net-10.0) and [BitDecrement reference](https://learn.microsoft.com/dotnet/api/system.math.bitdecrement?view=net-10.0).

## Red and green evidence

All build, test and formatter invocations go through `build-slot`. Outputs are retained in this ledger. No test loop or repeated-until-flaky run occurred.

### Expected missing-type RED

```bash
build-slot --label ec-b6-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests'
```

Exit 1, log `task-6-red.log`. Expected compile failures:

```text
CoverageReferenceEnclosureTests.cs(26,29): error CS0246: CoverageReferenceEnclosureScratch could not be found
CoverageReferenceEnclosureTests.cs(27,9): error CS0103: CoverageReferenceEnclosure does not exist in the current context
build-slot: exit=1 waited=52.802s ran=175.474s
```

### Fixture finiteness RED and independent branch fact GREEN

```bash
build-slot --label ec-b6-b29-feasibility -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests' --logger 'console;verbosity=detailed'
```

Exit 1, log `task-6-b29-feasibility.log`:

```text
Test Run Failed.
Total tests: 73
     Passed: 70
     Failed: 3
build-slot: exit=1 waited=0.180s ran=198.842s
```

The 70 passes comprise 69 finite point-box crossing checks and the independent uncertain-side branch fact. Passing a point-box check does not establish a finite epsilon for that row. It only means this necessary-condition probe found no unbounded quotient there. The three failures are the three lifted limit L2 rows named above. All three fail at the same quotient and have the same reserve delta.

No GREEN claim is made for the missing enclosure API, full pose evaluation, finiteness gate or Task 6 overall.

## Final verification

After the last source edit, the following Release build ran, exit 0, log `task-6-build.log`:

```bash
build-slot --label ec-b6-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
```

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
build-slot: exit=0 waited=0.109s ran=129.899s
```

The final expanded test filter includes all `CoverageReference` and `CoverageExactCaptureTests` rows as requested:

```bash
build-slot --label ec-b6-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests|FullyQualifiedName~CoverageReferencePrimitiveTests|FullyQualifiedName~CoverageReferenceFidelityTests|FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests' --logger 'console;verbosity=detailed'
```

Exit 1, log `task-6-tests.log`. The only failures are the three B29 fixture cases already identified. `task-6-final-failures.log` retains their full names. No existing reference or exact-capture test failed.

```text
Test Run Failed.
Total tests: 491
     Passed: 488
     Failed: 3
build-slot: exit=1 waited=0.110s ran=188.035s
```

The B13 scoped whitespace check ran from the worktree root with the one changed source path. The proposed production files do not exist and the precision file has no changes:

```bash
build-slot --label ec-b6-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs
```

The formatter exited 0, log `task-6-format.log`:

```text
Warnings were encountered while loading the workspace. Set the verbosity option to the 'diagnostic' level to log warnings.
build-slot: exit=0 waited=0.095s ran=470.020s
```

This is a workspace-loading warning, not a Release-build warning. Its specific cause was not diagnosed in this blocked task. No warning-free workspace-load claim is made. The scoped whitespace command did exit successfully.

`sh scripts/check-file-size.sh --tree` exited 0, log `task-6-file-size.log`. It reported only the existing lower-baseline notice for `KhaozEngine.Render3D/Scene3D.cs` (2,188 lines versus 2,228). That file and its baseline were not changed.

`git diff --check`, `sh scripts/check-dashes.sh --tree` and `sh scripts/check-prose.sh --tree` exited 0. Their logs are `task-6-diff-check.log`, `task-6-dashes.log` and `task-6-prose.log`. The prose guard reported only existing lower-baseline notices for `KhaozEngine.Gui/README.md` and `docs/USING-KHAOZENGINE.md`. Those files and baselines were not changed.

The untracked draft also has no whitespace-error output from `git diff --no-index --check -- /dev/null KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs`, exit 1 because the files differ. Its log is `task-6-draft-whitespace.log`. The retained reproducer patch reverse-checks against the draft with exit 0. No commit is allowed while the acceptance gate remains red.

## Fixture summary

These are **point-box feasibility results, not measured full-evaluator epsilon ranges**. The full evaluator does not exist. Under the specified propagation rule, the three failed rows already force epsilon to positive infinity and exceed `delta = 0.004999999888241291`. The other 69 epsilon ranges are not evaluated, rather than guessed from CPU output or reported finite. The worst rows tie across weights zero, half and full. No RV1 square-root conflict was encountered by this probe, which does not execute square-root intervals. Full-graph RV1 status remains untested.

| Fixture | Point-box B29 check | Epsilon range | Reserve delta |
| --- | --- | --- | --- |
| `cost-limit-l2-bent-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l2-bent-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l2-bent-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l2-lifted-w0` | unbounded quotient | forced +Infinity | 0.004999999888241291 |
| `cost-limit-l2-lifted-w05` | unbounded quotient | forced +Infinity | 0.004999999888241291 |
| `cost-limit-l2-lifted-w1` | unbounded quotient | forced +Infinity | 0.004999999888241291 |
| `cost-limit-l3-bent-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l3-bent-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l3-bent-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l3-lifted-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l3-lifted-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l3-lifted-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l4-bent-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l4-bent-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l4-bent-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l4-lifted-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l4-lifted-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-limit-l4-lifted-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l2-bent-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l2-bent-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l2-bent-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l2-lifted-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l2-lifted-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l2-lifted-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l3-bent-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l3-bent-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l3-bent-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l3-lifted-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l3-lifted-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l3-lifted-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l4-bent-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l4-bent-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l4-bent-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l4-lifted-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l4-lifted-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `cost-typical-l4-lifted-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l2-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l2-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l2-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l3-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l3-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l3-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l4-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l4-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-seams-l4-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l2-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l2-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l2-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l3-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l3-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l3-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l4-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l4-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-challenging-welded-l4-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l2-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l2-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l2-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l3-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l3-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l3-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l4-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l4-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-seams-l4-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l2-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l2-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l2-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l3-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l3-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l3-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l4-w0` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l4-w05` | no unbounded quotient found | not evaluated | 0.004999999888241291 |
| `curved-ordinary-welded-l4-w1` | no unbounded quotient found | not evaluated | 0.004999999888241291 |

## Warm allocation status

Not measured. There is no production enclosure evaluator or enclosure scratch to measure. The test-local reproducer allocates host and support-corner arrays during cold correctness checks. It is diagnostic test code only and supplies no warm allocation evidence. Task 8 retains the formal warm-path allocation proof.

## Concerns and continuation

Root must resolve the conflict between B29's zero-containing predicate branch join, NC1's mandatory unbounded zero-containing division, and the all-fixtures-finite gate. This is an interval-proof failure on real frozen collinear geometry, not evidence that the CPU construction or any GPU output is wrong. A larger reserve cannot repair an infinite bound, and no finite epsilon was substituted.

Until root gives that numerical-contract ruling, Task 6 implementation, its required internal API, finite per-fixture epsilon ranges and green verification remain open. No work is ready for integration. The only source-tree change is the untracked diagnostic test file named above. Its patch and source copy are also retained in the ignored ledger. No acceptance assertion was changed to pass, and no failing implementation was committed. Root owns the numerical ruling and any accepted continuation, commit and push.


## Continuation under B30

Status: BLOCKED. No commit. Numerical implementation stopped at the required fixture finiteness gate. HEAD is still `8765a4c27`. The source tree contains drafts, not an accepted Task 6 implementation. Do not integrate or consume these draft APIs as a completed finite oracle.

### Starting guard and scope

The exact requested one-command guard exited 0 and printed:

```text
feature/equipment-coverage-oracle
8765a4c27
?? KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs
```

Read the brief, first report and saved probe/API patches, B8/B9/B13/B15/B16/B26/B27/RV1 and B29/B30, Global Constraints and NC1, the requested amendment sections, relevant prior report sections, engine AGENTS.md and engine code/test contracts. Did not read the whole plan. The inherited probe is preserved by the original worker's ledger files. Replaced its source-tree file with the saved API test draft and extended that draft.

No stash, subagent, reviewer, push, merge, rebase, tag, branch change, other-worktree write, version bump, package, client, native run, timing pilot or stress run occurred. All build, test and formatter commands use build-slot. Root owns any later integration and push.

### Draft implementation and file ranges

The following files are drafts in the assigned worktree. Their phase bodies are implemented and compile, but the full gate fails in normal arithmetic. These ranges cover each whole cohesive type, not a claim that its acceptance checks are complete.

| File | Lines |
| --- | --- |
| `KhaozEngine.Render3D/Coverage/CoverageEnclosureCorrection.cs` | 1 to 97 |
| `KhaozEngine.Render3D/Coverage/CoverageEnclosureMath.cs` | 1 to 125 |
| `KhaozEngine.Render3D/Coverage/CoverageEnclosureNormals.cs` | 1 to 118 |
| `KhaozEngine.Render3D/Coverage/CoverageEnclosureOverlay.cs` | 1 to 124 |
| `KhaozEngine.Render3D/Coverage/CoverageEnclosurePose.cs` | 1 to 160 |
| `KhaozEngine.Render3D/Coverage/CoverageEnclosurePredicates.cs` | 1 to 102 |
| `KhaozEngine.Render3D/Coverage/CoverageEnclosureSupport.cs` | 1 to 150 |
| `KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosure.cs` | 1 to 79 |
| `KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosureScratch.cs` | 1 to 76 |
| `KhaozEngine.Render3D/Coverage/CoverageReferencePredicates.cs` | 1 to 114 |
| `KhaozEngine.Render3D/Coverage/CoverageReferenceSupport.cs` | 1 to 224 |
| `KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureDiagnosticTests.cs` | 1 to 58 |
| `KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs` | 1 to 135 |
| `KhaozEngine.Render.Tests/Render3D/CoverageReferenceCrossingTests.cs` | 1 to 52 |

`CoverageEnclosureMath` expands skin-matrix blending, source point/direction transforms, canonical recipes, scalar vector arithmetic, final stores and outward binary64 centers. `CoverageEnclosurePose` evaluates source directions, interpolation guards, source-ordered interval grid queries, possible boundary/tie paths, reserved pointwise conformation and cap/recession guards. `CoverageEnclosureSupport` implements corner/edge/general height alternatives and every possibly selected boundary edge. `CoverageEnclosureOverlay` joins corners, endpoints and B30 crossings. `CoverageEnclosureCorrection` computes immutable face results, group maxima, full-target checks and transition. `CoverageEnclosureNormals` evaluates basis and gather arithmetic as position-selection guards. The main evaluator joins the uncovered Before pose when a guard may refuse, stores model bounds, transforms all host vertices and streams every complete equipment source vertex into the equipment radius.

The scratch owns fixed source/refined/group/face/final arrays and branch/diagnostic state. It owns no variable-size equipment array. Length, capacity, palette-read bounds and scratch identity checks precede Reset. Every evaluation resets active lengths, maxima and branch state. An unbounded operation fills published model/world boxes with unbounded intervals and makes DeviceTolerance positive infinity. Evaluate makes no budget decision and invokes no certification.

No exact-path file, refusal-code declaration, NC1 primitive/k_op implementation or frozen fixture was changed. `CoverageReferencePrecision` already supplied outward Radius and ComposeTolerance and did not need another implementation.

### Draft interfaces

The primary internal API is the brief's API:

```csharp
CoverageReferenceEnclosureScratch(CoverageRefinement topology, int commandCapacity = CoverageScratch.MaxCommands)
ReadOnlySpan<CoverageVertexEnclosure> HostModelBounds
ReadOnlySpan<CoverageVertexEnclosure> HostWorldBounds
double DeviceTolerance
CoverageReferenceEnclosure.Evaluate(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> composedPalette,
    ReadOnlySpan<ModelVertex> referenceFinal, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands,
    CoverageReferenceScratch referenceScratch, CoverageReferenceEnclosureScratch scratch)
CoverageVertexEnclosure CoverageReferenceEnclosure.EquipmentWorldVertex(CoverageSourceTopology source,
    int vertex, in Matrix4x4 equipmentWorld)
```

Additional internal helpers split mathematical concerns and keep the evaluator bounded. Scratch diagnostics identify the first unbounded stage, index and support triangle, the worst host vertex and a tracked dominant stage. These are numeric/enum fields, not allocated histories. `CoverageReferencePredicates.CrossingParameter(s,t,a,b)` is the single reference implementation of B30's normalized determinant ratio.

### Widening and NC1 mapping

| Site | NC1 widening |
| --- | --- |
| Skin blend, point/direction transform, interpolation, conformation, discrepancy, correction, transition, normal/basis/gather and world arithmetic | Add/subtract/multiply use k=1 and the existing input flush hull and 2^-126 output term |
| Ordinary division, including height, boundary projection, correction and normal basis | k=4, hulled with existing Reciprocal1 k=1 and Multiply k=1 expansion, unchanged finite-divisor domain |
| Square root and normalization lengths | k=16 and the existing inverse-square-root k=2/inherited divide expansion, RV1 zero/subnormal handling unchanged |
| Exact Edge sign | No store or flush widening. A point box returns the exact determinant sign |
| Exact predicate's RN32 arithmetic value | Outward binary64 exact range, monotone binary32 image hull, then Store32 k=1 and flush handling |
| B30 crossing geometry | Outward binary64 intersection of the unit interval, finite quotient and projected overlap, followed by the normalized formula's NC1 error and unit clamp |
| Final model stores | Store32 k=1 |
| Abs, min, max, clamp, comparisons, hull/select | k=0, with existing operand flushing and reachable alternatives |
| Exact-certifier center and radius composition | Outward binary64 only, no second device widening of CPU-composed inputs |

The square helper clamps a proven sum-of-squares domain to nonnegative before square root. No height or normal divisor containing zero is clamped finite. No k_op, world tolerance, clearance, cap, reserve or refusal budget was enlarged.

### B30 implementation and cost

The sign and RN32 value ranges are separate. Point Edge calls use the existing exact double-expansion predicate machinery and retain its exact sign even when the arithmetic value rounds or flushes to zero. A non-point Edge first computes a sound outward bilinear range with four coordinate differences, two interval products and one difference. When that range contains zero, the evaluator enumerates the multilinear determinant's box vertices with exact predicates. With n varying XY coordinates this costs 2^n exact determinant calls, at most 64 per uncertain Edge site. A point needs one exact determinant. Thus a pose with P Edge sites costs the fixed fast-range work plus the sum of these 2^n fallbacks, rather than an allocated path tree. No timing claim or pilot was made.

The reference CrossingParameter expands both exact determinants, picks a common exact binade, scales both expansions by a power of two, rounds each once with B27's ties-to-even Scalar, then performs the pinned float subtraction and division. Each proper crossing adds two six-term exact edge expansions, at most twelve expansion-component scale operations, two RN32 conversions, one float subtraction and one float division. The support code still performs its unit clamp and pinned point/height construction.

The interval crossing starts from the device parameter's geometric set. It intersects [0,1] with the quotient only when finite and with the other edge's outward projection interval when the host-edge squared-length range excludes zero. It adds a derived local formula error for RN32, Store32, subtraction and Divide, using the normalized side range and the denominator bound. This local operation error is not a fixed world epsilon. Empty parameter sets do not add a crossing. Endpoint alternatives require XY box-hull intersection with the other finite segment. The code comment states why this is a geometric enclosure of the device's own parameter, rather than a clamp of an unbounded quotient.

### Reference bit identity and subnormal regression

Before changing the reference code, a temporary snapshot fact exported every ModelVertex field as explicit BinaryWriter float words for all 72 fixtures in both production reserve and like-for-like capture modes. There are 144 baseline files under `task-6r1-reference-bytes/`, with `task-6r1-before-manifest.log` containing their SHA-256 hashes. In the gate run the same fact reconstructed every output after the scaled-t change and compared every byte to those baseline files. It passed. The baseline files themselves still match the original manifest. No frozen fixture was recaptured or edited.

For the normal finite fixture operands, common power-of-two scaling commutes with RN32 and with the normal finite subtraction/division ratio. The byte comparison supplies the concrete proof for every fixture, including normal, tangent and other attribute bits, rather than relying only on that algebraic argument.

The new independent analytic rational case uses exact side values unit squared and minus twice unit squared. Its exact rational parameters are 1/3 and 2/3, with expected binary32 words 0x3eaaaaab and 0x3f2aaaab. The initial gate passed the exponent -75 case, whose first unscaled determinant rounds to zero. The final test draft also covers exponent -74, where both unscaled determinants are representable nonzero subnormals. Final verification results follow below. Existing fidelity and attribute tests are unchanged.

### Why the continuation stops

The first observed unbounded sample is `cost-limit-l2-lifted-w05`, normal basis face 360, vertices 546, 212 and 186. Every source, interpolation, support-height, B30 overlay, conformation, correction and transition quantity before that stage was finite. The first nonfinite result is the division forming the final normal basis Au = Cross(t,m) / afterDet (`CoverageEnclosureNormals.cs`, lines 96 and 102 to 103). Its divisor afterDet = Dot(s,Cross(t,m)) has interval:

```text
afterDet = [-0.0019832744196597086, 0.004288300800300323]
```

The diagnostic separately prints all Before/After boxes, CPU positions, normals, lengths, basis terms and quotient bounds in `task-6r1-normal-trace.log`. Selected preceding values:

```text
beforeLength = [0.001249874842042364, 0.0012501308966037423]
afterLength  = [0.0012498613524126983, 0.002315124001149477]
beforeDet    = [0.0012496228877958253, 0.0012503829506285236]
afterScale   = [0, 0.002319002433724151]
afterThreshold = [-2.117582368135752e-22, 2.3190026362161122e-15]
1 / afterDet = [-Infinity, +Infinity]
```

Both normal lengths and their normalizations are finite. The interval graph loses the dependency between s, t and their normalized cross product m. The independent final basis determinant then spans zero. The normal guard can refuse, so the draft marks the uncovered alternative reachable. That does not provide a finite bound for the division on the remaining branch under the current intervals. Its threshold lower bound does not prove a divisor in NC1's finite domain. The draft does not discard the normal guard, substitute the CPU-selected result, clamp this divisor or change a rule to pass.

The broad After boxes also matter. Vertex 546's final covered Z box is approximately [-0.0775553845,-0.0499999417], whereas the reference changes its Z only from -0.05 to -0.050000004. This is interval/branch enclosure width, not an observed device/reference position breach. A tighter correlated enclosure may resolve it. This failed implementation gate is not a proof that every B30-compliant evaluator must be unbounded. No new numerical ruling is proposed or assumed.

### Red and green runs before final cleanup

All commands ran from the assigned worktree. Raw command output is in the named log.

| Log | Command | Exit and result |
| --- | --- | --- |
| task-6r1-api-red.log | `build-slot --label ec-b6r1-api-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests\|FullyQualifiedName~Subnormal_opposite_determinants'` | 1, expected missing enclosure helpers and CrossingParameter compile errors |
| task-6r1-before-bytes.log | `COVERAGE_REFERENCE_SNAPSHOT="$PWD/.superpowers/sdd/2026-10-18-equipment-coverage-part-b-reference-oracle/task-6r1-reference-bytes" build-slot --label ec-b6r1-before-bytes -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~Every_fixture_reference_output_is_compared_byte_for_byte_when_requested` | 0, 1 snapshot fact passed, 144 files written |
| task-6r1-enclosure-gate.log and .trx | Same snapshot environment, `build-slot --label ec-b6r1-enclosure-gate -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageReferenceEnclosureTests\|FullyQualifiedName~CoverageReferenceCrossingTests' --logger 'trx;LogFileName=task-6r1-enclosure-gate.trx' --results-directory .superpowers/sdd/2026-10-18-equipment-coverage-part-b-reference-oracle` | 1, 66 passed, 11 failed, 0 skipped. Every failure is a fixture normal-stage unbounded result |
| task-6r1-normal-trace.log | `build-slot --label ec-b6r1-normal-trace -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageReferenceEnclosureDiagnosticTests --logger 'console;verbosity=detailed'` | 0, 1 diagnostic passed, identified the zero-containing final basis divisor |

The first missing-type run satisfies the specified RED gate. The implemented height-conditioning, zero-containing area, tiny-sign/flush, final-store-neighbor and radius-composition tests passed. The fixture gate has not gone GREEN.

### Fixture summary

All 72 frozen rows ran in production reserve mode with an empty clearance override. Of them, 61 had finite model/world boxes, finite epsilon, every final CPU vertex contained and independent outward world centers contained. Eleven rows were unbounded in the normal stage. No row was omitted. No RV1 square-root-at-zero conflict was observed.

Finite epsilon range: 2.4931117404422644e-6 to 0.14068051387042352. The largest finite value is `cost-limit-l4-bent-w1`, 28.13610340297562 times delta = 0.004999999888241291. Its tracked dominant site is Correction. Across all rows the worst epsilon is positive infinity. Thirteen rows exceed delta, including eleven unbounded rows and two finite rows. The two finite budget overruns are report-only here and belong to Task 8. The eleven unbounded rows fail Task 6.

Every fixture's individual epsilon, delta and stage is in `task-6r1-fixtures.log`. Every row above delta is listed below. For an infinite row, the site is its first unbounded stage, not a finite radius attribution.

| Fixture | Epsilon | Delta | Dominant or first unbounded site |
| --- | --- | --- | --- |
| cost-limit-l2-bent-w1 | inf | 0.004999999888241291 | Normals |
| cost-limit-l2-lifted-w05 | inf | 0.004999999888241291 | Normals |
| cost-limit-l2-lifted-w1 | inf | 0.004999999888241291 | Normals |
| cost-limit-l3-lifted-w05 | inf | 0.004999999888241291 | Normals |
| cost-limit-l3-lifted-w1 | inf | 0.004999999888241291 | Normals |
| cost-limit-l4-bent-w05 | 0.070340573385754723 | 0.004999999888241291 | Correction |
| cost-limit-l4-bent-w1 | 0.14068051387042352 | 0.004999999888241291 | Correction |
| cost-limit-l4-lifted-w05 | inf | 0.004999999888241291 | Normals |
| cost-limit-l4-lifted-w1 | inf | 0.004999999888241291 | Normals |
| cost-typical-l2-lifted-w05 | inf | 0.004999999888241291 | Normals |
| cost-typical-l2-lifted-w1 | inf | 0.004999999888241291 | Normals |
| cost-typical-l4-lifted-w05 | inf | 0.004999999888241291 | Normals |
| cost-typical-l4-lifted-w1 | inf | 0.004999999888241291 | Normals |

### Warm allocation and remaining concerns

The warmed Evaluate allocation assertion measured zero managed bytes, but its chosen fixture is unbounded and returns in normal evaluation. That proves zero allocation for the executed abort path only. It does not complete the warm successful full-world/equipment acceptance gate.

The same test's permutation/reset assertions pass on unbounded outputs, so they are not proof of the required finite-valid, unbounded, finite-valid reset or finite command-reorder invariance. Those tests must be moved to a finite fixture when work resumes. Full-solid outlier, nonuniform model, large-translation cancellation, independent support/nearest-tie alternatives and synthetic uncertain-guard acceptance checks also remain incomplete under the required stop. No such coverage is claimed.

No commit was made because the required fixture gate fails. Root must resolve or direct refinement of the normal-stage enclosure before this API can be accepted. The source drafts and `task-6r1-implementation.patch` preserve all 14 owned paths for that continuation. Its reverse check exits 0, with no application performed. `task-6r1-source-manifest.log` records their hashes. The branch remains at `8765a4c27` with no staged paths, as recorded in `task-6r1-end-state.log`. No manual playtest or native run is needed for this headless task.

### Final verification after the last source edit

The numerical source stopped before cleanup. After the last source edit, the Release build exited 0 with 0 warnings and 0 errors. The requested reference/capture filter exited 1 with 486 passed, 11 failed and 0 skipped out of 497. All 418 pre-existing tests pass, including every unchanged fidelity and attribute case. Both analytic rational regressions and the comparison of all 144 reference outputs pass. The 72 fixture output rows are identical to the first enclosure gate run, recorded in `task-6r1-final-fixtures.log` and `task-6r1-result-summary.json`.

B13 whitespace verification, git diff check, dash and prose guards exited 0. The formatter emitted its generic workspace-loading warning, not a compiler warning. The prose guard reported an existing baseline-reduction advisory. Neither baseline nor warning policy was changed. The size guard also exited 0. It reported an existing Scene3D.cs baseline-reduction advisory. No baseline was changed.

`task-6r1-final-verify.py` records exact commands, complete per-command logs and exit codes in `task-6r1-final-exits.json`. The full solution suite remains root's later finish, as the task context requires. The .NET API signatures were checked against the official [Math.ScaleB](https://learn.microsoft.com/en-us/dotnet/api/system.math.scaleb?view=net-10.0), [Math.BitIncrement](https://learn.microsoft.com/en-us/dotnet/api/system.math.bitincrement?view=net-10.0) and [Vector4.Item](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.vector4.item?view=net-10.0) references.


All required commands completed after the last source edit. Exact commands and exits:

`task-6r1-final-build.log`, exit 0:

```bash
build-slot --label ec-b6r1-final-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-restore
```

`task-6r1-final-tests.log`, exit 1:

```bash
build-slot --heavy --label ec-b6r1-final-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests' --logger 'trx;LogFileName=task-6r1-final-tests.trx' --results-directory .superpowers/sdd/2026-10-18-equipment-coverage-part-b-reference-oracle
```

`task-6r1-final-format.log`, exit 0:

```bash
build-slot --label ec-b6r1-final-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include KhaozEngine.Render3D/Coverage/CoverageReferencePredicates.cs KhaozEngine.Render3D/Coverage/CoverageReferenceSupport.cs KhaozEngine.Render3D/Coverage/CoverageEnclosureCorrection.cs KhaozEngine.Render3D/Coverage/CoverageEnclosureMath.cs KhaozEngine.Render3D/Coverage/CoverageEnclosureNormals.cs KhaozEngine.Render3D/Coverage/CoverageEnclosureOverlay.cs KhaozEngine.Render3D/Coverage/CoverageEnclosurePose.cs KhaozEngine.Render3D/Coverage/CoverageEnclosurePredicates.cs KhaozEngine.Render3D/Coverage/CoverageEnclosureSupport.cs KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosure.cs KhaozEngine.Render3D/Coverage/CoverageReferenceEnclosureScratch.cs KhaozEngine.Render.Tests/Render3D/CoverageReferenceCrossingTests.cs KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureTests.cs KhaozEngine.Render.Tests/Render3D/CoverageReferenceEnclosureDiagnosticTests.cs
```

`task-6r1-diff-check.log`, exit 0:

```bash
git diff --check
```

`task-6r1-dashes.log`, exit 0:

```bash
sh scripts/check-dashes.sh --tree
```

`task-6r1-prose.log`, exit 0:

```bash
sh scripts/check-prose.sh --tree
```

`task-6r1-size.log`, exit 0:

```bash
sh scripts/check-file-size.sh --tree
```

The test process received `COVERAGE_REFERENCE_SNAPSHOT` pointing to this ledger's `task-6r1-reference-bytes` directory. Source hashes still match `task-6r1-source-manifest.log`. `task-6r1-implementation.patch` passes `git apply --reverse --check` without applying it. No staged changes or commit exist.

Completion is held at the normal-enclosure finiteness gate. Root's next step is to direct its refinement, then complete the remaining acceptance tests and repeat the required verification before committing. No manual validation or release is involved.
