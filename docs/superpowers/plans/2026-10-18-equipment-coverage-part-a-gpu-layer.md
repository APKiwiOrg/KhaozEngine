# Equipment coverage Part A: engine GPU layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Supply the portable GPU ordering, storage reads, integer atomics, precise compilation, asynchronous buffer readback and timestamps that Part C needs.
**Architecture:** Keep these facilities in the dependency-free GPU seam and implement native operations behind each existing backend. Reuse completion fences, expose no barrier call, and allocate readback slots and timestamp storage before frame recording. Render3D owns actor layout, frame tags, fallback, history and the frame submission.
**Tech Stack:** C# on net10.0, GLSL 450, shaderc and SPIRV-Cross through Silk.NET 2.23.0, FXC Shader Model 5.0 through Vortice 2.3.0, native Metal and Vulkan, xUnit.
**Spec:** docs/design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md

## Global Constraints

- "Parts A and B in parallel, in separate worktrees branched from `feature/equipment-coverage`, one writer each with fresh review per task. They touch disjoint code."
- Execute only in `/Users/antonio/KhaozEngine/.worktrees/ec-gpu-layer`, branch `feature/equipment-coverage-gpu-layer`, created by root from `feature/equipment-coverage`.
- "The public seam keeps no barrier call."
- "Float values are never accumulated atomically."
- "A fixed ring of slots means it never stalls."
- "Where it is false the GPU gate is reported as unmeasurable, never estimated."
- "Coverage kernels compile in precise mode without fast maths: GLSL `precise` on position arithmetic, Metal fast maths off for these kernels, FXC IEEE strictness."
- "The part B plan fixes the number." Part A neither selects nor widens epsilon.
- "The 2 ms for 32 typical actors CPU gate, the 15 percent native GPU gate and the zero-allocation gate are unchanged."
- "Every measurement needs fresh owner approval." This plan supplies correctness tests, not pilots or budget measurements.
- "Vulkan synchronisation validation stays clean."
- "Warnings are errors." Each task finishes with a zero-warning Release build, focused tests and changed-file whitespace verification.
- "Plain `dotnet test` skips GPU facts." Native facts use `KE_GPU_TESTS=1` and the named backend.
- Every local restore, build, test or formatter invocation uses `build-slot --label <what> -- <command>`. Full suites use `--heavy`. Never loop tests.
- "KESIZE is a structure ratchet. Put new behavior in a new type." No baseline growth or exemption without owner approval.
- "In shared worktrees, stage and commit explicit paths. Never use the shared stash for coordination."
- "Commit subjects use `area(scope): summary`."
- Parts B, C and D, version selection, changelog, packaging, integration and release remain with root. Do not change coverage or ocean algorithms here.

## Review Focus

1. A written buffer survives an unrelated dispatch, a smaller pipeline layout and unchanged graphics bindings. Its eventual reader receives the write, and an independent reader pays no barrier. Task 1.
2. Vertex pulling with a nonzero first vertex and indexed base vertex reads the intended storage entries without an input-assembler vertex buffer. Task 1.
3. Warm caches alternate default and precise versions of identical source. Each stage receives its requested compile policy and the fragment stage keeps its existing policy. Task 3.
4. A readback ring fills while GPU completion is held, then wraps or abandons an unsubmitted frame. It neither waits, overwrites, returns stale tags nor grows. Tasks 4 and 5.
5. Timestamp support is absent, results are disjoint or unavailable, counters wrap, or Metal offers stage boundaries only. Resolve distinguishes pending and invalid from measured GPU results and invents no period. Tasks 6 to 9.

## Verified starting facts and boundaries

The planning guard passed on `feature/equipment-coverage`, containing `c525aa2bb52a75db2b4b9c0d17a31638b7938699`. The amendment already has an unrelated working-tree modification. Preserve it. Implementation starts later in root's separate clean worktree.

`GpuInterfaces.cs:317-399` still requires a drain between dependent dispatches. Native Vulkan already barriers dependent compute bindings, but `VulkanDrawRecorder.PrepareGraphics` does not consult buffer hazards and its dependency destination mask is compute only. Metal uses serial compute encoders. D3D11 already tracks SRV/UAV conflicts. `VertexStageBufferSeamGpuTests.cs` already proves vertex-stage storage reads using an attribute index, so Task 1 extends that path rather than adding another storage API. RW D3D11 buffers already have both raw SRVs and UAVs.

The shader pipeline already accepts integer atomic instructions through GLSL. Task 2 adds executable compiler and device conformance, not a C# atomic API. `GpuReadback` and both native Metal/Vulkan read maps drain. A completion poll followed by the old `Map` is therefore insufficient. There is no GPU timestamp seam.

