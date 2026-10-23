# Renderer Cold Publication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (the owner-approved method) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Root assigns one writer and a fresh independent reviewer to each task. This planning dispatch performs no implementation or review dispatch.

**Goal:** Connect actual workload construction to truthful cold compiler evidence and verified bounded publication without changing the approved measurement unit.

**Architecture:** Capture immutable factory inputs and backend construction witnesses during the fixed workload. After production cleanup, replay only those graphs on an explicitly configured diagnostic device and prove correspondence. One lifecycle owner carries the original deadline through both device disposals, registered sidecar verification and final stamping.

**Tech Stack:** .NET 10, existing optional GPU diagnostics, Vulkan/D3D11/Metal producers, xUnit, strict JSON and the existing renderer workflow acceptance path.

**Spec:** [Canonical cold publication amendment](../specs/2026-10-24-renderer-cold-publication-design.md). Read it with the [approved parent plan](2026-10-21-renderer-performance-baseline.md). CP-1 through CP-6 in the root ledger resolve the technical choices. The proposal is historical evidence, not a competing specification.

## Global Constraints

- Preserve the five scenes, twelve controls, manifest order, layouts, pacing, conditioning, warm-up and measured counts. A full unit has exactly 16,944 frames over 43 operations (one conditioning operation, 30 scene blocks and twelve controls). A pilot has nine operations.
- Preserve the 600/1,500-second pilot/full unit limits, 10,000 ms individual frame limit, 12/35-minute job caps and all original spend and approval gates. The existing post-authorization `coldStart` is the sole clock origin.
- Timed devices use compiler metadata off and runtime invocations off. Replay uses compiler metadata on and runtime invocations off. No process-global environment mutation is permitted. Retain the native creation gate, named-backend selection and no fallback.
- Keep the root cap at 268435456 bytes and JSON depth 32, including stamp padding. The root plus newly produced compiler sidecars also has a combined cap of 268435456 bytes. Keep `raw.format = metric-vectors/2` unchanged. Adopt `khaozengine.renderer-perf-report/2` and explicitly reject report/1.
- Resource counts mean current live counts at retained observations plus actual backend-maintained peak logical payload bytes. Do not add `PeakLiveResourceCount`. Diagnostic memory cannot raise the production peak.
- No extra scene, frame, submission, calibration, invocation block, profiler, counter sampling, automatic retry, fallback, native/paid/workflow run or release is authorized. Headless tests named below are future implementation checks, not commands executed by this planning task.
- Source boundary is clean pushed `e95154ffdd856269be54d0fea387689d6b974fa6` on `feature/renderer-performance-baseline`. Preserve staged 21.13.0. Root owns reconciliation, fresh independent reviews and push. Recheck release history before integration.
- All new counts/encodings/text reservations use the exact limits and domain strings in spec sections 3 and 6. No silent pruning or larger cap is an implementation choice.

## Review Focus

1. Caller arrays mutate or originals are disposed before replay. Task 2 tests detached nested values and original-native-object binds.
2. Recompilation changes an artifact while configuration hashes legitimately differ. Tasks 1 and 3 test actual stage/aggregate artifacts, code-generation identity and the allowlisted effective delta independently.
3. A repeated recipe changes its source hash or deduplicates differently between runs. Tasks 2 and 4 test occurrence-key joins, collision value checks and unequal role membership.
4. Cancellation, expiry or cleanup failure arrives after the last frame or before trustworthy native identity. Task 3 uses held clocks/completions at every boundary, retaining first/all later errors without invented identity.
5. A nonempty compiler payload fits in memory but its files are missing, aliased, changed or over the combined cap. Task 4 tests actual padded files, decoded totals and directory verification through both offline and workflow entrypoints.

## File and ownership map

`R/` means `KhaozEngine.Render.Tests/Gpu/RendererPerf/`. Backend names in this table are exact directory prefixes, not cross-repository work.

| Task | Create | Modify existing owners |
| --- | --- | --- |
| 1. Identity and strict shape | `KhaozEngine.Gpu/GpuPipelineConstructionIdentity.cs`, `GpuDiagnosticDeviceConfiguration.cs`, `Internal/GpuDiagnosticIdentityEncoding.cs`, `R/RendererPerfCompilerContracts.cs`, `RendererPerfCompilerProtocolTests.cs` | `KhaozEngine.Gpu/IGpuPipelineDiagnostics.cs`, all three backends' `Internal/*PipelineDiagnostics.cs`, `*PipelineIdentity.cs`, graphics/compute pipeline owners and shader evidence owners. Vulkan `VulkanShaderSet.cs`, `VulkanComputeShader.cs`, D3D `D3D11ShaderCompiler.cs`, `D3D11ShaderReflectionApi.cs`, Metal `MetalShaderEvidence.cs`. `R/RendererPerfContracts.cs`, `RendererPerfRawJson.cs`, `RendererPerfReportJson.cs`, `RendererPerfDriver.cs` and existing protocol/backend diagnostics fixture partials |
| 2. Immutable capture | `R/RendererPerfConstructionRecipes.cs`, `RendererPerfConstructionCapture.cs`, `RendererPerfConstructionEncoding.cs`, `RendererPerfCompilerCorrespondence.cs`, `RendererPerfConstructionTests.cs`, `RendererPerfConstructionFixture.cs` | `R/RendererPerfDevice.cs`, `RendererPerfRecorder.cs`, `RendererPerfCommandList.cs`, `RendererPerfNativeSession.cs`, `RendererPerfCounterTests.cs` |
| 3. Settings and lifecycle | `KhaozEngine.Gpu/GpuDeviceContext.Diagnostics.cs`, each backend's `Internal/*HeadlessDiagnosticSettings.cs`, `Internal/*GpuDevice.DiagnosticConfiguration.cs`, `R/RendererPerfUnitBudget.cs`, `RendererPerfUnit.cs`, `RendererPerfColdCollector.cs`, `RendererPerfColdLifecycleTests.cs`, `RendererPerfDiagnosticConfigurationTests.cs`, `KhaozEngine.Render3D/Scene3D.Disposal.cs` | `KhaozEngine.Gpu/GpuDeviceContext.cs`, `GpuDeviceContext.Create.cs`, `KhaozEngine.Render3D/Scene3D.cs` (move its existing Dispose owner to the new cohesive partial), backend `*BackendProvider.cs`, `*GpuDevice.Create.cs`, Vulkan `VulkanPipelineExecutableApi.cs`, D3D `D3D11ShaderCompiler.cs`, relevant resource factory/compiler constructors. `R/RendererPerfEntrypoint.cs`, `RendererPerfDriver.cs`, `RendererPerfSession.cs`, `RendererPerfNativeSession.cs`, `RendererPerfSceneWorkload.cs`, `RendererPerfControlWorkload.cs`, `RendererPerfMemoryEvidence.cs`, existing driver/memory fixtures |
| 4. Files and acceptance | `R/RendererPerfCompilerJson.cs`, `RendererPerfSidecarRegistry.cs`, `RendererPerfArtifactVerification.cs`, `RendererPerfCompilerPublicationTests.cs`, `tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj`, `tools/Renderer.Perf.Verify/Program.cs` | `R/RendererPerfPublication.cs`, `RendererPerfPublicationFiles.cs`, `RendererPerfEntrypoint.cs`, `RendererPerfAnalysis.cs`, `RendererPerfComparison.cs`, existing compact/size/raw/sidecar/completion/workflow fixtures, `KhaozEngine.Render.Tests/RendererPerfAssemblyAccess.cs`, `scripts/renderer-perf-workflow.ps1`, `.github/workflows/gpu-hosted-bench.yml`, GPU/backend READMEs, `docs/USING-KHAOZENGINE.md`, `docs/RENDERER-PERFORMANCE.md`, `docs/CROSS-PLATFORM.md`, `docs/verification/2026-10-21-renderer-performance-baseline.md` |

