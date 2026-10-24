using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfMetalDiagnosticsTests
{
    [Fact]
    public void Real_resource_owners_track_success_once_and_remove_on_dispose_after_device_death()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new Resources();
        var live = new DeviceLiveness();
        using var buffer = MetalBuffer.Create(native, live, new(17, GpuBufferUsage.VertexBuffer), null, memory);
        using var cube = MetalTexture.Create(native, live, new(4, 2, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.Cubemap, mipLevels: 3, arrayLayers: 2), memory);
        using var msaa = MetalTexture.Create(native, live, new(2, 2, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget, sampleCount: 4), memory);
        Assert.Equal(17ul, memory.ReadMemorySnapshot().LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(592ul, memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(609ul, memory.ReadMemorySnapshot().PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(3ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(3ul, memory.PeakResourceCount);
        Assert.Equal(new nuint[] { 20 }, native.BufferSizes);
        cube.Dispose(); cube.Dispose();
        Assert.Single(native.Released);
        Assert.Equal(64ul, memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        live.MarkDead();
        buffer.Dispose(); msaa.Dispose();
        Assert.Single(native.Released);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(609ul, memory.ReadMemorySnapshot().PeakLogicalPayloadBytes.Value!.UInt64);
    }

    [Fact]
    public void Failed_native_creation_and_missing_mapping_have_no_partial_ledger()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new Resources { Nil = true };
        var live = new DeviceLiveness();
        Assert.Throws<InvalidOperationException>(() => MetalBuffer.Create(native, live, new(16, GpuBufferUsage.VertexBuffer), null, memory));
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        native.Nil = false;
        native.Mapped = false;
        Assert.Throws<InvalidOperationException>(() => MetalTexture.Create(native, live,
            new(4, 4, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging), memory));
        Assert.Single(native.Released);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        native.Mapped = true;
        Assert.Throws<ArgumentOutOfRangeException>(() => MetalTexture.Create(native, live,
            new(1, 1, (GpuPixelFormat)int.MaxValue, GpuTextureUsage.Sampled), memory));
        Assert.Equal(2, native.Released.Count);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        Assert.Throws<OverflowException>(() => memory.TrackTexture(new(uint.MaxValue, uint.MaxValue,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled)));
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
    }

    [Fact]
    public void Ring_buffer_owner_counts_its_window_once_and_all_native_segments_as_capacity()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new Resources();
        using var harness = new MetalRingHarness(3);
        using var buffer = MetalBuffer.Create(native, harness.Liveness, new(48, GpuBufferUsage.UniformBuffer), harness.Rings, memory);
        Assert.NotNull(buffer.Ring);
        Assert.Equal(new nuint[] { 768 }, native.BufferSizes);
        var snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(48ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(768ul, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(48ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        buffer.Dispose(); buffer.Dispose();
        Assert.Single(native.Released);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
    }

    [Fact]
    public void Native_mapping_exception_releases_the_successful_allocation_without_ledger_entry()
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new Resources { ContentsFailure = new InvalidOperationException("native contents query failed") };
        var live = new DeviceLiveness();
        Assert.Throws<InvalidOperationException>(() => MetalBuffer.Create(native, live, new(16, GpuBufferUsage.VertexBuffer), null, memory));
        Assert.Single(native.Released);
        Assert.Throws<InvalidOperationException>(() => MetalTexture.Create(native, live,
            new(2, 2, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging), memory));
        Assert.Equal(2, native.Released.Count);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
    }

    [Fact]
    public void Staging_arena_counts_live_blocks_once_across_reuse_trim_and_disposal()
    {
        var memory = new MetalMemoryDiagnostics();
        using var native = new FakeMetalStagingSource();
        using var arena = new MetalStagingArena(new MetalMemoryStagingSource(native, memory), 2,
            blockBytes: 16, retentionBytes: 16);
        _ = arena.Take(12);
        _ = arena.Take(8);
        Assert.Equal(32ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(2ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        arena.BeginSlot(0, 0);
        Assert.Equal(16ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
        _ = arena.Take(4);
        Assert.Equal(16ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
        arena.Dispose(); arena.Dispose();
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_staging_tracks_release_and_dead_abandonment_without_extra_native_work(bool dead)
    {
        var memory = new MetalMemoryDiagnostics();
        var native = new FakeMetalSetupNative();
        var live = new DeviceLiveness();
        using var setup = new MetalSetupCommands(new MetalMemorySetupNative(native, memory), live);
        setup.Upload(new MTLTexture((IntPtr)100), new(2, 2, 1, 1, GpuPixelFormat.R8UNorm),
            new(0, 0, 0, 0, 2, 2), new byte[4]);
        Assert.Equal(4ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
        if (dead) live.MarkDead();
        setup.Dispose(); setup.Dispose();
        Assert.Equal(0ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(dead ? 0 : 1, native.ReleasedStaging.Count);
        Assert.Empty(native.Committed);
    }

    [Fact]
    public void Compute_identity_hashes_source_emission_layout_local_size_and_compile_configuration()
    {
        var live = new DeviceLiveness();
        var table = MetalShaderIndexTable.Build([], [], "empty");
        var evidence = MetalShaderEvidence.Capture(new(MetalShaderStage.Compute, "entry", "msl"), "glsl");
        string Identity(MetalShaderEvidence e, uint x = 8)
        {
            using var shader = new MetalComputeShader(live, default, table, x, 1, 1, e);
            using var pipeline = new MetalComputePipeline(live, shader, [], default);
            return pipeline.ReadCompilerReport().Identity.PipelineSha256.Sha256!;
        }
        string original = Identity(evidence);
        Assert.Equal(original, Identity(evidence));
        Assert.NotEqual(original, Identity(evidence with { Msl = "msl preserving precision" }));
        Assert.NotEqual(original, Identity(evidence with { EntryPoint = "other" }));
        Assert.NotEqual(original, Identity(evidence with { Configuration = evidence.Configuration + ";capture=changed" }));
        Assert.NotEqual(original, Identity(evidence, 16));
        using var layout = new MetalResourceLayout(live, new(new GpuResourceLayoutElement("input", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute)));
        Assert.NotEqual(MetalPipelineIdentity.Layout([]), MetalPipelineIdentity.Layout([layout]));
        using var missingShader = new MetalComputeShader(live, default, table, 8, 1, 1);
        using var missingPipeline = new MetalComputePipeline(live, missingShader, [], default);
        var missing = missingPipeline.ReadCompilerReport();
        Assert.Null(missing.Identity.PipelineSha256.Sha256);
        Assert.Null(missing.Identity.SourceSha256.Sha256);
        Assert.Empty(missing.Executables);
    }

    [Fact]
    public void Shader_evidence_and_resolved_graphics_identity_are_immutable_and_stage_complete()
    {
        var live = new DeviceLiveness();
        var evidence = new[]
        {
            MetalShaderEvidence.Capture(new(MetalShaderStage.Vertex, "v", "vertex msl"), "vertex glsl"),
            MetalShaderEvidence.Capture(new(MetalShaderStage.Fragment, "f", "fragment msl"), "fragment glsl")
        };
        using var shaders = new MetalShaderSet(live, [], MetalShaderIndexTable.Build([], [], "empty"), evidence);
        var d = new GpuPipelineDescription
        {
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm),
            BlendAttachments = [GpuBlendAttachment.OverrideBlend]
        };
        using var first = new MetalGraphicsPipeline(live, MetalGraphicsPipelinePlan.Build(live, d), default, default);
        var report = first.ReadCompilerReport();
        Assert.Equal(2, report.Executables.Length);
        d.Outputs.Colour[0] = GpuPixelFormat.R16G16B16A16Float;
        using var changed = new MetalGraphicsPipeline(live, MetalGraphicsPipelinePlan.Build(live, d), default, default);
        Assert.NotEqual(report.Identity.PipelineSha256.Sha256, changed.ReadCompilerReport().Identity.PipelineSha256.Sha256);
        evidence[0] = evidence[0] with { Msl = "different emitted MSL" };
        Assert.Equal(report.Identity.PipelineSha256.Sha256, first.ReadCompilerReport().Identity.PipelineSha256.Sha256);
        first.Dispose();
        Assert.Equal(GpuDiagnosticStatus.Invalid, first.ReadCompilerReport().Status);
        shaders.Dispose();
        Assert.Equal(GpuDiagnosticStatus.Invalid, changed.ReadCompilerReport().Status);
    }

    sealed class Resources : IMetalResourceNative
    {
        int _next = 1;
        internal bool Nil, Mapped = true;
        internal Exception? ContentsFailure;
        internal readonly List<nuint> BufferSizes = [];
        internal readonly List<IntPtr> Released = [];
        public MTLBuffer NewBuffer(nuint size) { BufferSizes.Add(size); return new(Nil ? IntPtr.Zero : (IntPtr)_next++); }
        public IntPtr Contents(MTLBuffer buffer) => ContentsFailure is not null ? throw ContentsFailure : Mapped ? buffer.Handle : IntPtr.Zero;
        public MTLTexture NewTexture(in GpuTextureDescription d, in MetalTextureViewPlan plan)
            => new(Nil ? IntPtr.Zero : (IntPtr)_next++);
        public void Release(IntPtr handle) => Released.Add(handle);
    }
}