Precision mechanisms are supported by the source chain, but native execution is still an implementation gate. GLSL `precise` must survive shaderc as SPIR-V `NoContraction`. [SPIRV-Cross's MSL implementation](https://github.com/KhronosGroup/SPIRV-Cross/blob/vulkan-sdk-1.3.280.0/spirv_msl.cpp) lowers decorated arithmetic through `spvFMul`, `spvFAdd` and `spvFSub`, including deliberate single-operation `fma` helpers. Do not reject every occurrence of `fma` in emitted MSL. [FXC IEEE strictness](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/d3dcompile-constants) and [HLSL precise](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/precise) prohibit unsafe contraction. [Metal fastMathEnabled](https://developer.apple.com/documentation/metal/mtlcompileoptions/fastmathenabled) is per compilation. Task 3 verifies the installed 2.23.0 toolchain, not an assumed upstream version.

For timestamps, use [Vulkan query availability and timestampPeriod](https://docs.vulkan.org/spec/latest/chapters/queries.html), [D3D11 timestamp-disjoint queries](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_query_data_timestamp_disjoint) and [GetData without flushing](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_async_getdata_flag). Metal needs [supported counter boundaries](https://developer.apple.com/documentation/metal/sampling-gpu-data-into-counter-sample-buffers), [GPU-side counter resolve](https://developer.apple.com/documentation/metal/converting-a-gpus-counter-data-into-a-readable-format) and [hardware clock correlation](https://developer.apple.com/documentation/metal/converting-gpu-timestamps-into-cpu-time). Its raw counter ticks do not have a portable fixed nanosecond period.

## File structure map

Paths below are relative to the engine root. Task file lists give the modification ranges, measured before implementation.

| Files | Responsibility |
| --- | --- |
| `KhaozEngine.Gpu/GpuInterfaces.cs`, new `GpuShaderCompileOptions.cs`, new `GpuTimestamps.cs` | Portable contracts and default refusal of optional operations |
| New `KhaozEngine.Gpu/GpuBufferReadbackRing.cs`, new `Internal/GpuTimestampPoolState.cs`, new `Internal/GpuTimestampResults.cs` | Fixed readback storage, timestamp lifecycle and counter normalization |
| `KhaozEngine.Gpu/Internal/SpirvFrontEnd.cs`, `SpirvCrossCompile.cs`, `ShaderValidation.cs` | Per-stage precision through validation, SPIR-V and cross-compilation |
| `KhaozEngine.Gpu.Vulkan/Internal/VulkanBindRecords.cs`, `VulkanResourceSet.cs`, `VulkanComputeHazards.cs`, `VulkanDrawRecorder.cs`, `VulkanDrawBatch.cs`, `VulkanCommandList.Draws.cs` | Buffer access metadata and draw/dispatch hazard publication |
| Each backend's `Internal/*ResourceFactory.cs`, shader compiler/build/key files, and Vulkan shader-set/compute-shader files | Public option routing and cache identity |
| New `KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.Readback.cs`, new `KhaozEngine.Gpu.Metal/Internal/MetalGpuDevice.Readback.cs`, new `KhaozEngine.Gpu.Vulkan/Internal/VulkanGpuDevice.Readback.cs` | Non-blocking reads after completion, bypassing synchronous maps |
| D3D11 staging-memory/access seams | Non-blocking native map and immediate-context ownership |
| `KhaozEngine.Gpu/GpuCapabilities.cs`, backend capability readers and device-create wiring | Default-false, live-device timestamp support |
| New backend `Internal/*TimestampPool.cs`, `I*TimestampApi.cs`, `*TimestampApi.cs`, Vulkan/Metal `*CommandList.Timestamps.cs`, D3D11 `D3D11CommandRecorder.Timestamps.cs` | Query ownership, native calls, record/replay and non-blocking resolve |
| New `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLTimestampInterop.cs` | Counter-set discovery, sample descriptors, counter resolve and correlated clocks |
| Existing D3D11 command recorder, emitter and stream files | Timestamp commands reach both immediate and deferred replay |
| New tests `ComputeBufferOrderingGpuTests.cs`, `ComputeVertexPullingGpuTests.cs`, `ComputeIntegerAtomicGpuTests.cs`, `ComputeAtomicShaders.cs` under `KhaozEngine.Render.Tests/Gpu/` | Ordering, vertex-index storage reads and atomic compiler/device proofs |
| New tests `PreciseShaderCompileTests.cs`, `ComputePreciseMathGpuTests.cs`, `PreciseMathShaders.cs` in the same directory | Compiler policy, cache separation and native arithmetic proof |
| New tests `GpuAsyncBufferReadTests.cs`, `ComputeAsyncReadbackGpuTests.cs`, `GpuBufferReadbackRingTests.cs` in the same directory | Held-completion negatives, native copies and fixed-ring lifecycle |
| New tests `GpuTimestampPoolTests.cs`, `ComputeTimestampGpuTests.cs`, `VulkanTimestampTests.cs`, `D3D11TimestampTests.cs`, `MetalTimestampTests.cs` in the same directory | Common lifecycle, native query policy and conditional support paths |
| Existing `VulkanComputeHazardWalkTests.cs`, `D3D11ComputePathTests.cs`, `GpuCapabilitiesTests.cs`, `ShaderKeyToolchainTests.cs`, `D3D11FxcValidationTests.cs` | Extend existing policy and real-FXC coverage |
| `docs/USING-KHAOZENGINE.md`, `KhaozEngine.Gpu/README.md`, `docs/CROSS-PLATFORM.md` | Living consumer contracts and backend validation routes, updated with owning tasks |

## Execution and verification convention

Root creates the execution worktree. Before Task 1, the implementer runs:

```bash
cd /Users/antonio/KhaozEngine/.worktrees/ec-gpu-layer
test "$(git branch --show-current)" = feature/equipment-coverage-gpu-layer
git merge-base --is-ancestor c525aa2bb52a75db2b4b9c0d17a31638b7938699 HEAD
a_start_commit=$(git rev-parse HEAD)
mkdir -p local-feed
```

Use the following Bash function in the execution shell, without saving a script. It is the exact finish command referenced by each task. `Render.Tests` already references the seam and all three backend projects, so its Release build builds every affected production project. The changed-file formatter is the whitespace route documented in `docs/superpowers/plans/2026-10-05-engine-format-baseline-cleanup.md:26-35`.

```bash
a_check() {
    local a_label="$1" a_filter="$2" a_path
    local -a a_cs=()
    build-slot --label "$a_label-build" -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release || return
    build-slot --label "$a_label-tests" -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$a_filter" || return
    while IFS= read -r a_path; do
        test -n "$a_path" && a_cs+=("$a_path")
    done < <(git diff --name-only --diff-filter=ACMR HEAD -- '*.cs'; git ls-files --others --exclude-standard -- '*.cs')
    build-slot --label "$a_label-format" -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include "${a_cs[@]}"
}
```

Assertion blocks below are test excerpts. Their observation locals come from the arrangement immediately above, not new production interfaces.

Expected for every `a_check`: build exit 0, 0 warnings, 0 errors, focused tests nonzero passed with 0 failed and 0 skipped, formatter exit 0. Run once after the change, and repeat only after a fix. A GPU correctness test may submit and drain once to establish its final assertion. Neither production `TryRead`/`TryResolve` nor their pending-path tests may drain, flush, sleep or poll in a loop.

Every commit step means stage exactly that task's changed files and commit with that same explicit path list. Root owns pushing and hosted dispatch. At each native checkpoint give root the stated `renderTestFilter`, `bake=false`, `tier=push`, and `disableGpuDiskCache=true`. All three native devices must exercise every capability before Part C. `Compute` in every new native test class also places it in the existing Vulkan sync subset. Accept that job only with a clean error-severity scan, not merely passing pixels. No goldens are needed for these exact numeric and pixel assertions. If an existing golden changes, root reviews the side-by-side delta and only the owning hosted backend bakes a replacement.

## Task 1: Ordered storage buffers through dispatches and vertex-index draws

**Files**

- Create/Test: `KhaozEngine.Render.Tests/Gpu/ComputeBufferOrderingGpuTests.cs`, `KhaozEngine.Render.Tests/Gpu/ComputeVertexPullingGpuTests.cs`.
- Modify/Test: `KhaozEngine.Render.Tests/Gpu/VulkanComputeHazardWalkTests.cs:1-300`, `KhaozEngine.Render.Tests/Gpu/D3D11ComputePathTests.cs:25-130`.
- Modify: `KhaozEngine.Gpu/GpuInterfaces.cs:317-399`.
- Modify: `KhaozEngine.Gpu.Vulkan/Internal/VulkanBindRecords.cs:33-49`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanResourceSet.cs:82-131,180-199,258-335`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanComputeHazards.cs:89-169`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanDrawRecorder.cs:151-215`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanDrawBatch.cs:34-49`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanCommandList.Draws.cs:122-145`.
- Modify comments: `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Draws.cs:222-243`.
- Modify docs: `docs/USING-KHAOZENGINE.md:17277-17328,22213-22366`, `KhaozEngine.Gpu/README.md:417-442`.

**Interfaces**

- Consumes: existing `void Dispatch(uint,uint,uint)`, `void Draw(uint,uint,uint,uint)`, `void DrawIndexed(uint,uint,uint,int,uint)`, `void SetGraphicsResourceSet(uint,IGpuResourceSet)` and both structured buffer kinds.
- Produces: the same public signatures with in-list buffer visibility. Internally extend `VulkanBoundSet` with optional `ulong[]? WritableStorageBuffers = null`, retain `StorageBuffers` for all storage reads, and expose `ReadOnlySpan<ulong> BoundWritableStorageBuffers`. For legacy test records a null writable array conservatively falls back to `StorageBuffers`, while real resource sets supply a non-null array, empty when all bindings are read-only. Existing `bool VulkanComputeHazards.NeedsBarrier(VulkanBindRecords)` works for either binding arm.

- [ ] Write `VulkanComputeHazardWalkTests.GraphicsReadAfterUnrelatedDispatchPublishesTheEarlierBuffer`. Arrange compute RW buffer handle 41, unrelated RW handle 42, an already-bound graphics RO view of 41 and an open rendering scope. Also test a pipeline dropping and restoring a set. Assert the dependency barrier precedes reopening rendering, with exactly one barrier before the first actual reader and none for independent handle 43. Assert destination stages contain `ComputeShaderBit | AllGraphicsBit` and accesses contain `ShaderReadBit | ShaderWriteBit`.
- [ ] Write native `ComputeBufferOrderingGpuTests.ReductionAndDrawShareOneSubmission`, using the existing 4096-value reduction and no intermediate end/submit/wait. Assert `Assert.Equal(8390656u, GpuReadback.ReadBuffer<uint>(device, total, 1)[0]);`. Add `ComputeVertexPullingGpuTests.DrawAndDrawIndexedUseTheStorageVertexIndex`: upload six full-screen-triangle entries, entries 0 to 2 red and 3 to 5 green. Draw with `vertexStart=3`, then indices `[0,1,2]` with `vertexOffset=3`, no vertex layouts or vertex-buffer bind. Vertex GLSL fetches `Entries[gl_VertexIndex]`. Assert each target's center RGBA equals `new byte[] { 0, 255, 0, 255 }`. Repeat after a compute writer changes entries 3 to 5 to blue and assert `new byte[] { 0, 0, 255, 255 }` in both targets.

```csharp
// GraphicsReadAfterUnrelatedDispatchPublishesTheEarlierBuffer
Assert.Equal(1, dependencyBarrierCount);
Assert.True(barrierBeforeRenderingBegin);
Assert.Equal(0, independentReaderBarrierCount);
// ReductionAndDrawShareOneSubmission
Assert.Equal(8390656u, GpuReadback.ReadBuffer<uint>(device, total, 1)[0]);
```

- [ ] Run red: `build-slot --label a1-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~GraphicsReadAfterUnrelatedDispatchPublishesTheEarlierBuffer`. Expected failure: no graphics buffer dependency barrier, or missing graphics destination stage.
- [ ] Implement the access metadata in the listed Vulkan files. `ResolveBuffer` adds every storage handle to reads and only `StructuredBufferReadWrite` to writes. Before graphics transition/begin, ask the same hazard set, end active rendering if owed, emit the dependency and clear writes. Widen the global barrier destination to compute plus all graphics before allowing any clear, so an earlier compute-to-compute publication also covers a later vertex reader. Keep slot walks bounded by `BindableSlotLimit`, not dirty slots. Metal's tracked shared resources and serial encoder and D3D11's existing conflict tracker remain the mechanisms.
- [ ] Extend the D3D11 trace test to a RW buffer viewed as a vertex SRV. Assert `CSSetUnorderedAccessViews(0,1,-)` precedes `VSSetShaderResources(0,1,<buffer-id>)`, and the reverse handoff invalidates the graphics binding. No new public storage type or vertex-buffer alias is introduced.
- [ ] Replace stale drain-required text in the listed living docs and native comments. Document RO vertex bindings and `gl_VertexIndex`, including first/base vertex. Keep cross-list synchronization explicit. Preserve the existing texture handoff and the production ocean implementation.
- [ ] Run green: `a_check a1 'FullyQualifiedName~ComputeBufferOrderingGpuTests|FullyQualifiedName~ComputeVertexPullingGpuTests|FullyQualifiedName~VulkanComputeHazardWalkTests|FullyQualifiedName~D3D11ComputePathTests|FullyQualifiedName~ComputeBufferGpuTests|FullyQualifiedName~ComputeTextureHandoffGpuTests|FullyQualifiedName~ComputeFftGpuTests|FullyQualifiedName~VertexStageBufferSeamGpuTests'`.
- [ ] Root hosted checkpoint: `legs=all`, `renderTestFilter='FullyQualifiedName~ComputeBufferOrderingGpuTests|FullyQualifiedName~ComputeVertexPullingGpuTests|FullyQualifiedName~ComputeBufferGpuTests|FullyQualifiedName~ComputeTextureHandoffGpuTests|FullyQualifiedName~ComputeFftGpuTests|FullyQualifiedName~VertexStageBufferSeamGpuTests'`, plus the standard Vulkan sync job.
- [ ] Commit explicit task paths with `gpu(ordering): guarantee in-list storage buffer visibility`.

## Task 2: Unsigned storage-buffer atomic conformance

**Files**

- Create/Test: `KhaozEngine.Render.Tests/Gpu/ComputeIntegerAtomicGpuTests.cs`.
- Create: `KhaozEngine.Render.Tests/Gpu/ComputeAtomicShaders.cs`.
- Modify/Test: `KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs:90-139` in its existing compute validation cases.
- Modify docs: `docs/USING-KHAOZENGINE.md:22213-22366`, `KhaozEngine.Gpu/README.md:417-453`.

**Interfaces**

- Consumes: `IGpuComputeShader CreateComputeShaderFromSpirv(string)`, `void Dispatch(uint,uint,uint)` and Task 1 ordering.
- Produces: documented GLSL `uint atomicOr(inout uint,uint)` and `uint atomicMax(inout uint,uint)` semantics on storage words. Test fixture `internal static class ComputeAtomicShaders` exposes `internal const string UnsignedOrMax` and `internal const string ReturnOldValue`.

- [ ] Write `ComputeIntegerAtomicGpuTests.UnsignedOrAndMaxContendAcrossFourWorkgroups`. Dispatch 4 groups of 64 invocations. Invocation `i` ORs `1u << (i & 31u)` into word 0 and maximizes word 1 with `0x80000000u | i`. Leave word 2 initialized to `0x12345678u`. Assert `Assert.Equal(new uint[] { 0xffffffffu, 0x800000ffu, 0x12345678u }, result);`. A later dispatch reads the results in the same list. Add `AtomicsReturnThePreviousUnsignedValue` with one invocation, initial value `0x20u`, OR argument `4u`, then MAX argument `0x80000000u`. Assert old results `0x20u` and `0x24u`, final word `0x80000000u`.

```csharp
// UnsignedOrAndMaxContendAcrossFourWorkgroups
Assert.Equal(new uint[] { 0xffffffffu, 0x800000ffu, 0x12345678u }, result);
// AtomicsReturnThePreviousUnsignedValue
Assert.Equal(new uint[] { 0x20u, 0x24u, 0x80000000u }, returnedAndFinal);
```

- [ ] Run red: `build-slot --label a2-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~ComputeIntegerAtomicGpuTests`. Expected CS0103 for the not-yet-created `ComputeAtomicShaders` fixture. The capability may already execute correctly, so do not manufacture a backend defect.
- [ ] Implement those two GLSL fixtures with std430 `uint` storage words, `local_size_x=64` and `1` respectively. Extend the real FXC validation corpus with both sources through `D3D11ShaderValidation.ValidateCompute(string,string?)`. Device-free assertions on `SpirvCrossCompile.ComputeToHlsl(byte[],string?)` require `InterlockedOr` and `InterlockedMax`. MSL assertions require `atomic_fetch_or_explicit` and `atomic_fetch_max_explicit`. Native shader creation must invoke the real FXC/Metal compiler with disk caches disabled at the checkpoint. No float atomic or separate atomic resource kind is added.
- [ ] Document unsigned max semantics and the distinction between atomic word updates and Task 1's inter-dispatch visibility. No algorithm relies on workgroup order.
- [ ] Run green: `a_check a2 'FullyQualifiedName~ComputeIntegerAtomicGpuTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~ComputeShaderValidationTests'`.
- [ ] Root hosted checkpoint: `legs=all`, `renderTestFilter='FullyQualifiedName~ComputeIntegerAtomicGpuTests'`. Require real FXC validation and clean Vulkan sync output.
- [ ] Commit explicit task paths with `gpu(atomics): prove unsigned storage atomic operations`.

## Task 3: Per-shader precise math through every compiler and cache

**Files**

- Create: `KhaozEngine.Gpu/GpuShaderCompileOptions.cs`, `KhaozEngine.Render.Tests/Gpu/PreciseMathShaders.cs`.
- Create/Test: `KhaozEngine.Render.Tests/Gpu/PreciseShaderCompileTests.cs`, `KhaozEngine.Render.Tests/Gpu/ComputePreciseMathGpuTests.cs`.
- Modify/Test: `KhaozEngine.Render.Tests/Gpu/ShaderKeyToolchainTests.cs:87-159`, `KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs:90-139` in their cache-key and compute cases.
- Modify: `KhaozEngine.Gpu/GpuInterfaces.cs:143-158`, `KhaozEngine.Gpu/ShaderValidation.cs:100-240` at `ValidatePair` and `ValidateCompute`.
- Modify: `KhaozEngine.Gpu/Internal/SpirvFrontEnd.cs:61-137`, `KhaozEngine.Gpu/Internal/SpirvCrossCompile.cs:81-203,346-378`.
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11ResourceFactory.cs:208-217`, `KhaozEngine.Gpu.D3D11/Internal/D3D11ShaderCompiler.cs:64-76`, `KhaozEngine.Gpu.D3D11/Internal/D3D11ShaderBuild.cs:53-123`, `KhaozEngine.Gpu.D3D11/Internal/D3D11ShaderKey.cs:81-129`, `KhaozEngine.Gpu.D3D11/Internal/D3D11ShaderValidation.cs:30-58` at its two validation methods.
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalResourceFactory.cs:161-173,248-270`, `KhaozEngine.Gpu.Metal/Internal/MetalShaderCompiler.cs:92-162,197-210`, `KhaozEngine.Gpu.Metal/Internal/MetalShaderBuild.cs:60-114`, `KhaozEngine.Gpu.Metal/Internal/MetalShaderKey.cs:123-171`, `KhaozEngine.Gpu.Metal/Internal/MslCompilePin.cs:58-86`.
- Modify: `KhaozEngine.Gpu.Vulkan/Internal/VulkanResourceFactory.cs:256-272`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanShaderSet.cs:43-64`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanComputeShader.cs:30-41`.
- Modify docs: `docs/USING-KHAOZENGINE.md:22111-22366`, `KhaozEngine.Gpu/README.md:407-453`.

**Interfaces**

- Consumes: the existing two factory shader creation methods and existing internal compiler/key routes.
- Produces: `public readonly record struct GpuShaderCompileOptions(bool PreciseMath = false)`.
- Produces factory overloads: `IGpuShaderSet CreateShadersFromSpirv(string vertGlsl,string fragGlsl,GpuShaderCompileOptions vertexOptions)` and `IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl,GpuShaderCompileOptions options)`. Existing overloads delegate with `default`. Default interface implementations forward default options and throw `NotSupportedException` for precise options, so external/fake implementations never silently ignore a request.
- Produces validation overloads: `void ShaderValidation.ValidatePair(string vertexGlsl,string fragmentGlsl,GpuShaderCompileOptions vertexOptions,string? label = null)` and `void ShaderValidation.ValidateCompute(string computeGlsl,GpuShaderCompileOptions options,string? label = null)`. Add the same option-bearing overloads to internal `D3D11ShaderValidation`.
- Extends `SpirvFrontEnd.ToSpirv(string,GpuShaderStages,string? label = null,GpuShaderCompileOptions options = default)` and `OptionsIdentity(string,GpuShaderCompileOptions options = default)`.

- [ ] Write `PreciseShaderCompileTests.OptionsSurviveAllStagesAndCaches`. For compute and vertex modules generated from nonconstant input, walk `OpDecorate` and the arithmetic result IDs and assert `NoContraction` decoration value `42` on the multiply and add. Assert emitted HLSL contains `precise` and MSL contains `spvFMul` and `spvFAdd`. Assert default and precise cache keys differ for SPIR-V, DXBC and MSL, repeated same-option keys equal, and default keys retain existing equality fixtures. Assert the Metal policy is fast math false for precise Vertex/Compute, true for Fragment and default Vertex/Compute. Assert the precise vertex FXC flags include `0x00002000u`, fragment flags keep the previous value.
- [ ] Write `ComputePreciseMathGpuTests.ComputeAndVertexKeepSeparateMultiplyAdd`. Supply input bits `[0x3f800001u,0x3f7ffffeu,0xbf800000u]`, evaluate `precise float p=a*b; precise float q=p+c;`. Compute stores `floatBitsToUint(q)`. Vertex emits that word as a flat unsigned varying, and the default fragment shader outputs green only for zero. Assert `Assert.Equal(0u, result[0]);` and center RGBA `new byte[] { 0, 255, 0, 255 }`. Alternate default, precise, default, precise creation with identical source in each cache test. Instantiate cache objects against test-private temporary directories rather than changing cache environment variables. Any test reading shared front-end counters joins the existing `[Collection("SpirvCompileCacheSerial")]`, whose definition disables parallelization. Never assert a particular result for default math.

```csharp
// OptionsSurviveAllStagesAndCaches
Assert.NotEqual(defaultKey, preciseKey);
Assert.Equal(preciseKey, repeatedPreciseKey);
Assert.Contains("precise", emittedHlsl);
Assert.Contains("spvFMul", emittedMsl);
Assert.Contains("spvFAdd", emittedMsl);
Assert.Equal(0x00002000u, preciseVertexFlags & 0x00002000u);
Assert.Equal(defaultFragmentFlags, actualFragmentFlags);
// ComputeAndVertexKeepSeparateMultiplyAdd
Assert.Equal(0u, result[0]);
Assert.Equal(new byte[] { 0, 255, 0, 255 }, centerRgba);
```

- [ ] Run red: `build-slot --label a3-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~PreciseShaderCompileTests`. Expected CS0246 for `GpuShaderCompileOptions`.
- [ ] Implement the public option and routing. Append an optional `GpuShaderCompileOptions options = default` after current optional arguments on internal compile/build methods, using the option only for compute or the vertex stage. `SpirvCrossCompile`'s pair methods accept `vertexOptions` and configure the two stage compilers separately. Add key overloads with `options` before the `params string[]` argument. Include the option for the whole paired-program identity, including the fragment key, even though its compile flags stay default.
- [ ] In precise shaderc compilation select `OptimizationLevel.Zero`, retain Vulkan 1.0/SPIR-V 1.0 and the existing entry point, and preserve authored `precise` dataflow. Default compilation remains `OptimizationLevel.Performance`. Append `;precise=1` only to precise front-end identities. This option controls the compiler, it does not insert qualifiers into source or promise identical transcendental/denormal behavior across devices.
- [ ] Preserve SPIR-V decorations through SPIRV-Cross. Enable `CompilerOption.MslInvariantFPMath` only for precise stage emission, using the exact [Silk.NET 2.23.0 enum value `0x0800002f`](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/SPIRV/Silk.NET.SPIRV.Cross/Enums/CompilerOption.gen.cs#L110-L112), and retain HLSL SM 50 and MSL source version 10200. HLSL carries `precise`. FXC ORs `0x00002000u` into the requested compute/vertex flags. Metal creates options for each stage compilation, sets fast math false only for precise Compute/Vertex, and retains language version `(3u << 16) | 2u` and existing preserve-invariance policy. Add `internal static bool MslCompilePin.FastMathEnabledFor(MetalShaderStage stage,GpuShaderCompileOptions options)` for the tested policy. Do not globally disable math optimizations or alter fragment compilation.
- [ ] Extend every cache identity before any lookup, including Vulkan's `SpirvBytesCache` callers. Pass options through validation to the same shipped paths. Prove the installed SPIRV-Cross emission and both native compilers with the above tests. A failure blocks the capability and returns to root, never drops strictness or widens epsilon. Document the source qualifier plus compiler-option requirement for Part C.
- [ ] Run green: `a_check a3 'FullyQualifiedName~PreciseShaderCompileTests|FullyQualifiedName~ComputePreciseMathGpuTests|FullyQualifiedName~ShaderKeyToolchainTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~SpirvCompileCacheTests|FullyQualifiedName~VulkanSpirvByteEqualityTests|FullyQualifiedName~SpirvCrossCompileTests'`.
- [ ] Root hosted checkpoint: `legs=all`, `renderTestFilter='FullyQualifiedName~ComputePreciseMathGpuTests|FullyQualifiedName~PreciseShaderCompileTests'`. Real FXC and Metal compilation and exact arithmetic assertions must pass. Part B still owns the larger reference-equivalence domain and epsilon.
- [ ] Commit explicit task paths with `gpu(shaders): add per-shader precise math compilation`.

## Task 4: Non-blocking reads from completed staging copies

**Files**

- Create: `KhaozEngine.Gpu.D3D11/Internal/D3D11GpuDevice.Readback.cs`, `KhaozEngine.Gpu.Metal/Internal/MetalGpuDevice.Readback.cs`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanGpuDevice.Readback.cs`.
- Create/Test: `KhaozEngine.Render.Tests/Gpu/GpuAsyncBufferReadTests.cs`, `KhaozEngine.Render.Tests/Gpu/ComputeAsyncReadbackGpuTests.cs`.
- Modify: `KhaozEngine.Gpu/GpuInterfaces.cs:514-525`.
- Modify/Test: `KhaozEngine.Render.Tests/Gpu/FakeD3D11StagingMemory.cs:34-204`.
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11Buffer.cs:44-45,109-110`, `KhaozEngine.Gpu.D3D11/Internal/D3D11GpuFence.cs:44-60`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanBuffer.cs:126-162`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanGpuFence.cs:47-64`, `KhaozEngine.Gpu.Metal/Internal/MetalGpuFence.cs:48-73`.
- Modify: `KhaozEngine.Gpu.D3D11/Internal/ID3D11StagingMemory.cs:35-100`, `KhaozEngine.Gpu.D3D11/Internal/D3D11ContextStagingMemory.cs:46-59,102-123`, `KhaozEngine.Gpu.D3D11/Internal/D3D11StagingAccess.cs:74-113`.
- Modify docs: `docs/USING-KHAOZENGINE.md:22261-22302`, `KhaozEngine.Gpu/README.md:330-340`, `docs/CROSS-PLATFORM.md:846-850`.

**Interfaces**

- Consumes: `void IGpuCommandList.CopyBuffer(IGpuBuffer,uint,IGpuBuffer,uint,uint)`, `void IGpuDevice.Submit(IGpuCommandList,IGpuFence)` and `bool IGpuFence.Signaled`.
- Produces: `bool IGpuDevice.TryReadBuffer(IGpuBuffer staging,IGpuFence completion,uint offsetBytes,Span<byte> destination)`, default interface implementation throws `NotSupportedException`. Returns false without changing destination if completion or native access is pending. Success copies bytes and closes any native map before returning.
- Produces internal D3D11 seam: `int ID3D11StagingMemory.TryMapBufferRead(object buffer,out IntPtr data)` and `bool D3D11StagingAccess.TryReadBuffer(IGpuBuffer staging,uint offsetBytes,Span<byte> destination)`.

- [ ] Write `GpuAsyncBufferReadTests.PendingAndBusyReadsNeitherTouchMemoryNorDrain`. Hold completion false, fill destination with `0xcc`, call once and assert `Assert.False(read); Assert.Equal(new byte[] { 0xcc,0xcc,0xcc,0xcc }, destination); Assert.Equal(0, waitCalls); Assert.Equal(0, flushCalls); Assert.Equal(0, nativeMapCalls);`. Set completion true and hold D3D11's immediate-context lock from another thread using a completion handshake, not a sleep. Assert false with unchanged destination. Add dead/disposed device, foreign fence, non-staging source, offset 3, and out-of-bounds offset 12/count 8 on a 16-byte staging buffer. Assert the named argument/disposal errors before reading any pointer.
- [ ] Write `ComputeAsyncReadbackGpuTests.RecordedCopyReadsTheRequestedSliceAfterCompletion`. Compute writes `[7u,11u,13u,17u]`, record a 12-byte copy from source offset 4 into staging offset 4 in the same list. Before submission assert TryRead false. Submit with the given completion fence, drain once in the test, read 12 bytes at offset 4 and assert `Assert.Equal(new uint[] { 11u,13u,17u }, words);`. A single-signaled old submission plus later unrelated pending work must not select the device-wide drain path, proved by device-free call tracing.

```csharp
// PendingAndBusyReadsNeitherTouchMemoryNorDrain
Assert.False(device.TryReadBuffer(staging, completion, 0, destination));
Assert.Equal(new byte[] { 0xcc, 0xcc, 0xcc, 0xcc }, destination);
Assert.Equal(0, waitCalls);
Assert.Equal(0, flushCalls);
Assert.Equal(0, nativeMapCalls);
// RecordedCopyReadsTheRequestedSliceAfterCompletion
Assert.Equal(new uint[] { 11u, 13u, 17u }, words);
```

- [ ] Run red: `build-slot --label a4-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~GpuAsyncBufferReadTests`. Expected CS1061 for `TryReadBuffer`.
- [ ] Implement per backend, validating live device, resource ownership, staging usage, four-byte offset alignment and subtraction-based bounds before native access. Require a completion fence for the same device whose submission contains this copy. The caller keeps source/staging/fence alive and never resets the fence until the readback consumer releases it. A reset or reused external fence is a caller error, not a new completion. Add `internal void RequireOwner(D3D11FenceSubsystem owner)`, `RequireOwner(VulkanTimeline owner)` and `RequireOwner(MetalTimeline owner)` on their respective fence types, refusing foreign owners and disposed fences. Expose the D3D11 buffer's `internal DeviceLiveness Owner` and Vulkan buffer's `internal VulkanResourceOwner Owner` for reference-identity checks. Metal buffers already expose their owner. No new public ownership token is added.
- [ ] D3D11 takes the immediate-context lock using `Monitor.TryEnter`, polls completion inside it and maps `Read` with `MapFlags.DoNotWait`. HRESULT `0x887A000A` returns false, other failures follow the existing device-loss path. Copy the requested bytes and unmap in `finally`. Metal checks completion then copies from the shared staging buffer's contents. Vulkan checks completion, invalidates the staging allocation range for non-coherent memory, then copies from its persistent mapped pointer. Neither calls existing `Map`, `WaitForIdle`, setup flush, submit or a blocking lock. Native memory visibility remains supplied by the existing buffer-copy barriers and completion.
- [ ] Document this explicitly separate from synchronous `GpuReadback`, including the fence lifetime and unsubmitted-fence behavior. Keep existing synchronous APIs unchanged.
- [ ] Run green: `a_check a4 'FullyQualifiedName~GpuAsyncBufferReadTests|FullyQualifiedName~ComputeAsyncReadbackGpuTests|FullyQualifiedName~D3D11StagingMapTests|FullyQualifiedName~CopyBufferOffsetGpuTests'`.
- [ ] Root hosted checkpoint: `legs=all`, `renderTestFilter='FullyQualifiedName~ComputeAsyncReadbackGpuTests|FullyQualifiedName~CopyBufferOffsetGpuTests'` and clean Vulkan sync output.
- [ ] Commit explicit task paths with `gpu(readback): read completed staging copies without waiting`.

## Task 5: Fixed readback ring with retained frame identity

**Files**

- Create: `KhaozEngine.Gpu/GpuBufferReadbackRing.cs`.
- Create/Test: `KhaozEngine.Render.Tests/Gpu/GpuBufferReadbackRingTests.cs`.
- Modify/Test: `KhaozEngine.Render.Tests/Gpu/ComputeAsyncReadbackGpuTests.cs` from Task 4.
- Modify docs: `docs/USING-KHAOZENGINE.md` in Task 4's asynchronous readback section, `KhaozEngine.Gpu/README.md:330-340`.

**Interfaces**

- Consumes: Task 4 `TryReadBuffer`, recorded `CopyBuffer`, and caller-owned completion fences.
- Produces `public sealed class GpuBufferReadbackRing : IDisposable` with constructor `(IGpuDevice device,int slotCount,uint slotSizeInBytes)`, `int Capacity { get; }`, `int PendingCount { get; }`, `bool TryRecord(IGpuCommandList commands,IGpuBuffer source,uint sourceOffsetBytes,uint sizeInBytes,ulong tag,IGpuFence completion)`, `bool TryRead(Span<byte> destination,out ulong tag,out uint sizeInBytes)`, `void DiscardUnsubmitted(IGpuFence completion)` and `void Dispose()`.

- [ ] Write `GpuBufferReadbackRingTests.FullRingDoesNotWaitAndPreservesFifoTags`. Create exactly 3 slots of 16 bytes and unsubmitted, unsignaled fences for tags 100, 101 and 102. Assert three records succeed and a fourth tag 103 returns false with no copy or allocation. Assert pending read false leaves destination unchanged and sets outputs to 0. Complete only the second fence and assert FIFO still returns false. Complete the first, assert bytes `[1u,2u,3u,4u]`, tag `100ul`, count `16u`. Record 103 into the released slot, then consume 101, 102, 103 in that order. Assert `Assert.Equal(3, ring.Capacity); Assert.Equal(0, waitCalls); Assert.Equal(0, flushCalls);`.
- [ ] Add `DiscardedRecordingReleasesOnlyItsUnsubmittedSlots`: record tags 200 and 201 behind fence A, tag 202 behind B, discard A before submission and assert only 202 remains and no fence is reset/disposed. Add small-destination refusal, `slotCount=0`, `slotSizeInBytes=0`, copy exceeding slot capacity, and pending disposal. Assert `ArgumentOutOfRangeException`, `ArgumentException`, or `InvalidOperationException` respectively. Warm one record/read on preallocated fakes, measure one subsequent record/read with `GC.GetAllocatedBytesForCurrentThread()` and assert the delta equals `0L`.

```csharp
// FullRingDoesNotWaitAndPreservesFifoTags, with all three slots retained
Assert.False(ring.TryRecord(commands, source, 0, 16, 103ul, fourthCompletion));
Assert.False(ring.TryRead(destination, out ulong tag, out uint count));
Assert.Equal(0ul, tag);
Assert.Equal(0u, count);
Assert.Equal(3, ring.Capacity);
Assert.Equal(3, ring.PendingCount);
Assert.Equal(0, waitCalls);
// One warmed record/read using preallocated fakes
Assert.Equal(0L, allocationDelta);
```

- [ ] Run red: `build-slot --label a5-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~GpuBufferReadbackRingTests`. Expected CS0246 for `GpuBufferReadbackRing`.
- [ ] Implement arrays and staging buffers allocated exactly once in the constructor. Reserve the next free slot only after validating the request. `TryRecord` copies to offset 0 in the caller's open frame list, retaining tag, byte count and completion reference. Full returns false before recording anything. `TryRead` attempts only the oldest pending slot through Task 4 and releases it only after a successful copy. Do not allocate an output array or grow a queue. `DiscardUnsubmitted` performs stable removal of matching entries in the fixed metadata array, never frees other frames. The caller must invoke it only for an abandoned list that was never submitted. `Dispose` refuses while pending entries remain and otherwise disposes only owned staging buffers.
- [ ] Add a native ring case with 3 slots, a recorded compute output and one frame submission. Drain only at test completion, assert the frame tag and bytes. Document Part C's usage: one aggregate actor-result copy per frame, tag is the frame ID, and actor IDs/offsets are fields in the copied payload. Optional surface verification can share the same stable frame fence. Caller submits once with that fence and cannot reset or dispose it until every associated ring has consumed or discarded its entries. If full, skip diagnostics for that frame, keep rendering and preserve the dispatched outcome.
- [ ] Run green: `a_check a5 'FullyQualifiedName~GpuBufferReadbackRingTests|FullyQualifiedName~GpuAsyncBufferReadTests|FullyQualifiedName~ComputeAsyncReadbackGpuTests'`.
- [ ] Root hosted checkpoint: `legs=all`, `renderTestFilter='FullyQualifiedName~ComputeAsyncReadbackGpuTests'`.
- [ ] Commit explicit task paths with `gpu(readback): add a fixed asynchronous buffer ring`.

## Task 6: Timestamp pool protocol and invalid-result semantics

**Files**

- Create: `KhaozEngine.Gpu/GpuTimestamps.cs`, `KhaozEngine.Gpu/Internal/GpuTimestampPoolState.cs`, `KhaozEngine.Gpu/Internal/GpuTimestampResults.cs`.
- Create/Test: `KhaozEngine.Render.Tests/Gpu/GpuTimestampPoolTests.cs`, `KhaozEngine.Render.Tests/Gpu/ComputeTimestampGpuTests.cs`.
- Modify: `KhaozEngine.Gpu/GpuCapabilities.cs:70-72`, `KhaozEngine.Gpu/GpuInterfaces.cs:158-174,399-400`.
- Modify/Test: `KhaozEngine.Render.Tests/Gpu/GpuCapabilitiesTests.cs:7-80` in default-capability tests.
- Modify docs: `docs/USING-KHAOZENGINE.md:16837-16860` and a new subsection beside GPU compute, `KhaozEngine.Gpu/README.md:417-453`.

**Interfaces**

- Consumes: `bool IGpuFence.Signaled`, `void IGpuDevice.Submit(IGpuCommandList,IGpuFence)` and the existing device-owned resource lifetime.
- Produces: `public enum GpuTimestampStatus { Pending, Ready, Invalid }` and `public interface IGpuTimestampPool : IDisposable` with `uint Capacity { get; }` and `GpuTimestampStatus TryResolve(IGpuFence completion,Span<ulong> ticks,out double tickPeriodNanoseconds)`.
- Produces: `bool GpuCapabilities.SupportsTimestamps { get; init; }`, default false, without changing existing constructor positions.
- Produces: `IGpuTimestampPool IGpuResourceFactory.CreateTimestampPool(uint capacity)`, `void IGpuCommandList.BeginTimestamps(IGpuTimestampPool pool,uint count)`, `void WriteTimestamp(IGpuTimestampPool pool,uint index)`, `void EndTimestamps(IGpuTimestampPool pool)`. Optional interface defaults throw `NotSupportedException` until a backend implements them.
- Produces internal state: `GpuTimestampPoolState(uint capacity)`, `void Begin(uint count)`, `void Write(uint index)`, `void End()`, `void Resolved()`, `uint Count { get; }`. State storage is fixed at creation.
- Produces internal normalization: `bool GpuTimestampResults.TryNormalize(ReadOnlySpan<ulong> raw,int validBits,Span<ulong> relativeTicks)`.

- [ ] Write `GpuTimestampPoolTests.DefaultSupportIsFalseAndIncompleteBatchesAreRefused`: assert `Assert.False(default(GpuCapabilities).SupportsTimestamps);`. A capacity-4 state accepts count 2 and unique indices 0 and 1, refuses count 0 or 5, index 2, duplicate index 0, index 1 before index 0, End with a missing index, and Begin while an ended batch is unresolved. Assert exact exception types `ArgumentOutOfRangeException` for numeric bounds and `InvalidOperationException` for lifecycle errors. Resolved permits the next batch, and beginning it clears the prior written-index state.
- [ ] Write `WrappedCountersNormalizeWithoutInventingTicks`: raw `[250ul,5ul,9ul]`, valid bits 8 gives `[0ul,11ul,15ul]`. For valid bits 64, raw `[ulong.MaxValue-1,1ul]` gives `[0ul,3ul]`. Valid bits 0/65 and an undersized output are rejected before changing output. A backend's unresolved or invalid result leaves caller ticks unchanged and sets period to `0d`.
- [ ] Add `ComputeTimestampGpuTests.PassBoundaryQueriesEitherResolveOrReportUnsupported`. On support false, assert pool creation throws `NotSupportedException`, without skipping the test. On support true, create capacity 4/count 3, place indices 0, 1 and 2 around a one-group compute writer and a draw that consumes it. Before submission assert status Pending. Submit with the supplied fence, drain once in the test, assert status Ready, `ticks[0]==0ul`, `ticks[1]>=ticks[0]`, `ticks[2]>=ticks[1]` and `double.IsFinite(period) && period>0d`. Assert compute output `17u` and the green draw pixel independently. No duration threshold, positive-minimum delta or timing report is asserted.

```csharp
// DefaultSupportIsFalseAndIncompleteBatchesAreRefused
Assert.False(default(GpuCapabilities).SupportsTimestamps);
// WrappedCountersNormalizeWithoutInventingTicks
ulong[] output = { 99ul, 99ul, 99ul };
Assert.True(GpuTimestampResults.TryNormalize(new ulong[] { 250ul, 5ul, 9ul }, 8, output));
Assert.Equal(new ulong[] { 0ul, 11ul, 15ul }, output);
Assert.False(GpuTimestampResults.TryNormalize(new ulong[] { 0ul, ulong.MaxValue, 0ul }, 64, output));
Assert.Equal(new ulong[] { 0ul, 11ul, 15ul }, output);
```

- [ ] Run red: `build-slot --label a6-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~GpuTimestampPoolTests`. Expected CS0246 for `GpuTimestampPoolState` or `IGpuTimestampPool`.
- [ ] Implement the types and fixed state. Require indices in increasing order from 0 to count minus 1, exactly once, one unresolved batch per pool, and no reuse until Ready/Invalid has been consumed. Pending/Invalid never modify caller ticks. Native backends fill private preallocated scratch first, then publish normalized elapsed ticks relative to index 0 only on Ready. Normalize each adjacent difference with the valid-bit mask and checked cumulative addition, treating cumulative overflow as invalid. Check the complete normalization before writing its output, using two passes so a failed normalization leaves it unchanged. The interval between adjacent queries must be shorter than a complete counter wrap, a documented device limitation rather than a guessed duration.
- [ ] Define the completion contract shared with Task 4: the supplied same-device fence belongs to the submission containing EndTimestamps and remains unreset until resolve completes. Begin/Write/End only encode work and never submit. Unsupported pool creation throws, and invalid or pending results carry no usable period. Document a fixed caller-owned pool ring with one pool per retained frame. When every pool is pending, record no timestamps for that frame. Replace the guide's blanket claim that no GPU timing API exists. Each backend reports support only after its subsequent native task is complete.
- [ ] Run green: `a_check a6 'FullyQualifiedName~GpuTimestampPoolTests|FullyQualifiedName~ComputeTimestampGpuTests|FullyQualifiedName~GpuCapabilitiesTests'`. The native case currently verifies the unsupported branch. It must later run its positive branch on each device that probes support.
- [ ] Commit explicit task paths with `gpu(timestamps): define fixed pool and non-blocking resolve contracts`.

## Task 7: Native Vulkan timestamp queries

**Files**

- Create: `KhaozEngine.Gpu.Vulkan/Internal/IVulkanTimestampApi.cs`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanTimestampApi.cs`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanTimestampPool.cs`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanCommandList.Timestamps.cs`.
- Create/Test: `KhaozEngine.Render.Tests/Gpu/VulkanTimestampTests.cs`.
- Modify: `KhaozEngine.Gpu.Vulkan/Internal/VulkanResourceFactory.cs:55-110,183-194`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanCapabilityRead.cs:126-142`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanPhysicalDeviceReader.cs:1-120`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanGpuDevice.Create.cs:190-215,366-375`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanCommandList.cs:68-220` in constructor dependencies.
- Modify docs: `docs/USING-KHAOZENGINE.md` in Task 6's timestamp subsection, `docs/CROSS-PLATFORM.md:460-487`.

**Interfaces**

- Consumes: all Task 6 public and internal contracts and the existing Vulkan timeline, owner retirement and rendering schedule.
- Produces internal `IVulkanTimestampApi` with `ulong CreatePool(uint capacity)`, `void DestroyPool(ulong pool)`, `void Reset(ulong commandBuffer,ulong pool,uint count)`, `void Write(ulong commandBuffer,ulong pool,uint index)`, `bool TryRead(ulong pool,uint count,Span<ulong> values,Span<ulong> availability)`. API handles remain `ulong`, native types stay inside `VulkanTimestampApi`.
- Produces `VulkanTimestampPool : IGpuTimestampPool` with constructor `(IVulkanTimestampApi api,VulkanTimeline timeline,VulkanResourceOwner owner,uint capacity,int validBits,double tickPeriodNanoseconds)` and the three Task 6 recording methods on `VulkanCommandList`.

- [ ] Write `VulkanTimestampTests.ProbeUsesTheSelectedQueueAndResolveNeverWaits`. Probe selected queue valid bits 0/36 and period 0/2.5, assert false/true only for nonzero bits and finite positive period. Fake query values `[250ul,5ul,9ul]` with bits 8 and availability `[1ul,0ul,1ul]`: assert Pending, unchanged output and period `0d`. With all available assert Ready, `[0ul,11ul,15ul]`, `2.5d`. Assert query flags are exactly 64-bit plus availability, stride `16ul`, with no WAIT or PARTIAL bit. Assert Reset precedes writes and active rendering ends before reset/write.

```csharp
// ProbeUsesTheSelectedQueueAndResolveNeverWaits, once all availability words are 1
Assert.Equal(GpuTimestampStatus.Ready, status);
Assert.Equal(new ulong[] { 0ul, 11ul, 15ul }, ticks);
Assert.Equal(2.5d, tickPeriodNanoseconds);
Assert.Equal(5u, nativeQueryResultFlags);
Assert.Equal(16ul, nativeQueryStride);
Assert.Equal(0, waitCalls);
```

- [ ] Run red: `build-slot --label a7-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~VulkanTimestampTests`. Expected CS0246 for `IVulkanTimestampApi`.
- [ ] Implement one fixed `VK_QUERY_TYPE_TIMESTAMP` pool and private value/availability scratch per wrapper. Probe the selected graphics/compute queue's `timestampValidBits`, not any queue, and the physical device's `limits.timestampPeriod`. Record `vkCmdResetQueryPool` for the requested range and `vkCmdWriteTimestamp2` with `ALL_COMMANDS`, ending active rendering through the existing schedule. End closes the batch without a CPU query. TryResolve first polls the supplied completion, then calls `vkGetQueryPoolResults` once without WAIT, checks every availability word and normalizes through Task 6. Query/reset errors use the existing device-loss behavior. Destruction uses the existing owner retirement path and never drains per resolve.
- [ ] Wire factory creation and live capabilities. Keep support false if the selected queue has zero valid bits or no finite positive period. Unsupported is not a fabricated software timestamp. Add the exact Vulkan mechanism and counter-wrap limitation to the living timestamp docs.
- [ ] Run green: `a_check a7 'FullyQualifiedName~VulkanTimestampTests|FullyQualifiedName~GpuTimestampPoolTests|FullyQualifiedName~ComputeTimestampGpuTests'`.
- [ ] Root hosted checkpoint: `legs=vulkan-native`, `renderTestFilter='FullyQualifiedName~VulkanTimestampTests|FullyQualifiedName~ComputeTimestampGpuTests|FullyQualifiedName~ComputeBufferOrderingGpuTests'`. Require strict and synchronization validation logs free of errors. An unavailable timestamp device must exercise the negative branch and report its device probe evidence.
- [ ] Commit explicit task paths with `gpu(timestamps): implement native Vulkan query pools`.

## Task 8: Native D3D11 timestamps with disjoint validity

**Files**

- Create: `KhaozEngine.Gpu.D3D11/Internal/ID3D11TimestampApi.cs`, `KhaozEngine.Gpu.D3D11/Internal/D3D11TimestampApi.cs`, `KhaozEngine.Gpu.D3D11/Internal/D3D11TimestampPool.cs`, `KhaozEngine.Gpu.D3D11/Internal/D3D11CommandRecorder.Timestamps.cs`, `KhaozEngine.Gpu.D3D11/Internal/D3D11NativeEmitter.Timestamps.cs`.
- Create/Test: `KhaozEngine.Render.Tests/Gpu/D3D11TimestampTests.cs`.
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11ResourceFactory.cs:83-127,237-255`, `KhaozEngine.Gpu.D3D11/Internal/D3D11CapabilityRead.cs:175-187`, `KhaozEngine.Gpu.D3D11/Internal/D3D11DxgiQueries.cs:44-57`.
- Modify: `KhaozEngine.Gpu.D3D11/Internal/D3D11CommandRecorder.cs:49-50` to make it partial, `KhaozEngine.Gpu.D3D11/Internal/ID3D11Emitter.cs:128-151`, `KhaozEngine.Gpu.D3D11/Internal/D3D11StreamEmitter.cs:160-171`, `KhaozEngine.Gpu.D3D11/Internal/D3D11OpCode.cs:80-86`, `KhaozEngine.Gpu.D3D11/Internal/D3D11StreamReplay.cs:128-155`, `KhaozEngine.Gpu.D3D11/Internal/D3D11CountingEmitter.cs:150-171`, `KhaozEngine.Gpu.D3D11/Internal/D3D11NativeTraceEmitter.cs:430-474` at their record/replay and trace dispatches. Native timestamp bodies live in the new partial, not the main emitter.
- Modify docs: `docs/USING-KHAOZENGINE.md` in Task 6's timestamp subsection, `docs/CROSS-PLATFORM.md:432-443`.

**Interfaces**

- Consumes: Task 6 contracts and D3D11's existing immediate-context lock and command stream.
- Produces internal `ID3D11TimestampApi` with `object CreateTimestamp()`, `object CreateDisjoint()`, `void Begin(object disjoint)`, `void End(object query)`, `bool TryReadTimestamp(object query,out ulong value)`, `bool TryReadDisjoint(object query,out ulong frequency,out bool disjoint)`, `void Destroy(object query)`.
- Produces `D3D11TimestampPool : IGpuTimestampPool` with constructor `(ID3D11TimestampApi api,D3D11FenceSubsystem fences,object submitLock,uint capacity)`, plus Task 6 recording methods on `D3D11CommandRecorder<TEmitter>`. Add `void BeginTimestamps(IGpuTimestampPool,uint)`, `void WriteTimestamp(IGpuTimestampPool,uint)` and `void EndTimestamps(IGpuTimestampPool)` to `ID3D11Emitter` and all its implementations.

- [ ] Write `D3D11TimestampTests.DisjointAndUnavailableQueriesNeverProduceMeasuredTime`. Fake frequency `10000000ul`, raw values `[100ul,125ul]` and non-disjoint ready data give Ready, ticks `[0ul,25ul]` and period `100d`. Any unavailable query gives Pending. Ready disjoint true or frequency 0 gives Invalid, period `0d`, unchanged output. Assert each native GetData uses `D3D11_ASYNC_GETDATA_DONOTFLUSH=1`, no flush/wait, and lock contention returns Pending. An unsupported-query creation failure for either timestamp or disjoint query returns support false and disposes the other probe object.
- [ ] Write `D3D11TimestampTests.DeferredReplayRetainsTheQueriesAndTheirOrdering`: record Begin, Write 0, a dispatch, Write 1, End, then replay. Assert the exact sequence `Begin(disjoint), End(timestamp0), Dispatch(1,1,1), End(timestamp1), End(disjoint)`. Before replay no native query calls occur. Stream resource references retain the pool until replay/stream disposal. Also run the immediate driver through the same recorder.

```csharp
// DisjointAndUnavailableQueriesNeverProduceMeasuredTime
Assert.Equal(GpuTimestampStatus.Ready, validStatus);
Assert.Equal(new ulong[] { 0ul, 25ul }, ticks);
Assert.Equal(100d, validPeriod);
Assert.Equal(GpuTimestampStatus.Pending, unavailableStatus);
Assert.Equal(GpuTimestampStatus.Invalid, disjointStatus);
Assert.Equal(GpuTimestampStatus.Invalid, zeroFrequencyStatus);
Assert.Equal(0d, invalidPeriod);
Assert.Equal(1u, getDataFlags);
Assert.Equal(0, flushCalls);
```

- [ ] Run red: `build-slot --label a8-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~D3D11TimestampTests`. Expected CS0246 for `ID3D11TimestampApi`.
- [ ] Implement fixed timestamp query objects plus one disjoint query per pool. Probe the actual device by creating both query kinds, without timing a workload. Unsupported query creation reports false, while removal or other device faults retain the existing failure path. Begin starts the disjoint query, Write ends the selected timestamp query, End ends disjoint. All operations reach native calls through the shipped recorder/emitter path, including deferred replay, with no FXC/DX12 query substitute. TryResolve uses `Monitor.TryEnter` for the immediate-context lock, polls the supplied completion and issues one non-flushing GetData per required query. A valid batch's period is exactly `1_000_000_000d / frequency`. Invalidate disjoint/zero frequency. A device error follows existing removal handling.
- [ ] Wire live capabilities and factory creation. Document one disjoint batch per frame or less, one outstanding batch per pool, and that invalid samples do not enter the GPU gate. Query disposal obeys existing submitted-resource lifetime rules, without adding a resolve-time drain.
- [ ] Run green: `a_check a8 'FullyQualifiedName~D3D11TimestampTests|FullyQualifiedName~GpuTimestampPoolTests|FullyQualifiedName~ComputeTimestampGpuTests|FullyQualifiedName~D3D11CommandStreamTests|FullyQualifiedName~D3D11ComputePathTests'`.
- [ ] Root hosted checkpoint: `legs=direct3d11-native`, `renderTestFilter='FullyQualifiedName~D3D11TimestampTests|FullyQualifiedName~ComputeTimestampGpuTests|FullyQualifiedName~ComputeIntegerAtomicGpuTests|FullyQualifiedName~ComputePreciseMathGpuTests'`, pinned WARP.
- [ ] Commit explicit task paths with `gpu(timestamps): implement D3D11 disjoint query batches`.

## Task 9: Native Metal pass-boundary counters and calibrated tick period

**Files**

- Create: `KhaozEngine.Gpu.Metal/Internal/IMetalTimestampApi.cs`, `KhaozEngine.Gpu.Metal/Internal/MetalTimestampApi.cs`, `KhaozEngine.Gpu.Metal/Internal/MetalTimestampPool.cs`, `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.Timestamps.cs`, `KhaozEngine.Gpu.Metal/Internal/ObjC/MTLTimestampInterop.cs`.
- Create/Test: `KhaozEngine.Render.Tests/Gpu/MetalTimestampTests.cs`.
- Modify: `KhaozEngine.Gpu.Metal/Internal/MetalResourceFactory.cs:51-64,102-104`, `KhaozEngine.Gpu.Metal/Internal/MetalCapabilityRead.cs:188-199`, `KhaozEngine.Gpu.Metal/Internal/MetalGpuDevice.Create.cs:255-261`, `KhaozEngine.Gpu.Metal/Internal/MetalCommandList.cs:54-170` in constructor dependencies, `KhaozEngine.Gpu.Metal/Internal/MetalEncoderScope.cs:188-246`.
- Modify docs: `docs/USING-KHAOZENGINE.md` in Task 6's timestamp subsection, `KhaozEngine.Gpu/README.md:417-453`, `docs/CROSS-PLATFORM.md:260-263,466-487`.

**Interfaces**

- Consumes: Task 6 contracts, tracked shared buffers, the existing encoder scope and Metal completion timeline.
- Produces internal `readonly record struct MetalTimestampResources(IntPtr Samples,IntPtr ResultBuffer,IntPtr ResultContents,IntPtr MarkerSource,IntPtr MarkerDestination,IntPtr Event,IntPtr[] BoundaryDescriptors)` in `MetalTimestampApi.cs`.
- Produces internal `IMetalTimestampApi` with `bool SupportsTimestampCounters { get; }`, `bool SupportsStageBoundary { get; }`, `bool SupportsBlitBoundary { get; }`, `MetalTimestampResources CreatePool(uint capacity,bool stageBoundary)`, `void DestroyPool(in MetalTimestampResources resources)`, `void SampleClocks(out ulong cpuNanoseconds,out ulong gpuTicks)`, `void WriteBoundary(IntPtr commandBuffer,in MetalTimestampResources resources,uint index,ulong beforeValue,ulong afterValue,bool stageBoundary)`, `void Resolve(IntPtr commandBuffer,in MetalTimestampResources resources,uint count)`, `void CopyResolved(in MetalTimestampResources resources,uint count,Span<ulong> raw)`.
- Produces `MetalTimestampPool : IGpuTimestampPool` with constructor `(IMetalTimestampApi api,MetalTimeline timeline,IDeviceLiveness liveness,uint capacity)` and Task 6 recording methods on `MetalCommandList`. Prefer the stage-boundary mode when both modes exist. Interop wrappers remain internal and toolchain-free outside the ObjC/native API implementation.

- [ ] Write `MetalTimestampTests.StageOnlyDevicesSampleThroughPassAttachments`. Timestamp counter set present, stage support true, blit-command support false and successful sample-buffer creation must yield support true. Assert every boundary closes the previous encoder and uses a blit-pass counter attachment at the requested end-of-encoder index, with the other sample index set to `MTLCounterDontSample`. A fixed 4-byte marker copy makes the marker pass nonempty. Pool allocation for capacity 4 returns exactly four boundary descriptors, and a second completed batch creates no new descriptor, buffer or event. Test the command-boundary-only device separately: its sample command uses `barrier=true`. Assert the GPU trace is prior encoder end, signal/wait value 1, marker pass, signal/wait value 2, next encoder. Neither path calls a selector unsupported by that mode. Missing counter set, both modes false or failed sample-buffer creation yields support false.
- [ ] Write `MetalTimestampTests.PeriodComesFromCorrelatedHardwareClocks`. Clock pairs `(cpu=1000,gpu=100)` and `(cpu=2000,gpu=600)` give `2d` nanoseconds per raw GPU tick. Raw counters `[150ul,175ul,200ul]` give `[0ul,25ul,50ul]`. Assert Pending before completion without a second clock read. After completion assert Ready and period `2d`. Equal/reversed clock spans, a counter outside the calibration interval, or a zero sample or `MTLCounterErrorValue` gives Invalid, period `0d` and unchanged caller ticks. A native error or dead device never reads shared memory. Assert no wait or CPU stopwatch duration substitutes for counters.

```csharp
// StageOnlyDevicesSampleThroughPassAttachments
Assert.True(stageOnlyDeviceSupport);
Assert.Equal(0, unsupportedSelectorCalls);
Assert.Equal(new ulong[] { 1ul, 2ul }, encodedEventValues);
// PeriodComesFromCorrelatedHardwareClocks
Assert.Equal(GpuTimestampStatus.Ready, status);
Assert.Equal(new ulong[] { 0ul, 25ul, 50ul }, ticks);
Assert.Equal(2d, tickPeriodNanoseconds);
Assert.Equal(GpuTimestampStatus.Invalid, sentinelStatus);
Assert.Equal(0d, invalidPeriod);
Assert.Equal(0, cpuWaitCalls);
```

- [ ] Run red: `build-slot --label a9-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~MetalTimestampTests`. Expected CS0246 for `IMetalTimestampApi`.
- [ ] Implement ObjC selectors in the new focused interop file, leaving the near-capacity `ObjCMsgSend.cs` alone. Discover `MTLCommonCounterSetTimestamp`, check supported sampling boundaries and trial-create a sample buffer when building device capabilities. Release the probe immediately. `MetalTimestampApi.CreatePool` allocates and owns one fixed counter sample buffer, shared resolved-result buffer and two 4-byte marker buffers plus one private shared event for GPU-only ordering. `DestroyPool` releases all resources including each descriptor. No per-write descriptor/native object creation: create fixed marker-pass descriptors for the capacity at pool creation and reuse them only after the prior batch resolves.
- [ ] Record each WriteTimestamp at a real GPU boundary. End the current encoder through `MetalEncoderScope.EnsureNoEncoder()`. For stage-boundary devices open a dedicated nonempty blit pass with that index's sample attachment, copy the fixed 4-byte marker and close it. For command-boundary devices encode the marker and `sampleCountersInBuffer:atSampleIndex:withBarrier:` in a blit encoder and close it. Preserve pending framebuffer clears and graphics/compute bind dirtiness through the existing encoder epoch mechanism. EndTimestamps records `resolveCounters:inRange:destinationBuffer:destinationOffset:` to the preallocated shared result buffer and closes its encoder. It never calls the allocating CPU `resolveCounterRange:`. A timestamp marker must also order work that shares no resource with it. Before the marker, encode a private event signal followed by a GPU event wait after closing the previous encoder. After the marker, encode the next signal/wait pair before subsequent encoders can open. Use strictly increasing event values for the pool lifetime and the [Metal event ordering contract](https://developer.apple.com/documentation/metal/mtlevent). These are encoded GPU dependencies, never CPU waits. Keep the event selectors in the new interop file and do not borrow or advance the device completion timeline.
- [ ] Sample the correlated CPU/GPU clocks once at BeginTimestamps, and again only when TryResolve sees completion. Cache that pair for repeated resolves of the same batch. Derive period from the difference of the API's CPU nanoseconds divided by the GPU-tick difference. This is hardware clock calibration of GPU counter samples, not elapsed CPU time around a pass. Reject unusable calibration or sample sentinels as Invalid. Preserve a finite positive period only for Ready. No `1 ns` constant, command-buffer duration estimate, CPU timing fallback or per-frame retry loop is allowed.
- [ ] Document the marker cost as part of instrumented GPU time, never subtract an estimated overhead. Hosted Apple Paravirtual may report unsupported, in which case its negative test must run and root records the gate as unmeasurable there. On the local Metal device the native correctness case must exercise the positive path if its probe supports it. Real-hardware budget timing still requires the owner's later approval and chosen device.
- [ ] Run green: `a_check a9 'FullyQualifiedName~MetalTimestampTests|FullyQualifiedName~GpuTimestampPoolTests|FullyQualifiedName~ComputeTimestampGpuTests|FullyQualifiedName~ComputeAsyncReadbackGpuTests|FullyQualifiedName~ComputeBufferOrderingGpuTests|FullyQualifiedName~ComputeVertexPullingGpuTests|FullyQualifiedName~ComputeIntegerAtomicGpuTests|FullyQualifiedName~ComputePreciseMathGpuTests|FullyQualifiedName~ComputeBufferGpuTests|FullyQualifiedName~ComputeTextureHandoffGpuTests|FullyQualifiedName~ComputeFftGpuTests'`.
- [ ] Root hosted checkpoint: `legs=all`, `renderTestFilter='FullyQualifiedName~Compute|FullyQualifiedName~VertexStageBufferSeamGpuTests'`, plus the standard Vulkan sync job. Record every device's timestamp probe and whether the positive or unsupported branch ran. Accept no silent skip or estimated GPU result.
- [ ] Run the relevant doc/governance guards once: `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree`, `bash scripts/check-doc-versions.sh` and `git diff --check`. Sweep changed API names and old drain/timer claims across Markdown, package readmes and native comments. Update living references, leave historical designs marked as superseded rather than rewriting their evidence. If a guard exposes unrelated work, report it to root without widening this task.
- [ ] Commit explicit task paths with `gpu(timestamps): implement Metal counter sampling and clock calibration`.

## Part C handoff and root integration gate

Part C uses one command list for its seven compute stages and all consuming draws. RW scratch/final buffers bind as RW during compute and RO in precise vertex shaders through vertex-index pulling. It uses Task 2's uint atomics, Task 3's source qualifiers/options, Task 5's aggregate frame-tagged readback and Tasks 6 to 9's fixed timestamp pools. It owns the stable frame-fence lifetime shared by readbacks and query resolution. A full ring or pool set drops telemetry for that frame without changing drawing decisions.

Root reconciles these APIs with the Part B/C plans before implementation. Epsilon, actor records, support-grid layout, refusal payloads and history are not defined here. No Part A result replaces Part B's exact oracle or Part C's native device-equivalence tests.

Root owns final current-main reconciliation, full API/doc sweep, version/changelog, whole-branch review and integration. After reconciliation, run once through the heavy lane:

```bash
build-slot --heavy --label ec-a-full-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label ec-a-full-tests -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
```

Run changed-file whitespace verification again with this exact execution-shell command, using the start commit retained before Task 1:

```bash
a_final_cs=()
while IFS= read -r a_path; do
    test -n "$a_path" && a_final_cs+=("$a_path")
done < <(git diff --name-only --diff-filter=ACMR "$a_start_commit" HEAD -- '*.cs')
build-slot --label ec-a-final-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore --include "${a_final_cs[@]}"
```

Root then requests the hosted all-leg full `cross-platform-gpu` verification with empty `renderTestFilter`, `bake=false`, and checks zero unexpected skips and clean Vulkan strict/sync logs. This is required because plain local solution tests skip GPU facts. No implementation worker merges, releases or cleans another worktree. No performance pilot is authorized by this plan.

## Self-review

- Part A item 1 maps to Task 1's dispatch and graphics hazards, including the existing texture regression cases.
- Part A item 2 maps to Task 1's existing vertex storage seam plus first/base vertex pixel assertions. No redundant resource abstraction is introduced.
- Part A item 3 maps to Task 2's unsigned OR/MAX, old-value checks, emitted forms and real compiler/device proofs.
- Part A item 4 maps to Tasks 4 and 5's non-blocking native read and fixed frame-tagged ring.
- Part A item 5 maps to Tasks 6 to 9's fixed pool, WriteTimestamp, resolve status, device probe, validity and real tick-period mechanisms.
- The root precision addition maps to Task 3's compute/vertex option, SPIR-V decorations, SPIRV-Cross lowering, FXC strictness, Metal per-stage options, native arithmetic and cache tests.
- `GpuShaderCompileOptions.PreciseMath`, `TryReadBuffer`, `GpuBufferReadbackRing.TryRecord/TryRead`, `SupportsTimestamps`, `BeginTimestamps/WriteTimestamp/EndTimestamps` and `IGpuTimestampPool.TryResolve` have one spelling and type contract throughout. Period is `double` nanoseconds per tick, normalized ticks are `ulong`, frame tags are `ulong`, and byte ranges are `uint`.
- Review Focus 1 and 2 are placed in Task 1, Focus 3 in Task 3, Focus 4 in Tasks 4 and 5, and Focus 5 in Tasks 6 to 9. Each has exact negative assertions rather than relying on successful shader creation.
- The plan describes decisions, signatures, native mechanisms and test assertions. It omits determined function bodies and the coverage algorithm. Nine independently reviewable cycles are shorter than the native implementation and tests they describe. Remaining root gates are native execution evidence, cross-part API agreement and separately approved timing/release work.