Each implementation task commits its own explicit files. Overlapping owners are sequential, never concurrent. Keep helpers in cohesive new files and existing owning types, with no size-baseline increase. No Scene3D collector, mandatory GPU interface expansion, project dependency change or new native frame loop is planned. The verifier is an outside-solution console tool referencing Render.Tests, following the existing performance-tool assembly-access pattern.

## Task 1: Truthful identity and strict report/2 contracts

**Files:** Task 1 row above. Extend `RendererPerfProtocolTests`, `RendererPerfRawValidationTests`, `RendererPerfVulkanDiagnosticsTests.Wiring.cs`, `RendererPerfD3D11DiagnosticsTests.Compiler.cs`/`.Owners.cs` and `RendererPerfMetalDiagnosticsTests` with their current fake native seams. Do not execute GPU facts.

**Consumes:** Existing `GpuDiagnosticStatus`, `GpuPipelineExecutableReport`, `GpuPipelineStatistic`, `GpuDiagnosticValue`, `GpuDiagnosticMetric`, native shader bytes/resolved state and strict report codec. Existing `ReadCompilerReport()` signature is retained.

**Produces:** The following public declarations in the two named GPU contract files. Bodies below are declarations only.

```csharp
public sealed record GpuDiagnosticDigest(string? Sha256, string Source,
    GpuDiagnosticStatus Status, string? Reason);
public sealed record GpuPipelineStageIdentity(string Stage, string EntryPoint,
    GpuDiagnosticDigest SourceSha256, GpuDiagnosticDigest? EmittedSourceSha256,
    GpuDiagnosticDigest ShaderArtifactSha256);
public sealed record GpuPipelineConstructionIdentity(
    GpuDiagnosticDigest PipelineSha256, GpuDiagnosticDigest SourceSha256,
    GpuDiagnosticDigest ShaderArtifactSha256, GpuDiagnosticDigest LayoutSha256,
    GpuDiagnosticDigest DiagnosticConfigurationSha256, GpuDiagnosticDigest CodeGenerationSha256,
    string CompilerFlags, uint LocalSizeX, uint LocalSizeY, uint LocalSizeZ,
    ImmutableArray<GpuPipelineStageIdentity> Stages);
public interface IGpuPipelineConstructionDiagnostics
{
    GpuPipelineConstructionIdentity ConstructionIdentity { get; }
}
public sealed record GpuPipelineCompilerReport(GpuPipelineConstructionIdentity Identity,
    GpuDiagnosticStatus Status, string? Reason,
    ImmutableArray<GpuPipelineExecutableReport> Executables);
public sealed record GpuPipelineDiagnosticRequest(bool CompilerMetadata, bool RuntimeInvocations);
public sealed record GpuDiagnosticOption(string Key, string Value);
public sealed record GpuEffectiveDiagnosticConfiguration(GpuBackendKind Backend,
    string AdapterKey, string Driver, GpuPipelineDiagnosticRequest Requested,
    bool CompilerMetadataEnabled, bool RuntimeInvocationsEnabled, bool DebugLayerEnabled,
    bool ValidationEnabled, bool PipelineStatisticsSupported, bool PipelineExecutableFeatureEnabled,
    uint PipelineCaptureFlags, ImmutableArray<GpuDiagnosticOption> CodeGenerationOptions,
    ImmutableArray<GpuDiagnosticOption> ObservationOptions);
public interface IGpuDeviceDiagnosticConfiguration
{
    GpuEffectiveDiagnosticConfiguration DiagnosticConfiguration { get; }
}
public interface IGpuHeadlessDiagnosticSettings
{
    GpuBackendKind Backend { get; }
    GpuPipelineDiagnosticRequest Diagnostics { get; }
    IGpuHeadlessDiagnosticSettings WithDiagnostics(GpuPipelineDiagnosticRequest request);
    IGpuHeadlessDiagnosticSettings WithAdapter(string adapterKey);
}
public interface IGpuHeadlessDiagnosticProvider
{
    IGpuHeadlessDiagnosticSettings ResolveHeadlessSettings(GpuPipelineDiagnosticRequest request);
    GpuProviderDevice CreateHeadless(IGpuHeadlessDiagnosticSettings settings);
}
```

`IGpuHeadlessDiagnosticProvider` is optional beside `IGpuBackendProvider`. Task 1 declares it and Task 3 implements it. Settings are backend-owned immutable values, not a bag the harness edits. `WithAdapter` pins the production-selected UUID/LUID for replay. Wrong-backend or foreign settings refuse.

