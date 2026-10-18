# Task 8 report

Status: BLOCKED. No commit was created. HEAD remains `ac4b1a0c7` on
`feature/equipment-coverage-oracle` in `/Users/antonio/KhaozEngine/.worktrees/ec-oracle`.
The six-path implementation draft is preserved in the assigned worktree, unstaged.

## Blocking fixture gate

The brief requires every frozen fixture to pass and says a case failing at any stage returns to root,
without omission or relaxation. Six full-weight challenging-collar cases fail exact certification.
All construct successfully. Their complete refusal records have stage `Certification`, code
`EquipmentIntersection`, host `synthetic-host`, equipment instance `1`, command `1` and limit `0`.

| Frozen case | Count |
| --- | --- |
| `curved-challenging-seams-l2-w1` | 128 |
| `curved-challenging-welded-l2-w1` | 128 |
| `curved-challenging-seams-l3-w1` | 218 |
| `curved-challenging-welded-l3-w1` | 218 |
| `curved-challenging-seams-l4-w1` | 345 |
| `curved-challenging-welded-l4-w1` | 345 |

The oracle passes the original commands, production reference `Before` and final output to the
unchanged public exact certifier. No tolerance, override, padded clearance or alternative certification
path is used. The other 66 frozen cases pass. No case was omitted. The broad final filter confirms the
same six failures and no additional failures. The 144-output hash test passes unchanged.

I stopped implementation at this gate. I did not change the certifier, reference arithmetic,
construction, fixtures, manifest or refusal codes, and did not guess a workaround. The root must
investigate and disposition the fixture/certification conflict before this task can be committed.
The evidence does not independently identify which numerical or fixture assumption needs changing.

## Implemented draft and file ranges

| File | Lines | Draft behavior |
| --- | --- | --- |
| `KhaozEngine.Render3D/Coverage/CoverageOracle.cs` | 1 to 96 | Public reusable owner, copied inverse binds, cold validation, fixed scratch, pinned composition and skinning, construction then exact certification, failure propagation and internal observations |
| `KhaozEngine.Render3D/Coverage/CoverageOracleResult.cs` | 1 to 19 | Two-stage enum and immutable failure/certificate records, with XML documentation |
| `KhaozEngine.Render.Tests/Render3D/CoverageOracleTests.cs` | 10 to 267 | All 72 frozen cases, negatives, reuse, B2, B31, B38, partial transitions, creation/capacity guards and stable numbering |
| `KhaozEngine.Render.Tests/Render3D/CoverageOracleTests.cs` | 269 to 328 | Cold test adapters, pinned skin helper and independent small protected-collar fixture |
| `KhaozEngine.Render.Tests/Render3D/CoverageOracleAllocationTests.cs` | 9 to 45 | Nonparallel allocation correctness proof with precreated state/delegate and five warm checks |
| `docs/USING-KHAOZENGINE.md` | 6417 to 6498 | Consumer contract, full typed headless example, ownership, failures, flushing, device contract and sampled/partial-pose limits |
| `KhaozEngine.Render3D/README.md` | 154 to 158 | Headless guide link beside the preserved runtime description |

Only the listed six paths are changed or untracked. No changes were staged. The existing runtime,
`CoverageFailure.cs` and `CoverageExactnessSite.cs` remain untouched. B38 governs the draft: no
SubnormalArithmetic code, DeviceExactness stage or failure Site is added.

## Public API as built

```csharp
public enum CoverageOracleStage
{
    None = 0,
    Construction = 1,
    Certification = 2,
}

public readonly record struct CoverageOracleFailure(
    CoverageOracleStage Stage, CoverageFailure Failure);

public readonly record struct CoverageOracleCertificate(
    bool FullCoreClearance, double MinimumCoreClearance, int DisplacedVertices);

public sealed class CoverageOracle
{
    public CoverageOracle(CoverageRefinement topology,
        ReadOnlySpan<Matrix4x4> inverseBind,
        int commandCapacity = CoverageScratch.MaxCommands);
    public CoverageRefinement Topology { get; }
    public bool TryCheck(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands,
        out CoverageOracleCertificate certificate, out CoverageOracleFailure failure);
}
```

Creation accepts 1 to 128 finite affine inverse binds, snapshots them, validates palette indices
through `CoverageReferenceSkin` and requires 1 to 4 command capacity. A palette-length mismatch throws
`ArgumentException` before construction. Commands require one through the configured capacity.
Each check clears the certificate, candidate exposure and diagnostic arithmetic state.
`CoverageReferenceSkinning.Compose` and `TrySkin` provide the pinned source surface. Construction
uses production reserve mode. Certification uses `CoverageCertification.ValidateGeometry` with the
original commands and approved clearance. The oracle owns no GPU resource and is not disposable or
thread safe. It grants no drawable permission.

