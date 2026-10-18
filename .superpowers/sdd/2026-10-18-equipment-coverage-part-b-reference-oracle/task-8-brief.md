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