`GpuDiagnosticIdentityEncoding` produces exact shared identities with these internal signatures: `GpuDiagnosticDigest Stages(string field, ImmutableArray<GpuPipelineStageIdentity> stages)`, `GpuDiagnosticDigest CodeGeneration(GpuBackendKind backend, ImmutableArray<GpuDiagnosticOption> options)`, `GpuDiagnosticDigest Configuration(GpuEffectiveDiagnosticConfiguration configuration)`. Use spec section 3 encoding, with independent golden bytes. Backend-specific Pipeline/Layout digests retain their stated encodings. Precompute construction identity in pipeline construction, returning the same immutable object after disposal. A live compiler read after disposal remains Invalid.

Declare `RendererPerfCollectionState { Complete, Incomplete, Invalid, Cancelled, DeadlineExceeded }`, `RendererPerfPipelineKind { Graphics, Compute }`, `RendererPerfConstructionStage { Cold, Warmup, Measured }` and `RendererPerfKindCounts(int Graphics, int Compute)` in `RendererPerfCompilerContracts.cs`. Define these internal transport records there.

| Record | Constructor fields in order |
| --- | --- |
| `RendererPerfCompilerCounts` | `int FactoryAttempts`, `RendererPerfKindCounts PipelineAttempts`, `SuccessfulCreations`, `Occurrences`, `ReplayAttempts`, `Reports`, then `int UniqueRecipes`, `int ReplayEligibleRecipes`, `int BoundOccurrences`, `int Executables`, `int Statistics`, `int OmittedFactoryEvents` |
| `RendererPerfCompilerResult` | `int RecipeId`, `RendererPerfCollectionState State`, `string? Reason`, `int? PipelineIndex`, `ImmutableArray<int> OccurrenceIds` |
| `RendererPerfCompilerEvidence` | `RendererPerfCollectionState State`, `string? Reason`, `RendererPerfCompilerCounts Counts`, `GpuEffectiveDiagnosticConfiguration? Production`, `GpuEffectiveDiagnosticConfiguration? Diagnostic`, `ImmutableArray<RendererPerfCompilerResult> Results`, `string? InventoryPath`, `ImmutableArray<RendererPerfCpuSpan> Stages`, `ImmutableArray<string> Errors` |

Add required `RendererPerfCompilerEvidence Compiler` to both `RendererPerfReport` and `RendererPerfRawJson.WireReport`. `Pipelines` remains the array of unique diagnostic reports, indexed explicitly from Results. The inventory DTO is produced by Task 2 and lives in a sidecar. Before Tasks 2/3 connect it, driver reports have explicit Incomplete evidence and cannot promote. Do not insert synthetic native evidence in the driver to keep old success fixtures green.

- [ ] **Step 1: Add failing contract tests.** `RendererPerfCompilerProtocolTests.Typed_availability_preserves_valid_companions` asserts Metal Artifact Unsupported/null/cause, valid source retained, UInt64.MaxValue, Int64.MinValue/MaxValue, Boolean and finite Float64 round-trip unchanged. `Known_hashes_and_report_versions_are_strict` rejects empty/uppercase/nonhex hashes, unavailable hashes with values, Available with reasons, missing/null/duplicate properties, report/1 and metric-vectors/1. `Invalid_or_incomplete_collection_cannot_promote` retains raw frames/statistics and sets promotion false. Independent byte vectors test stage order, field selector and signed float bits.
- [ ] **Step 2: Run the focused selection below once to observe the missing-contract failure.** Expected FAIL identifies absent types/shape or assertion, not unrelated native capability. No GPU/device fact is selected.
- [ ] **Step 3: Implement shared records, encodings and all prerelease producers together.** Preserve raw flags and per-stage real hashes. Add backend-produced code-generation identities from actual pins/options/native flags. Factor D3D code-generation options from the actual `_flags`, entry/profile and frontend/cross-compiler pins already assembled before the reflection branch in D3D11ShaderEvidence.Capture. Its existing diagnostic suffix and reflection.Compiler enrichment remain raw observation metadata. Do not add a guessed production compiler version or invoke reflection to populate code generation. Retain Creator as observation metadata, with `ID3D11ShaderReflectionApi.Read(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes)` and bound Creator at the native GetDesc pointer before managed string conversion. Pass 4096, retain all 30 named counts, and enforce 16384 UTF-8 bytes for raw flags. Update fake reflection adapters. Empty bytes never become an Available artifact. Update Metal to truthful absence without MSL substitution. D3D/Vulkan supported-but-missing capture must become Invalid once effective options are supplied by Task 3.
- [ ] **Step 4: Extend owner tests.** `Construction_getter_is_precomputed_and_survives_disposal` asserts getter returns the same object and performs zero additional native/reflection calls while live reads still refuse disposed owners. `Actual_artifact_or_layout_change_changes_identity` alters one byte/state field and checks the witness. `Primary_native_output_reservations_cover_maximum_encoded_fields` independently serializes Vulkan 64-by-256 maximal native strings/scalars and D3D two-by-30 fields with worst escaping, proves the spec formulas and one-over refusal. Test D3D oversize Creator before decoder invocation and raw flag limits. No zero-allocation claim is inferred without an isolated region.
- [ ] **Step 5: Run the focused selection again.** `build-slot --label renderer-cold-identity -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~RendererPerfCompilerProtocolTests|FullyQualifiedName~RendererPerfProtocolTests|FullyQualifiedName~RendererPerfRawValidationTests|FullyQualifiedName~RendererPerfVulkanDiagnosticsTests|FullyQualifiedName~RendererPerfD3D11DiagnosticsTests|FullyQualifiedName~RendererPerfMetalDiagnosticsTests'`. Expected exit 0, all selected headless cases pass, no GPU measurement fact executes. Keep four Vulkan API enumeration attempts, with no measurement retry.
- [ ] **Step 6: Commit explicit Task 1 paths.** Subject `feat(gpu): add truthful compiler construction identities`. Fresh independent review must accept all producer/codec changes before Task 2. Runtime publication is still incomplete at this gate. Task 4 owns workflow acceptance and living-doc adoption before integration.

## Task 2: Immutable factory capture and occurrence correspondence

**Files:** Task 2 row. The collector is per unit, with operation-scoped native reference tables. It does not own native resource disposal or scene enumeration.