Internal B11 observations are `ReadOnlySpan<ModelVertex> LastConstructedVertices` (empty after a
construction refusal and invalidated by the next call) and `int CertificationRunsForTests` (incremented
only when exact validation is called, without a delegate). These are not public drawing surfaces.

## Negatives, stages and codes

| Scenario | Stage | Code and evidence |
| --- | --- | --- |
| Existing support translated `.9f` | Construction | CoreEscaped, command 1, positive count, entire failure equals the reference constructor result, zero certification calls |
| Existing recession budget `.1f`, full and zero transition | Construction | RecessionExceeded, command 1, positive count, entire failure equals the reference constructor result, zero certification calls |
| Independent protected collar at `z = -.01f`, solid sheet at `z = 0`, core at `z = -.1f`, clearance `.02f`, caps `[-2,2]`, budget `.8f` | Certification | EquipmentIntersection, command 1, positive count, nonempty constructed output, entire failure equals direct exact validation |
| B2 `unabsorbed-error`, clearance `1e-6f` | Certification | UnsupportedTransform, command 1, nonempty output, one certification call, default certificate |
| Nonfinite skinning palette | Construction | Refusal clears prior candidate, skips exact validation and allows the next successful check to reproduce its original certificate and bytes |
| Empty or oversized command span at capacity 1 | Construction | CapacityExceeded, zero equipment and command IDs, exact supplied count and limit 1, zero certification calls |
| Subnormal palette plus escaped support | Construction | CoreEscaped, unchanged certification-call count, no extra stage |

The small collar fixture pins its host and solid coordinates in the test. Before calling the oracle,
`CoverageTriangleGeometry.SeparationLowerBound` confirms its positive gap lies in `[.00999999, .01]`.
Core vertices are all behind the sheet. It is a separation failure after successful construction and
normal guards, not the tiny-clearance frame-admission negative.

Both signs of `2^-130` in joint 1 M12 of `cost-typical-l2-lifted-w1` construct and certify successfully,
then reproduce exactly the final bytes and certificate of the same coefficient replaced by signed zero.
The B38 flush trace never refuses a sample. The B31 inverse-bind translations `-.25f` and `+.125f`
with opposite joint translations match an independently invoked pinned skinning/constructor path.
Mutating the caller's inverse-bind array afterwards leaves the oracle unchanged. Zero-weight source
bones retain identity skinning under a translated palette.

Partial weights 0 and .5 return `FullCoreClearance = false` and `MinimumCoreClearance = NaN`.
At weight zero all unconformed reference vertex bytes remain exact. Existing failure codes 0 through
14 keep their values, code 15 remains rejected and the oracle has exactly stages 0, 1 and 2.

## Warm allocation result

`CoverageOracleAllocationTests` uses the existing `AllocSensitive` collection whose definition has
`DisableParallelization = true`. All fixtures, command arrays, palettes, oracle instances, boolean and
failure arrays and the Action delegate are created before measurement. The delegate is warmed once,
then `AllocationMeasurement.Measure` executes it once in its checked no-GC region.

The five calls are success, escaped-core refusal, budget refusal, protected-collar exact separation
refusal and success recovery. Results are `[true, false, false, false, true]`. Assertions and output
formatting occur after measurement.

```text
Warm oracle allocation: 0 bytes across five checks.
```

The allocation test passes in both the initial implementation run and the final required filter.
It measures allocation correctness only. No timing pilot, stress proof, iteration loop or native
backend claim is made. Full output is retained in `task-8-green.trx`, `task-8-final-tests.trx` and
`task-8-results-summary.log`.

## TDD and verification commands

Every command below ran from the assigned worktree. `task-8-run.py` executes the literal argument
vector synchronously, redirects complete stdout/stderr to `task-8-<name>.log`, records the exact
shell-quoted command and appends `EXIT_CODE`. No build-slot call returned 75. There was no test loop.

### Red before production code

```bash
build-slot --label ec-b8-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageOracleTests|FullyQualifiedName~CoverageOracleAllocationTests'
```

`task-8-red.log`, exit 1. The public API did not exist: 22 CS0246 missing-type diagnostics and 11
CS0103 missing-stage-name diagnostics. One CS0619 Assert.Throws overload-resolution diagnostic
also appeared while its oracle receiver was unresolved. It disappeared without changing that test
when the missing API was supplied. All 93 test cases were written before the production files.

### Initial implementation run

```bash
build-slot --label ec-b8-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageOracle' --logger 'trx;LogFileName=task-8-green.trx' --results-directory .superpowers/sdd/2026-10-18-equipment-coverage-part-b-reference-oracle
```

`task-8-green.log`, exit 1. This is an attempted green run, not a passing TDD cycle.

```text
Failed!  - Failed: 6, Passed: 87, Skipped: 0, Total: 93
```

The six cases above trigger the return-to-root gate. No implementation source edit followed their
observation. The final required checks below then completed for the blocked handoff.

