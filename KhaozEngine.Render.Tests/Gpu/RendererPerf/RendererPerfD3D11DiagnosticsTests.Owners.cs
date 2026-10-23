using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfD3D11DiagnosticsTests
{
    [Fact]
    public void Graphics_owner_retains_each_stage_and_snapshots_all_pipeline_identity()
    {
        var api = new ReflectionApi();
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Immediate, true, false);
        var vertex = D3D11ShaderEvidence.Capture("vertex source", new byte[] { 1, 2, 3 }, "vertex", 0, options, api);
        var pixel = D3D11ShaderEvidence.Capture("pixel source", new byte[] { 1, 2, 3 }, "pixel", 0, options, api);
        var live = new DeviceLiveness();
        var vertexHandle = new NativeHandle();
        using var shaders = new D3D11ShaderSet(live, vertexHandle, new NativeHandle(), new byte[] { 1, 2, 3 }, vertex, pixel);
        using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Input", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
        var d = new GpuPipelineDescription
        {
            ShaderSet = shaders,
            ResourceLayouts = [layout],
            VertexLayouts = [],
            Topology = GpuPrimitiveTopology.TriangleList,
            Outputs = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm),
            BlendAttachments = [GpuBlendAttachment.OverrideBlend]
        };
        var native = new GraphicsApi();
        using var first = new D3D11GraphicsPipeline(live, d, native);
        var report = first.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Available, report.Status);
        Assert.Equal(new[] { "vertex", "pixel" }, report.Executables.Select(x => x.Stage));
        Assert.Equal(64, report.Identity.PipelineSha256.Sha256!.Length);
        Assert.Equal(64, report.Identity.LayoutSha256.Sha256!.Length);
        Assert.Same(vertexHandle, ((ID3D11PipelineState)first).VertexShader);
        using var repeat = new D3D11GraphicsPipeline(live, d, native);
        Assert.Equal(report.Identity.PipelineSha256.Sha256, repeat.ReadCompilerReport().Identity.PipelineSha256.Sha256);
        d.Topology = GpuPrimitiveTopology.LineList;
        using var changed = new D3D11GraphicsPipeline(live, d, native);
        Assert.NotEqual(report.Identity.PipelineSha256.Sha256, changed.ReadCompilerReport().Identity.PipelineSha256.Sha256);
        d.Outputs.Colour[0] = GpuPixelFormat.R16G16B16A16Float;
        Assert.Same(report, first.ReadCompilerReport());
        using var output = new D3D11GraphicsPipeline(live, d, native);
        Assert.NotEqual(changed.ReadCompilerReport().Identity.PipelineSha256.Sha256, output.ReadCompilerReport().Identity.PipelineSha256.Sha256);
        Assert.Equal(2, api.Reads);
        first.Dispose();
        first.Dispose();
        Assert.Equal(1, native.FirstBlend!.Disposals);
        Assert.Equal(0, vertexHandle.Disposals);
        Assert.Equal(GpuDiagnosticStatus.Invalid, first.ReadCompilerReport().Status);
        shaders.Dispose();
        Assert.Equal(GpuDiagnosticStatus.Invalid, repeat.ReadCompilerReport().Status);
    }

    [Fact]
    public void Reflection_failures_empty_data_and_disabled_collection_keep_their_causes()
    {
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false);
        var api = new ReflectionApi { Failure = new InvalidOperationException("native reflection failure") };
        var failed = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0, options, api);
        Assert.Equal(GpuDiagnosticStatus.Invalid, failed.Status);
        Assert.Same(api.Failure, Assert.Single(failed.Failures).Error);
        api.Failure = null;
        api.Empty = true;
        var empty = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0, options, api);
        Assert.Equal(GpuDiagnosticStatus.Unsupported, empty.Status);
        Assert.Contains("no shader statistics", empty.Reason);
        var disabled = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0,
            options with { Reflection = false }, api);
        Assert.Equal(GpuDiagnosticStatus.Unsupported, disabled.Status);
        Assert.Equal(2, api.Reads);
        var invalid = D3D11ShaderEvidence.Capture("source", [], "compute", 0, options, api);
        Assert.Equal(GpuDiagnosticStatus.Invalid, invalid.Status);
        Assert.Equal(2, api.Reads);
        Assert.NotEqual(failed.Configuration, disabled.Configuration);
        api.Failure = new NotSupportedException("missing reflection entry point");
        var unavailable = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0, options, api);
        Assert.Equal(GpuDiagnosticStatus.Invalid, unavailable.Status);
        Assert.Same(api.Failure, Assert.Single(unavailable.Failures).Error);
    }

    [Fact]
    public void Resource_owners_count_complete_lifetimes_and_ignore_failed_creation()
    {
        var memory = new D3D11MemoryDiagnostics();
        var live = new DeviceLiveness();
        using var harness = new D3D11RingHarness(48, 3);
        var bufferHandle = new NativeHandle();
        using var buffer = new D3D11Buffer(live, harness.Allocator, new GpuBufferDescription(48, GpuBufferUsage.UniformBuffer),
            _ => new(bufferHandle, RingMemory: harness.Memory), memory);
        var cubeHandle = new NativeHandle();
        using var cube = new D3D11Texture(live,
            new GpuTextureDescription(4, 2, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Cubemap, mipLevels: 3, arrayLayers: 2),
            _ => new(cubeHandle), memory);
        using var msaa = new D3D11Texture(live,
            new GpuTextureDescription(2, 2, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil, sampleCount: 4),
            _ => new(new NativeHandle()), memory);
        // Cube is (8 + 2 + 1) texels * 4 bytes * 12 faces. MSAA is 4 texels * 8 bytes * 4 samples.
        var snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(656ul, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(704ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(768ul, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(3ul, snapshot.LiveResourceCount.Value!.UInt64);
        Assert.Throws<InvalidOperationException>(() => new D3D11Texture(live,
            new GpuTextureDescription(1, 1, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled),
            _ => throw new InvalidOperationException("native creation failed"), memory));
        Assert.Equal(snapshot, memory.ReadMemorySnapshot());
        cube.Dispose(); cube.Dispose();
        Assert.Equal(1, cubeHandle.Disposals);
        Assert.Equal(128ul, memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        buffer.Dispose();
        Assert.Equal(1, bufferHandle.Disposals);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().StagingCapacityBytes.Value!.UInt64);
        live.MarkDead();
        msaa.Dispose();
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(GpuDiagnosticStatus.Invalid, memory.Snapshot(true).LogicalBufferBytes.Status);
    }

    [Fact]
    public void Invalid_payload_releases_native_resource_without_partial_accounting()
    {
        var memory = new D3D11MemoryDiagnostics();
        var handle = new NativeHandle();
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D11Texture(new DeviceLiveness(),
            new GpuTextureDescription(1, 1, (GpuPixelFormat)int.MaxValue, GpuTextureUsage.Sampled), _ => new(handle), memory));
        Assert.Equal(1, handle.Disposals);
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Throws<OverflowException>(() => memory.TrackTexture(new GpuTextureDescription(uint.MaxValue, uint.MaxValue,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled)));
        Assert.Equal(0ul, memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
    }

    [Theory]
    [InlineData(GpuPixelFormat.R8UNorm, 14ul)]
    [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 56ul)]
    [InlineData(GpuPixelFormat.B8G8R8A8UNorm, 56ul)]
    [InlineData(GpuPixelFormat.R32Float, 56ul)]
    [InlineData(GpuPixelFormat.D24UNormS8UInt, 56ul)]
    [InlineData(GpuPixelFormat.R16G16Float, 56ul)]
    [InlineData(GpuPixelFormat.R16G16B16A16Float, 112ul)]
    [InlineData(GpuPixelFormat.D32FloatS8UInt, 112ul)]
    public void Staging_capacity_uses_each_DXGI_format_and_mip_layer_shape(GpuPixelFormat format, ulong bytes)
    {
        var memory = new D3D11MemoryDiagnostics();
        using var texture = new D3D11Texture(new DeviceLiveness(), new GpuTextureDescription(3, 2, format,
            GpuTextureUsage.Staging, mipLevels: 2, arrayLayers: 2), _ => new(new NativeHandle()), memory);
        var snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(bytes, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(bytes, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Contains("excludes texture row padding", snapshot.StagingCapacityBytes.Source);
        Assert.Contains("overlaps", snapshot.StagingCapacityBytes.Source);
    }

    [Fact]
    public void Mixed_stage_failures_preserve_the_original_native_cause()
    {
        var api = new ReflectionApi();
        var options = new D3D11DiagnosticOptions(false, D3D11RecordMode.Deferred, false, false);
        var vertex = D3D11ShaderEvidence.Capture("vertex", new byte[] { 1, 2, 3 }, "vertex", 0, options, api);
        api.Failure = new InvalidOperationException("pixel reflection failed natively");
        var pixel = D3D11ShaderEvidence.Capture("pixel", new byte[] { 1, 2, 3 }, "pixel", 0, options with { Reflection = true }, api);
        var live = new DeviceLiveness();
        using var shaders = new D3D11ShaderSet(live, new NativeHandle(), new NativeHandle(), new byte[] { 1, 2, 3 }, vertex, pixel);
        using var pipeline = new D3D11GraphicsPipeline(live, new GpuPipelineDescription { ShaderSet = shaders }, new GraphicsApi());
        var report = pipeline.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Status);
        Assert.Contains("not requested", report.Reason);
        var result = pipeline.ReadCompilerReport(D3D11PipelineDiagnostics.Request, _ => { }, default);
        Assert.Same(api.Failure, result.Failures[0].Error);
        Assert.Contains("not captured", result.Failures[1].Error.Message);
        Assert.Equal("pixel reflection failed natively", result.Failures[0].Error.Message);
    }

    [Fact]
    public void Missing_metadata_has_no_fabricated_artifact_or_pipeline_identity()
    {
        using var shader = new D3D11ComputeShader(new DeviceLiveness(), new NativeHandle(), new byte[] { 1, 2, 3 }, 8, 4, 2);
        using var pipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(shader));
        var report = pipeline.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Unsupported, report.Status);
        Assert.Null(report.Identity.PipelineSha256.Sha256);
        Assert.Null(report.Identity.SourceSha256.Sha256);
        Assert.Null(report.Identity.ShaderArtifactSha256.Sha256);
        Assert.Empty(report.Executables);
    }

    sealed class GraphicsApi : ID3D11GraphicsPipelineApi
    {
        internal NativeHandle? FirstBlend;
        public D3D11GraphicsPipelineObjects Create(in GpuPipelineDescription description, ID3D11ShaderSet shaders,
            D3D11InputElement[] elements)
        {
            var blend = new NativeHandle();
            FirstBlend ??= blend;
            var set = (D3D11ShaderSet)shaders;
            return new(blend, new NativeHandle(), new NativeHandle(), null, 4, set.VertexHandle, set.PixelHandle);
        }
    }
}