**Consumes:** Task 1 identity/configuration/collection records, existing factory signatures in `GpuInterfaces.cs`, native local sizes, existing recorder registration and raw per-frame growth/allocation evidence.

**Produces:** These exact immutable DTOs in `RendererPerfConstructionRecipes.cs`. Fields use existing GPU value structs, never GPU handles.

| Record | Constructor fields in order |
| --- | --- |
| `RendererPerfStageSource` | `string Stage`, `string EntryPoint`, `string Source`, `uint LocalX`, `uint LocalY`, `uint LocalZ` |
| `RendererPerfShaderRecipe` | `int Id`, `ImmutableArray<RendererPerfStageSource> Stages` |
| `RendererPerfLayoutRecipe` | `int Id`, `ImmutableArray<GpuResourceLayoutElement> Elements` |
| `RendererPerfVertexRecipe` | `uint Stride`, `uint InstanceStepRate`, `ImmutableArray<GpuVertexElement> Elements` |
| `RendererPerfGraphicsRecipe` | `Vector4 BlendFactor`, `ImmutableArray<GpuBlendAttachment> BlendAttachments`, `GpuDepthStencilState DepthStencil`, `GpuRasterizerState Rasterizer`, `GpuPrimitiveTopology Topology`, `ImmutableArray<RendererPerfVertexRecipe> VertexLayouts`, `GpuPixelFormat? Depth`, `ImmutableArray<GpuPixelFormat> Colour`, `int SampleCount` |
| `RendererPerfPipelineRecipe` | `int Id`, `RendererPerfPipelineKind Kind`, `int ShaderId`, `ImmutableArray<int> LayoutIds`, `RendererPerfGraphicsRecipe? Graphics`, `GpuDiagnosticDigest CodeGenerationSha256`, `string RecipeSha256` |
| `RendererPerfFactoryEvent` | `int OperationOrdinal`, `int Ordinal`, `RendererPerfFactoryKind Kind`, `int InputId`, `int? OutputId`, `string? Failure`. Kind enum is Shader, Layout, GraphicsPipeline, ComputePipeline. InputId selects the corresponding table, including failed attempts. OutputId identifies a successful logical resource, never a pointer |
| `RendererPerfPipelineOccurrence` | `int Id`, `int OperationOrdinal`, `int CreationOrdinal`, `RendererPerfConstructionStage Stage`, `int? FrameIndex`, `RendererPerfPipelineKind Kind`, `int RecipeId`, `GpuPipelineConstructionIdentity Production`, `long BindCount` |
| `RendererPerfCompilerInventory` | `string Schema`, `ImmutableArray<string> Operations`, `ImmutableArray<RendererPerfFactoryEvent> FactoryEvents`, `ImmutableArray<RendererPerfShaderRecipe> Shaders`, `ImmutableArray<RendererPerfLayoutRecipe> Layouts`, `ImmutableArray<RendererPerfPipelineRecipe> Recipes`, `ImmutableArray<RendererPerfPipelineOccurrence> Occurrences`, `RendererPerfCompilerCounts Counts`, `bool Complete`, `string? Reason` |

Cold provisional recipe IDs are finalized/remapped outside measured spans. Failed creation inputs remain recorded without a fabricated witness. A failed shader has zero unknown local sizes and a failure event, never an accepted compute recipe. All final IDs are dense zero-based in first-occurrence order and every reference is checked. A failed capture that cannot retain input uses the explicit omitted counter and incomplete state, not an invented table entry.

`RendererPerfConstructionCapture(RendererPerfManifest manifest)` provides `void BeginOperation(int operationOrdinal)`, `void EndOperation()`, `string? Failure { get; }`, `bool HasMeasuredCreation { get; }`, `void EnterFrame(RendererPerfConstructionStage stage, int frameIndex)`, `void LeaveFrame()`, `RendererPerfCompilerInventory Freeze()`. Factory capture hooks stay private/internal to the wrapper and collector. Add `RendererPerfDevice.Wrap(IGpuDevice native, RendererPerfRecorder recorder, RendererPerfConstructionCapture capture)` alongside the existing overload. Session construction accepts that same capture plus `int operationOrdinal`, appended to the existing constructor parameters. It calls BeginOperation before creating work, updates stage/frame at existing boundaries, exposes the first capture refusal or measured-creation cause through ReadInfo.Failure without losing a frame exception, and calls EndOperation after cleanup to drop native reference tables. Command bind checks flow through existing Facts registration. `Freeze()` is idempotent and refuses further capture.

`RendererPerfConstructionEncoding.Digest(RendererPerfPipelineRecipe recipe, ImmutableArray<RendererPerfShaderRecipe> shaders, ImmutableArray<RendererPerfLayoutRecipe> layouts)` returns `string` using spec section 3. `RendererPerfCompilerCorrespondence.Validate(RendererPerfCompilerInventory inventory, ImmutableArray<GpuPipelineCompilerReport> pipelines, RendererPerfCompilerEvidence compiler)` returns `ImmutableArray<string>` ordered refusal reasons. It verifies exact within-run source/layout/state/local-size/code-generation/artifact identities, every occurrence/result association, independent totals and effective configuration deltas. This pure checker is also consumed by Task 4.