### Final Release build

```bash
build-slot --label ec-b8-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
```

`task-8-final-build.log`, exit 0.

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Final required coverage and exact filter

```bash
build-slot --label ec-b8-tests -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CoverageOracle|FullyQualifiedName~CoverageReference|FullyQualifiedName~CoverageExactCaptureTests|FullyQualifiedName~CoverageCertificationTests' --logger 'trx;LogFileName=task-8-final-tests.trx' --results-directory .superpowers/sdd/2026-10-18-equipment-coverage-part-b-reference-oracle
```

`task-8-final-tests.log`, exit 1.

```text
Failed!  - Failed: 6, Passed: 537, Skipped: 0, Total: 543
```

Only the same six new frozen-oracle fixture assertions fail. Existing reference, exact-capture and
certification tests all pass, including
`CoverageReferenceOutputTests.Every_fixture_reference_output_matches_the_hash_manifest`.
The whole-solution suite belongs to the later orchestrator finish and was not run here.

### Analyzer suite

```bash
build-slot --label ec-b8-analyzers -- dotnet test KhaozEngine.CodeHealth.Analyzers.Tests/KhaozEngine.CodeHealth.Analyzers.Tests.csproj -c Release
```

`task-8-analyzers.log`, exit 0.

```text
Passed!  - Failed: 0, Passed: 275, Skipped: 0, Total: 275
```

### Scoped whitespace check

```bash
build-slot --label ec-b8-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include KhaozEngine.Render3D/Coverage/CoverageOracle.cs KhaozEngine.Render3D/Coverage/CoverageOracleResult.cs KhaozEngine.Render.Tests/Render3D/CoverageOracleTests.cs KhaozEngine.Render.Tests/Render3D/CoverageOracleAllocationTests.cs
```

`task-8-format.log`, exit 0, no whitespace violations. Paths are repo-relative from the worktree root.
The formatter also emitted this workspace-loading warning, so its exit 0 is not a warning-free
workspace-load claim:

```text
Warnings were encountered while loading the workspace. Set the verbosity option to the 'diagnostic' level to log warnings.
```

The Release build itself has zero warnings. No extra format run or unrelated workspace change was
made after the explicit fixture stop.

## Guard results

The exact single starting guard printed the required branch and `ac4b1a0c7`, with exit 0 and no status
entries under the guarded scopes. Its transcript is `task-8-starting-guard.log`.

| Command | Log | Exit and output |
| --- | --- | --- |
| `git diff --check` | `task-8-diff-check.log` | 0, no output |
| `sh scripts/check-dashes.sh --tree` | `task-8-dashes.log` | 0, no dash violations |
| `sh scripts/check-prose.sh --tree` | `task-8-prose.log` | 0, no prose violations |
| `sh scripts/check-file-size.sh --tree` | `task-8-file-size.log` | 0, existing Scene3D.cs baseline can be ratcheted down |
| `bash scripts/check-doc-versions.sh` | `task-8-doc-versions.log` | 0 |
| `git diff --exit-code ac4b1a0c7 -- KhaozEngine.Render3D/Coverage/CoverageCertification.cs 'KhaozEngine.Render3D/Coverage/CoverageReference*.cs' KhaozEngine.Render3D/Coverage/CoverageFailure.cs KhaozEngine.Render3D/Coverage/CoverageExactnessSite.cs KhaozEngine.Render.Tests/Fixtures KhaozEngine.CodeHealth.Analyzers KhaozEngine.CodeHealth.Analyzers.Tests` | `task-8-immutable-paths.log` | 0, no output |
| `git status --short` | `task-8-status.log` | 0, only the six listed owned paths |

The size guard reports that unchanged `KhaozEngine.Render3D/Scene3D.cs` has 2188 lines against a
2228-line baseline. It is informational, exits 0 and was not actioned in this task.

The Markdown name/claim sweep is saved in `task-8-doc-sweep.log`. The only matching living references
outside historical design and plan documents are the two assigned references. Their existing runtime
description is preserved for Part C's planned API/doc sweep. No historical design was rewritten.

## Concerns and next action

The task is not complete and its TDD cycle is not green. Six frozen challenging-collar samples refuse
exact geometry certification after successful reference construction. Root must decide and authorize
the proper follow-up investigation. No refusal is masked and no numerical gate is relaxed.

No manual or windowed validation is needed. No native device, Scene3D, client or render capture was
created. Device bit-equality and real-GPU verification remain Part C, and deterministic consumer
sampling, blend grids and asset inventory remain Part D.

There was no commit because the mandated correctness gate failed. The draft and logs are preserved
for root in the named worktree. No push, merge, rebase, tag, branch change, stash, subagent dispatch,
other-worktree write, version bump or pack occurred. Root owns continuation, review, integration and
pushing after this blocker is resolved.
