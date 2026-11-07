using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfVulkanDiagnosticsTests
{
    [Fact]
    public void Device_snapshot_counts_requested_bytes_and_all_cubemap_faces()
    {
        using var fixture = new VulkanDeviceFixture();
        using IGpuBuffer buffer = fixture.Device.Factory.CreateBuffer(new GpuBufferDescription(13, GpuBufferUsage.VertexBuffer));
        using IGpuTexture cube = fixture.Device.Factory.CreateTexture(new GpuTextureDescription(
            4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap,
            mipLevels: 3, arrayLayers: 2));
        GpuResourceMemorySnapshot snapshot = ((IGpuResourceMemoryDiagnostics)fixture.Device).ReadMemorySnapshot();
        Assert.Equal(13ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(1008ul, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(1021ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(2ul, snapshot.LiveResourceCount.Value!.UInt64);
        fixture.Device.Dispose();
        Assert.Equal(GpuDiagnosticStatus.Invalid,
            ((IGpuResourceMemoryDiagnostics)fixture.Device).ReadMemorySnapshot().CommittedBytes.Status);
    }

    [Fact]
    public void Multisample_payload_and_staging_capacity_have_separate_sources()
    {
        var fixture = new VulkanResourceFixture(maxMsaaSampleCount: 4);
        using var memory = fixture.Memory;
        using IGpuTexture multisample = fixture.Factory.CreateTexture(new GpuTextureDescription(
            4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget, sampleCount: 4));
        using IGpuTexture staging = fixture.Factory.CreateTexture(new GpuTextureDescription(
            4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
        GpuResourceMemorySnapshot snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(320ul, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(64ul, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Contains("overlaps", snapshot.StagingCapacityBytes.Source);
    }

    [Fact]
    public void Factories_bind_diagnostics_to_both_pipeline_types_and_disposal_refuses_queries()
    {
        var fixture = new VulkanResourceFixture();
        using var memory = fixture.Memory;
        var executable = new ExecutableApi();
        var diagnostics = new VulkanPipelineDiagnostics(true, true, true, executable, "validation=off");
        var api = new DiagnosticPipelineApi(diagnostics, executable);
        var pipelines = new VulkanPipelines(new(api, fixture.Timeline, fixture.Retired),
            fixture.Descriptors.PipelineLayouts, null);
        using IGpuShaderSet shaders = fixture.Factory.CreateShadersFromSpirv(
            "#version 450\nvoid main(){gl_Position=vec4(0);}",
            "#version 450\nlayout(location=0) out vec4 C; void main(){C=vec4(1);}");
        using IGpuComputeShader compute = fixture.Factory.CreateComputeShaderFromSpirv(
            "#version 450\nlayout(local_size_x=8,local_size_y=4,local_size_z=2) in; void main(){}");
        var description = new GpuPipelineDescription
        {
            ShaderSet = shaders,
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            Outputs = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm),
            Rasterizer = new GpuRasterizerState(GpuFaceCull.Back, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: true, scissorTestEnabled: true),
            DepthStencil = GpuDepthStencilState.Disabled,
            Topology = GpuPrimitiveTopology.TriangleList
        };
        using IGpuPipeline graphics = pipelines.CreateGraphics(description);
        GpuPipelineCompilerReport first = ((IGpuPipelineDiagnostics)graphics).ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Available, first.Status);
        Assert.Equal(64, first.Identity.PipelineSha256.Sha256!.Length);
        Assert.Equal(64, first.Identity.SourceSha256.Sha256!.Length);
        Assert.Equal(64, first.Identity.ShaderArtifactSha256.Sha256!.Length);
        Assert.Equal(64, first.Identity.LayoutSha256.Sha256!.Length);
        Assert.Contains("opt=", first.Identity.CompilerFlags);

        using IGpuPipeline repeat = pipelines.CreateGraphics(description);
        Assert.Equal(first.Identity.PipelineSha256.Sha256, ((IGpuPipelineDiagnostics)repeat).ReadCompilerReport().Identity.PipelineSha256.Sha256);
        description.Topology = GpuPrimitiveTopology.LineList;
        using IGpuPipeline changed = pipelines.CreateGraphics(description);
        Assert.NotEqual(first.Identity.PipelineSha256.Sha256, ((IGpuPipelineDiagnostics)changed).ReadCompilerReport().Identity.PipelineSha256.Sha256);

        using IGpuComputePipeline kernel = pipelines.CreateCompute(new GpuComputePipelineDescription(compute));
        GpuPipelineCompilerReport report = ((IGpuPipelineDiagnostics)kernel).ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Available, report.Status);
        Assert.Equal(8u, Assert.Single(report.Executables).LocalSizeX);
        var witness = ((IGpuPipelineConstructionDiagnostics)kernel).ConstructionIdentity;
        int getterCalls = executable.Calls;
        Assert.Same(witness, ((IGpuPipelineConstructionDiagnostics)kernel).ConstructionIdentity);
        Assert.Same(report.Identity, witness);
        Assert.Equal(getterCalls, executable.Calls);
        kernel.Dispose();
        graphics.Dispose();
        Assert.Same(witness, ((IGpuPipelineConstructionDiagnostics)kernel).ConstructionIdentity);
        int calls = executable.Calls;
        Assert.Equal(GpuDiagnosticStatus.Invalid, ((IGpuPipelineDiagnostics)kernel).ReadCompilerReport().Status);
        Assert.Equal(GpuDiagnosticStatus.Invalid, ((IGpuPipelineDiagnostics)graphics).ReadCompilerReport().Status);
        Assert.Equal(calls, executable.Calls);
        pipelines.DestroyAll();
    }

    [Fact]
    public void Production_pipeline_keeps_identity_when_capture_is_unavailable()
    {
        using var fixture = new VulkanDeviceFixture();
        using IGpuComputeShader shader = fixture.Device.Factory.CreateComputeShaderFromSpirv(
            "#version 450\nlayout(local_size_x=8) in; void main(){}");
        using IGpuComputePipeline pipeline = fixture.Device.Factory.CreateComputePipeline(new GpuComputePipelineDescription(shader));
        GpuPipelineCompilerReport report = ((IGpuPipelineDiagnostics)pipeline).ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Unsupported, report.Status);
        Assert.Equal(64, report.Identity.PipelineSha256.Sha256!.Length);
        Assert.Equal(64, report.Identity.SourceSha256.Sha256!.Length);
        Assert.Equal(64, report.Identity.DiagnosticConfigurationSha256.Sha256!.Length);
        Assert.Contains("opt=", report.Identity.CompilerFlags);
        Assert.Empty(report.Executables);
    }

    sealed class DiagnosticPipelineApi(VulkanPipelineDiagnostics diagnostics, ExecutableApi executable)
        : IVulkanPipelineApi, IVulkanPipelineDiagnosticsSource
    {
        readonly FakeVulkanPipelineApi _inner = new();
        public VulkanPipelineDiagnostics Diagnostics => diagnostics;
        public ulong CreateCache(ReadOnlySpan<byte> seed) => _inner.CreateCache(seed);
        public byte[] ReadCacheData(ulong cache) => _inner.ReadCacheData(cache);
        public void DestroyCache(ulong cache) => _inner.DestroyCache(cache);
        public void DestroyPipeline(ulong pipeline) => _inner.DestroyPipeline(pipeline);
        public ulong CreateGraphicsPipeline(ulong cache, VulkanGraphicsPipelineSpec spec)
            => executable.ExpectedPipeline = _inner.CreateGraphicsPipeline(cache, spec);
        public ulong CreateComputePipeline(ulong cache, in VulkanComputePipelineSpec spec)
            => executable.ExpectedPipeline = _inner.CreateComputePipeline(cache, spec);
    }
}