- [ ] **Step 1: Add failing capture tests.** `RendererPerfConstructionTests.Mutable_inputs_and_disposed_originals_do_not_change_replay_values` mutates layout elements, nested vertex arrays/list, blend/color arrays after each factory call. Assert frozen values and encoded digest remain unchanged and binds receive the identical original native return. A recording fake independently counts each forwarded call exactly once, including failed creation.
- [ ] **Step 2: Run once for the missing collector failure.** Use the selection in Step 5. Distinguish missing capture from a fake workload bug.
- [ ] **Step 3: Implement the bounded append collector and canonicalization.** Preallocate tables for the spec's 43/256/1024 operation envelopes, enforce 11008/44032 totals, 1024 unique recipes, descriptor limits and 64 MiB logical sub-budgets before copies. Keep shader strings, final specialization and resolved local sizes. Forward original calls once, retain backend witnesses, preserve repeated and unused creations, detach native references at operation completion. Hash/deduplicate outside measured spans, value-check collisions and remap all references deterministically. Capture exhaustion latches incomplete evidence, preserves forwarding and the first capacity cause and asks the driver to stop at the next safe boundary. It does not silently continue a successful unit.
- [ ] **Step 4: Add independent behavioral cases.** `Lazy_temporal_and_specialized_ocean_are_actual_factory_inputs` exercises existing full/compact/depth-store and row/column fake construction at two resolutions, asserts final specialized sources/local sizes/layout ordering and created-but-unused membership. `Digest_collision_and_repeated_occurrences_keep_logical_roles` injects a colliding digest and asserts unequal values never deduplicate. `Unknown_bind_failed_creation_and_each_one_over_limit_refuse` asserts counts, reasons and partial coverage. `Measured_first_creation_keeps_frame_and_refuses_promotion` retains all raw CPU/allocation/growth evidence. `Warmed_bind_observer_has_no_growth` uses an isolated allocated-byte region including the observer, not capacity inspection. Compare witnesses after changing each output/sample/raster/vertex/local-size/compiler field. A fake trace asserts no compiler read or extra frame/submission during capture.
- [ ] **Step 5: Run focused verification.** `build-slot --label renderer-cold-capture -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~RendererPerfConstructionTests|FullyQualifiedName~RendererPerfCounterTests|FullyQualifiedName~RendererPerfCompilerProtocolTests'`. Expected exit 0 with independent forwarding totals, retained failure inputs, exact role membership and no hidden allocation-region growth.
- [ ] **Step 6: Commit explicit Task 2 paths.** Subject `feat(renderer): capture immutable pipeline construction`. Fresh independent review accepts capture and the pure checker. This gate proves fake factory coverage, not a certified native inventory size or artifact correspondence.

## Task 3: Explicit device settings and one cold lifecycle

**Files:** Task 3 row. Extend existing `RendererPerfDriverTests`, `RendererPerfMemoryEvidenceTests`, frame/batch failure fixtures and backend configuration fixture partials. Native providers are tested through replaceable factory seams without native execution.

**Consumes:** Task 1 optional provider/configuration and witness contracts, Task 2 capture/inventory/pure correspondence, existing `RendererPerfSessionFactory`, `RendererPerfClock`, report/publication error records and native session owners.

**Produces:** These exact internal/public entrypoints and handoff records.

```csharp
// GpuDeviceContext.Diagnostics.cs, public, through the existing creation gate.
public static IGpuHeadlessDiagnosticSettings ResolveHeadlessDiagnosticSettings(
    GpuBackendKind backend, GpuPipelineDiagnosticRequest request);
public static GpuDeviceContext CreateHeadless(GpuBackendKind backend,
    IGpuHeadlessDiagnosticSettings settings);
// RendererPerfUnitBudget.cs, internal.
sealed record RendererPerfUnitBudget(RendererPerfClock Clock, long ColdStart,
    long Deadline, CancellationToken Cancellation)
{
    public void Check(string stage);
    public double ElapsedMilliseconds();
}
// RendererPerfCompilerContracts.cs, internal, append in this task.
sealed record RendererPerfColdResult(RendererPerfCompilerInventory Inventory,
    RendererPerfCompilerEvidence Compiler, ImmutableArray<GpuPipelineCompilerReport> Pipelines);
sealed record RendererPerfUnitOutcome(RendererPerfReport? Report,
    ImmutableArray<Exception> Failures);
sealed record RendererPerfUnitFailure(string Schema, RendererPerfAuthorization Authorization,
    ImmutableDictionary<string, string> KnownFacts, long ColdStart, long EndTicks,
    long Frequency, string OriginalFailure, ImmutableArray<string> Errors,
    ImmutableArray<RendererPerfCpuSpan> Stages);
interface IRendererPerfUnitPublication
{
    void PrepareCollection(RendererPerfReport report,
        RendererPerfCompilerInventory inventory, RendererPerfUnitBudget budget);
    bool ReserveReportBytes(int maximumBytes);
    void CommitReportBytes(int actualBytes);
    RendererPerfReport Publish(RendererPerfReport report,
        RendererPerfCompilerInventory inventory, RendererPerfUnitBudget budget);
    void WriteFailure(RendererPerfUnitFailure failure, RendererPerfUnitBudget budget);
}
```

Declaration bodies describe interfaces, not implementation. Actual methods use the stated visibility. Backend settings classes implement Task 1's optional interface and capture all environment-backed settings used by that device/compiler, including cache/debug/validation/record/drain/frame-depth options. Explicit constructors consume these settings without re-reading environment. Ordinary constructors resolve their existing defaults. Task 3 also supplies precomputed effective device configuration. A fake provider verifies the new context overload shares `_lifecycleGate`, adoption cleanup and exact-backend failure propagation.

`RendererPerfDriver.Execute(RendererPerfManifest manifest, RendererPerfRunIdentity identity, RendererPerfSessionFactory factory, RendererPerfUnitBudget budget)` returns an unfinalized `RendererPerfReport`. Replace driver's first-session clock/deadline derivation with the explicit budget. The existing Run overload can remain a test adapter composing Execute and its supplied publication, but cannot certify absent compiler evidence. `RendererPerfColdCollector.Collect(RendererPerfCompilerInventory inventory, GpuDeviceContext diagnostic, GpuEffectiveDiagnosticConfiguration production, RendererPerfUnitBudget budget, Func<int, bool> reserveReportBytes, Action<int> commitReportBytes)` returns `RendererPerfColdResult`, using existing factory signatures and Task 2 checker. Replay only successful unique recipes. Before constructing another graph, reserve its full backend report maximum. Refusal starts no graph or read and marks pending rows Incomplete. After reading, strictly encode its report within that reservation and commit its actual byte count. A graph is created, read and disposed before the next one. It starts no work after budget/cancellation checks fail.

