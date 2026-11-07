using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfD3D11DiagnosticsTests
{
    [Fact]
    public void Deferred_graphics_evidence_does_not_realize_native_shader_or_pipeline_objects()
    {
        var live = new DeviceLiveness();
        var native = new CompilerApi();
        var reflection = new ReflectionApi();
        var compiler = new D3D11ShaderCompiler(live, native, null, 0x8000,
            new(true, D3D11RecordMode.Deferred, false, false), reflection);
        using var shaders = (D3D11ShaderSet)compiler.CreateShaderSet("vertex", "pixel");
        var graphics = new GraphicsApi();
        using var pipeline = new D3D11GraphicsPipeline(live,
            new GpuPipelineDescription { ShaderSet = shaders }, graphics);
        Assert.Empty(native.Handles);
        Assert.Null(graphics.FirstBlend);
        var report = pipeline.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Available, report.Status);
        Assert.Equal(2, report.Executables.Length);
        Assert.Same(report.Identity, pipeline.ConstructionIdentity);
        Assert.Equal(2, reflection.Reads);
        Assert.Empty(native.Handles);
        Assert.Null(graphics.FirstBlend);
        var bound = ((ID3D11PipelineState)pipeline).VertexShader;
        Assert.Same(native.Handles[0], bound);
        Assert.Equal(2, native.Handles.Count);
        Assert.NotNull(graphics.FirstBlend);
        Assert.Equal(2, reflection.Reads);
        pipeline.Dispose();
        shaders.Dispose();
        Assert.Same(report.Identity, pipeline.ConstructionIdentity);
        Assert.Equal(GpuDiagnosticStatus.Invalid, pipeline.ReadCompilerReport().Status);
    }

    [Fact]
    public void Deferred_compile_failure_keeps_its_original_exception_at_binding_and_observation()
    {
        var failure = new ShaderValidationException("synthetic original compile failure");
        var preparation = D3D11ShaderCompilation<D3D11PreparedGraphics>.Start(() => throw failure);
        var live = new DeviceLiveness();
        using var shaders = new D3D11ShaderSet(live, preparation,
            _ => throw new InvalidOperationException("Native creation must not follow a failed compile."));
        var native = new GraphicsApi();
        using var pipeline = new D3D11GraphicsPipeline(live,
            new GpuPipelineDescription { ShaderSet = shaders }, native);
        Assert.Same(failure, Assert.Throws<ShaderValidationException>(
            () => ((ID3D11PipelineState)pipeline).VertexShader));
        Assert.Same(failure, Assert.Throws<ShaderValidationException>(() => pipeline.ConstructionIdentity));
        Assert.Null(native.FirstBlend);
    }

    [Fact]
    public void Default_output_identity_remains_distinct_from_an_explicit_single_sample_description()
    {
        var live = new DeviceLiveness();
        var compiler = new D3D11ShaderCompiler(live, new CompilerApi(), null, 0x8000,
            new(true, D3D11RecordMode.Deferred, false, false), new ReflectionApi());
        using var shaders = (D3D11ShaderSet)compiler.CreateShaderSet("vertex", "pixel");
        using var unspecified = new D3D11GraphicsPipeline(live,
            new GpuPipelineDescription { ShaderSet = shaders }, new GraphicsApi());
        using var single = new D3D11GraphicsPipeline(live, new GpuPipelineDescription
        {
            ShaderSet = shaders,
            Outputs = new GpuOutputDescription(null)
        }, new GraphicsApi());
        Assert.NotEqual(unspecified.ConstructionIdentity.PipelineSha256,
            single.ConstructionIdentity.PipelineSha256);
    }

    [Fact]
    public async Task Pending_graphics_evidence_snapshots_inputs_before_compilation_completes()
    {
        using var release = new ManualResetEventSlim();
        var live = new DeviceLiveness();
        var reflection = new ReflectionApi();
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false);
        var preparation = D3D11ShaderCompilation<D3D11PreparedGraphics>.Start(() =>
        {
            release.Wait();
            return new(new([1, 2, 3], [1, 2, 3]),
                D3D11ShaderEvidence.Capture("vertex", new byte[] { 1, 2, 3 }, "vertex", 0, options, reflection),
                D3D11ShaderEvidence.Capture("pixel", new byte[] { 1, 2, 3 }, "pixel", 0, options, reflection));
        });
        using var shaders = new D3D11ShaderSet(live, preparation,
            _ => new(new NativeHandle(), new NativeHandle()));
        var description = new GpuPipelineDescription
        {
            ShaderSet = shaders,
            VertexLayouts = [],
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm)
        };
        var native = new GraphicsApi();
        var construction = Task.Run(() => new D3D11GraphicsPipeline(live, description, native));
        try
        {
            var pending = await construction.WaitAsync(TimeSpan.FromSeconds(20));
            description.Outputs.Colour[0] = GpuPixelFormat.R16G16B16A16Float;
            description.BlendAttachments[0] = GpuBlendAttachment.AlphaBlend;
            release.Set();
            using var baseline = new D3D11GraphicsPipeline(live, new GpuPipelineDescription
            {
                ShaderSet = shaders,
                VertexLayouts = [],
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm)
            }, new GraphicsApi());
            Assert.Equal(baseline.ConstructionIdentity.PipelineSha256, pending.ConstructionIdentity.PipelineSha256);
            Assert.Null(native.FirstBlend);
        }
        finally
        {
            release.Set();
            (await construction).Dispose();
            _ = preparation.Join();
        }
    }
}