`RendererPerfUnit.Run(RendererPerfManifest manifest, RendererPerfAuthorization authorization, RendererPerfUnitBudget budget, IGpuHeadlessDiagnosticSettings settings, Func<GpuBackendKind, IGpuHeadlessDiagnosticSettings, GpuDeviceContext> createDevice, Func<IGpuDevice, RendererPerfRunIdentity> identifyProduction, Func<IGpuDevice, RendererPerfConstructionCapture, RendererPerfSessionFactory> createSessions, IRendererPerfUnitPublication publication)` returns `RendererPerfUnitOutcome`. A null Report means no trustworthy run identity, bounded WriteFailure attempted and the fixed performance fact must fail. Failures retains the actual ordered exception objects even when serialized evidence cannot retain all error text. No success path hides a failure from this outcome. The entrypoint retains its authorization/runtime admission, sets coldStart once, computes checked absolute Deadline and wires these delegates to actual context/native/session owners. Unit Run owns both contexts. The production identify delegate alone performs the existing cold timestamp calibration. Diagnostic admission reads actual configuration/adapter facts without that delegate or calibration.

- [ ] **Step 1: Add failing configuration/lifecycle tests.** `RendererPerfDiagnosticConfigurationTests.Explicit_instances_do_not_read_or_mutate_process_environment` uses an injected counting environment reader, asserts one settings resolution, independent production/replay choices, zero setter calls and no re-read during creation/compilation. `Timed_D3D_excludes_reflection_and_invocations_independently` sets their approved-environment switches individually and refuses them, then verifies explicit false/false actual options. `Vulkan_supported_capture_omission_is_invalid_extension_absence_is_unsupported` distinguishes omission from genuine capability absence.
- [ ] **Step 2: Run once for missing lifecycle/optional-route failures.** Use Step 6 selection. No new timing or workload test is selected.
- [ ] **Step 3: Implement frozen native settings and actual correspondence.** Use explicit named-backend context calls. Pin replay adapter from production actual key, use true/false diagnostic request, compare exact driver/code-generation options and allow only the spec's effective diagnostic deltas. Require different diagnostic hashes to pass structured comparison, not hash inequality alone. Preserve raw CompilerFlags. Test different actual compiler flags/artifact bytes as Invalid even if source matches. A production-off/replay-on configuration with matching actual stage/aggregate artifacts passes. No provider fallback, profiler, invocation callback, extra query pool or submit is introduced. Preserve genuine Unsupported extension/statistics causes with complete witnesses.
- [ ] **Step 4: Implement Unit/Execute/Collect and cleanup ordering.** Retain every error emitted by each owned release, flattening nested AggregateException causes in their original order. Correct SceneWorkload constructor/Dispose short-circuiting and context disposal skipping GpuDevice.Dispose when MarkDeviceDisposed throws. Move Scene3D disposal into a cohesive partial, retain its required drain/liveness safety order, and attempt each remaining safe child release after a child failure. Preserve the original constructor/frame/compiler failure when cleanup also fails. Existing backend teardown safety rules stay authoritative, never force unsafe destruction after device loss. Add fake release-order cases at these actual owners, not only at the Unit facade. Then observe `Operation="unit", Stage="unit-final", FirstFrame=null` once, before production context disposal. Extend memory capacity to `Batches.Length + operations + 1`, allowing only this new owner. Preserve real peak bytes/current count and count domains. Dispose timed context, freeze inventory, call publication.PrepareCollection with the retained workload report/inventory, then supply publication.ReserveReportBytes and CommitReportBytes to Collect. Replay/read/dispose graphs and diagnostic context, then call publication.Publish. If preparation refuses, perform no diagnostic creation. Task 3 uses a recording fake publication, Task 4 supplies actual bounded file accounting. Charge all spans once from ColdStart. No replay on workload failure, expiry or cancellation. Keep all release errors in occurrence order and first workload/compiler error as OriginalFailure. Before identity exists call WriteFailure with known facts, never fake adapter/timestamps. Native calls remain cooperative, with cleanup attempted even after deadline.
- [ ] **Step 5: Add held-clock/failure assertions.** `RendererPerfColdLifecycleTests.Every_boundary_checks_one_deadline_and_never_starts_next_item` cancels/expires before and after device/shader/layout/pipeline/read/disposal/file-handoff boundaries. Assert no next recipe/frame, unchanged frame order/count, stages charged from original origin, both context disposals precede publication. `All_cleanup_causes_follow_original_failure` injects frame/compiler plus multiple session/graph/context failures and compares the ordered exact causes. `Preidentity_failure_keeps_actual_known_facts_only` asserts WriteFailure called, Publish not called and outcome.Report null and all exception objects retained. `Production_final_observation_precedes_disposal_and_excludes_diagnostics` asserts exactly one final observation, true transient peak bytes retained and live count lower, no PeakLiveResourceCount field and failed reads preserve reasons. No sleep, retry or stress loop.
- [ ] **Step 6: Run focused verification.** `build-slot --label renderer-cold-lifecycle -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~RendererPerfColdLifecycleTests|FullyQualifiedName~RendererPerfDiagnosticConfigurationTests|FullyQualifiedName~RendererPerfDriverTests|FullyQualifiedName~RendererPerfMemoryEvidenceTests|FullyQualifiedName~RendererPerfBatchFailureTests|FullyQualifiedName~RendererPerfFrameLimitTests'`. Expected exit 0 with held-clock assertions, exact independent flags and cleanup order. Context/provider fixture additions in other test projects run once in their existing headless project selection if their owners require it, with the explicit command recorded in the task report before review.
- [ ] **Step 7: Commit explicit Task 3 paths.** Subject `feat(renderer): own cold diagnostics within one unit lifecycle`. Fresh independent review checks gate preservation, all effective options and all failures. Actual native recompilation correspondence and deadline admission remain unproved.

## Task 4: Bounded files, directory verification and nonempty size proof

**Files:** Task 4 row. Extend existing `RendererPerfCompactReportTests`, `RendererPerfNativeSizeFixture`, `RendererPerfSidecarAccountingTests`, `RendererPerfPublicationCompletionTests`, `RendererPerfAnalysisTests`, `RendererPerfComparisonTests` and `RendererPerfWorkflowContractTests` rather than replace their original assertions.

**Consumes:** Task 1 strict report/2 codec and exact typed scalars, Task 2 inventory/encoding/correspondence, Task 3 cold result, budget, unit publication interface and early failure DTO, existing file sink/stamp/failure correction owners.

**Produces:** These exact internal declarations in the new compiler JSON, registry and verification owners.

```csharp
enum RendererPerfSidecarKind { CompilerInventory, CompilerFailure, NonCompiler }
sealed record RendererPerfSidecarCounts(int FactoryAttempts, int Occurrences,
    int Recipes, int Reports, int Executables, int Statistics);
sealed record RendererPerfExpectedSidecar(RendererPerfSidecarKind Kind, string Schema,
    string Path, long ByteLength, string Sha256, RendererPerfSidecarCounts Counts);
// RendererPerfCompilerJson
static byte[] Serialize(RendererPerfCompilerInventory inventory, int maximumBytes);
static RendererPerfCompilerInventory Parse(ReadOnlySpan<byte> utf8, int maximumBytes);
// RendererPerfSidecarRegistry, unit scoped
void Register(RendererPerfExpectedSidecar sidecar);
ImmutableArray<RendererPerfExpectedSidecar> Freeze();
// RendererPerfArtifactVerification
static RendererPerfVerifiedArtifact Verify(string directory, string reportName,
    RendererPerfUnitBudget? budget = null);
```

Task 4 replaces the two-field `RendererPerfSidecar` with `RendererPerfExpectedSidecar` in report and wire records. Only the verifier can construct `RendererPerfVerifiedArtifact`. It exposes `RendererPerfReport Report`, `RendererPerfCompilerInventory Inventory`, `RendererPerfSidecarCounts Counts`, `long CombinedBytes`. It is not serialized or inferred from Parse. `RendererPerfPublicationWriter` implements Task 3's `IRendererPerfUnitPublication`, retaining existing fake adapters and partial recovery. Its file preparation registers actual producer output, not enumerated directory contents. WriteFailure uses `khaozengine.renderer-unit-failure/1` under the same compiler aggregate bound.

Add `RendererPerfAnalysis.ReadDirectory(string directory, string reportName, ImmutableDictionary<string, RendererPerfReferenceRates>? referenceRates = null)` returning `RendererPerfSummary`, and `RendererPerfComparison.Compare(RendererPerfVerifiedArtifact baseline, RendererPerfVerifiedArtifact candidate)` returning existing `RendererPerfComparisonResult`. Existing byte/report/summary overloads may derive descriptive numbers but must explicitly report unverified artifacts and refuse promotion/regression verdicts. They cannot manufacture the verified wrapper. Preserve reference-rate context when verified analysis produces a summary, carrying an internal verified-artifact association instead of an unchecked public bool.

- [ ] **Step 1: Add failing publication tests.** `RendererPerfCompilerPublicationTests.Registered_files_are_reopened_and_reconciled_before_certification` supplies fake producer output and checks exact lengths, streamed hashes, unit identity and independently decoded totals. `Missing_changed_aliased_empty_or_wrong_identity_sidecars_refuse` removes/corrupts files, swaps source/report kinds, adds duplicate paths, symlink/reparse traversal and escaping targets. Assert final correction, partial retention and promotion false. `Parse_is_not_directory_verification` proves byte-only paths cannot obtain verified status or a comparison verdict. Assertions include report/1 rejection in the managed verifier and workflow acceptance.
- [ ] **Step 2: Run once for missing verifier/publication failure.** Use Step 6 focused selection. Keep the expensive full fixture for Step 6 after implementation, not a loop of red full-suite runs.
- [ ] **Step 3: Implement exact registration, reservation and live verification.** Compute encoded base frame/root/inventory sizes and the 16 MiB recovery reserve before the first optional report. Implement Task 3 publication.PrepareCollection/ReserveReportBytes/CommitReportBytes with the registry-owned payload account. Supply the already declared Collect callbacks. Reserve 75497472 for Vulkan or 1048576 for D3D before each graph/read, commit actual bounded encoded report bytes, and refuse a negative or greater-than-reserved commit. Exactly one reservation can be outstanding. Release it only by commit or terminal refusal and count existing retained results once. The 16 MiB completion reserve includes the four bounded option tables (6717440 worst-case encoded bytes) and all admitted result/association/error/reference rows. Before any native read, independently encode their maximum shape with actual retained original causes and refuse if that reserve is insufficient. No assertion that all count maxima fit together is implied. Capacity refusal before a read marks remaining rows Incomplete without further compilation. Preserve all admitted raw fields and typed precision. Enforce checked 64 MiB retained graph sub-budgets and root/combined caps independently, including final 32-byte slot padding. Reopen every expected regular sidecar, refuse aliases/traversal, verify strict schema/association/counts/stage aggregates and sum real lengths. Keep existing noncompiler producers explicit and verified. Bound streamed chunks to 65536 bytes with before/after budget checks. After writing provisional final JSON/Markdown, invoke the shared directory verifier before sampling completion and stamping. The file sink then changes only the two reserved accounting slots. Preserve failed-final correction and bounded partial recovery. No complete claim survives overflow, malformed cause, missing report or unattempted row.
- [ ] **Step 4: Wire both acceptance routes.** Add the verifier console entrypoint with exactly two positional arguments, directory and report filename. Exit 0 only after Verify returns, nonzero with a bounded refusal cause otherwise. It registers no backend, creates no device and invokes no performance fact. Grant internal access to assembly `Renderer.Perf.Verify`. Build it in the existing renderer workflow job, then call its DLL from trusted PowerShell Verify after the existing independent execution-identity checks. Keep Select/Authorize/one fixed measurement fact and all mode/attempt/spend gates unchanged. Offline ReadDirectory calls the identical managed verifier. Update schema/workflow tests and living API docs together, including truthful Metal artifact absence, current counts/true peak bytes, report/1 refusal and remaining native gates. Do not dispatch the workflow.
- [ ] **Step 5: Add decisive nonempty and comparison cases.** Extend the existing recorder-built full fixture using Task 2's actual-shaped fake factory capture, with graphics/compute, specialized sources, repeated/unused occurrences, production/replay identities, raw Boolean/Int64/UInt64.MaxValue/Float64 statistics and compiler sidecar files. Assert 16944 unchanged frames and unchanged raw command/CPU/pass/error evidence, nonzero pipeline/occurrence/report/statistic counts, independent fake-forwarding/count reconciliation and real `rootLength + compilerLengths <= 268435456` after final stamp. Write measured section/count/byte evidence to `/tmp/khaozengine-renderer-cold-publication/`. Test complete optional Unsupported separately from Invalid/incomplete variants. Check exactly-at/one-over graph/report/combined/depth/field bounds and cancellation during chunk verification/stamp. `Occurrence_join_allows_candidate_hash_changes_but_refuses_membership_or_effective_setting_changes` reorders unique recipes and changes candidate sources, proving valid role joins while missing/duplicate roles and real compiler/driver/layout changes refuse. Independently reconstruct aggregates, never accept a report-file checksum as artifact evidence. If the nonempty fixture exceeds the cap, stop and return exact section/file lengths to root rather than prune it.
- [ ] **Step 6: Run one focused verification and one finishing integration gate.** Focused command is `build-slot --heavy --label renderer-cold-publication -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~RendererPerfCompilerPublicationTests|FullyQualifiedName~RendererPerfCompactReportTests|FullyQualifiedName~RendererPerfSidecarAccountingTests|FullyQualifiedName~RendererPerfPublicationCompletionTests|FullyQualifiedName~RendererPerfAnalysisTests|FullyQualifiedName~RendererPerfComparisonTests|FullyQualifiedName~RendererPerfWorkflowContractTests'`. Expected exit 0 and independently measured nonempty files within both caps. Verify the console against the same valid directory and each corrupt case through a process test, without native execution. At root's integration checkpoint run the repository-required zero-warning Release build, ordinary Release test selection, formatting and relevant doc/version/size/workflow guards through build-slot, full suite heavy, once after reconciliation. Record exact commands, cwd and integer exits. Do not run the outside-solution measurement fact or native workflow.
- [ ] **Step 7: Commit explicit Task 4 paths.** Subject `feat(renderer): verify bounded compiler evidence directories`. Fresh independent whole-amendment review checks all four tasks, actual guard/test evidence and this matrix. Root owns push. Native pilots/full units require their existing separate owner approvals.

## Self-review and requirement coverage

This is a planner self-review, not independent implementation review. No tests or size proof named above have run during plan preparation.

| Requirement | Owning task and decisive test/check | Planner result |
| --- | --- | --- |
| CP-1 actual/lazy/specialized capture, deep copy, original forwarding, repeats/unused/failures/binds | Task 2 mutation/lazy/collision/unknown-bind tests, independent factory counts | Covered. No manual constructor inventory or extra frame |
| CP-1 measured creation/observer allocation and explicit bounds | Task 2 measured-first-creation, isolated warmed-bind region and one-over tests | Covered. Native admission not asserted |
| CP-2 instance settings/gate/no fallback/effective D3D and Vulkan exclusion | Task 3 configuration tests, fake provider gate and actual option assertions | Covered. Existing ordinary defaults retained |
| CP-2 exact primary artifact/code generation with allowed structured diagnostic delta | Tasks 1/3 golden-stage/artifact/configuration tests | Covered. Raw flags remain intact |
| CP-3 typed digest availability and malformed companions, exact scalars | Task 1 protocol/backend fixtures, independent stage aggregate vectors | Covered, including Metal absence |
| CP-3 report/2 rejection of report/1, unchanged vectors/2 and coordinated consumers | Tasks 1/4 strict decoder, verifier/workflow/docs checks | Covered. No legacy migration |
| CP-4 actual peak bytes/current live counts, exactly one final observation | Task 3 transient-peak/final-memory tests and validator capacity | Covered. No peak-count field |
| CP-5 retained/native string/count/byte bounds and before-read reservation | Tasks 1/2 maximal escaped output/Creator/one-over tests, Task 4 reservation tests | Covered. Maximal bounds are refusal envelopes |
| CP-5 combined root/compiler budget, exact nonempty fixture and real file lengths | Task 4 full recorder fixture, independent counts/bytes and padded cap checks | Required future proof, not a planning result |
| CP-5 registered directory-aware sidecar integrity and every acceptance entry | Task 4 corrupt/missing/alias/schema/count/identity tests and verifier CLI process cases | Covered. Parse alone never certifies |
| CP-6 one origin/deadline/cancellation, every cleanup error, preidentity failure | Task 3 held clocks, all-failures/early-sidecar tests, Task 4 file/stamp cancellation | Covered. No interruptibility/process-kill guarantee |
| Parent sections 2/3/5 fixed work, hot growth, frame retention, production memory | Tasks 2/3 exact frame/call trace, unchanged count and retained observation assertions | Preserved. No added workload or invocation query |
| Parent section 6 partial/stamp/cost/comparison semantics, Tasks 8/14/15 | Tasks 3/4 ordered failures, completion parity, original driver/sidecar fixtures and occurrence joins | Preserved. Diagnostic collection is not timing |
| Parent Tasks 9/10/11 optional native evidence and Task 13 authorization | Tasks 1/3 backend cases, Task 4 unchanged admission/workflow contract tests | Covered. No Metal manifest or paid run |
| Native completeness/correspondence, actual payload and same-budget acceptance | Root's separately approved native review after all task gates | Open by design, exact failure returns to root |

Signature review: Task 1 owns all shared identity/configuration and root compiler types. Task 2 owns detached inventory and pure correspondence. Task 3 owns budget, result/failure/unit and publication handoff. Task 4 owns the registered file types, verified wrapper and reservation integration. Collect declares both reservation callbacks in Task 3, in reserve/commit order. Task 3 uses the publication interface and fake account, Task 4 implements that same interface without changing the signature. There are no live handles in detached DTOs. Every later-used record and method has a producing task above. Tasks are sequential because shared owner files overlap. No outside subsystem or speculative general capture framework is introduced.

Step review: tests name inputs and observable refusals, command steps name focused selections, implementation steps name exact owners/signatures/encodings, commit gates retain fresh independent review. The five Review Focus cases each have explicit tests. This plan provides declarations and assertions rather than method bodies. No CP contract gap or source conflict was found. Native proof limits remain those in spec section 9.

## Execution handoff

Root obtains a fresh independent review of this spec and plan before runtime implementation dispatch. Use the already owner-approved subagent workflow. Do not ask for new run authorization to implement these technical rulings. Do not infer permission for a native or paid run from review acceptance, successful headless tests or a passing payload fixture. Any source conflict, nondeterministic primary artifact or payload/deadline failure returns precise evidence to root before a different contract is selected.
