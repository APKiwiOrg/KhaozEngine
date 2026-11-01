using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfD3D11DiagnosticsTests
{
    [Fact]
    public void Compiler_binds_actual_artifacts_and_releases_a_failed_pair()
    {
        var native = new CompilerApi();
        var reflection = new ReflectionApi();
        var compiler = new D3D11ShaderCompiler(new DeviceLiveness(), native, null, 0x8000,
            new(true, D3D11RecordMode.Deferred, false, false), reflection);
        using var shader = compiler.CreateComputeShader("exact compute source");
        Assert.Equal("exact compute source", native.LastCompute);
        using var pipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(shader));
        var report = pipeline.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Available, report.Status);
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", report.Identity.ShaderArtifactSha256.Sha256);
        Assert.Equal(8u, Assert.Single(report.Executables).LocalSizeX);
        Assert.Contains("cs_5_0", report.Identity.CompilerFlags);
        using var shaders = compiler.CreateShaderSet("exact vertex source", "exact pixel source");
        Assert.Equal("exact vertex source", native.LastVertex);
        Assert.Equal("exact pixel source", native.LastPixel);
        Assert.Equal(3, reflection.Reads);
        Assert.Equal(3, native.Handles.Count);
        native.FailPixel = true;
        Assert.Throws<InvalidOperationException>(() => compiler.CreateShaderSet("vertex", "pixel"));
        Assert.Equal(4, native.Handles.Count);
        Assert.Equal(1, native.Handles[3].Disposals);
        Assert.Equal(0, native.Handles[0].Disposals);
    }

    [Fact]
    public void Unavailable_reflection_does_not_revoke_a_created_compute_shader()
    {
        var native = new CompilerApi();
        var compiler = new D3D11ShaderCompiler(new DeviceLiveness(), native, null, 0x8000,
            new(true, D3D11RecordMode.Deferred, false, false),
            new ReflectionApi { Failure = new NotSupportedException("reflection unavailable") });
        using var shader = compiler.CreateComputeShader("compute source");
        using var pipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(shader));
        Assert.Same(native.Handles[0], ((ID3D11ComputePipelineState)pipeline).ComputeShader);
        Assert.Equal(GpuDiagnosticStatus.Invalid, pipeline.ReadCompilerReport().Status);
        var result = pipeline.ReadCompilerReport(D3D11PipelineDiagnostics.Request, _ => { }, default);
        Assert.IsType<NotSupportedException>(Assert.Single(result.Failures).Error);
        Assert.Equal("reflection unavailable", result.Failures[0].Error.Message);
        Assert.Equal(0, native.Handles[0].Disposals);
    }

    [Fact]
    public void Diagnostic_configuration_preserves_recording_validation_and_capture_choices()
    {
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false);
        var api = new ReflectionApi();
        var evidence = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0x8000, options, api);
        using var shader = new D3D11ComputeShader(new DeviceLiveness(), new NativeHandle(), new byte[] { 1, 2, 3 }, 8, 4, 2, evidence);
        using var pipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(shader));
        var baseline = pipeline.ReadCompilerReport();
        foreach (var changed in new[] { options with { RecordMode = D3D11RecordMode.Immediate },
            options with { DebugLayer = true }, options with { Invocations = true } })
        {
            var alternate = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0x8000, changed, api);
            using var second = new D3D11ComputeShader(new DeviceLiveness(), new NativeHandle(), new byte[] { 1, 2, 3 }, 8, 4, 2, alternate);
            using var secondPipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(second));
            var report = secondPipeline.ReadCompilerReport();
            Assert.NotEqual(baseline.Identity.DiagnosticConfigurationSha256.Sha256, report.Identity.DiagnosticConfigurationSha256.Sha256);
            Assert.Equal(baseline.Identity.PipelineSha256.Sha256, report.Identity.PipelineSha256.Sha256);
            Assert.Equal(baseline.Identity.ShaderArtifactSha256.Sha256, report.Identity.ShaderArtifactSha256.Sha256);
        }
        using var localShader = new D3D11ComputeShader(new DeviceLiveness(), new NativeHandle(), new byte[] { 1, 2, 3 }, 16, 4, 2, evidence);
        using var localPipeline = new D3D11ComputePipeline(new GpuComputePipelineDescription(localShader));
        Assert.NotEqual(baseline.Identity.PipelineSha256.Sha256, localPipeline.ReadCompilerReport().Identity.PipelineSha256.Sha256);
    }

    [Fact]
    public void Budgeted_reader_keeps_original_aggregate_and_repeated_native_occurrences()
    {
        var original = new AggregateException(new InvalidOperationException("native aggregate"));
        var reflection = new ReflectionApi { Failure = original };
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false);
        var vertex = D3D11ShaderEvidence.Capture("vertex", new byte[] { 1, 2, 3 }, "vertex", 0, options, reflection);
        var pixel = D3D11ShaderEvidence.Capture("pixel", new byte[] { 1, 2, 3 }, "pixel", 0, options, reflection);
        var owner = new D3D11PipelineDiagnostics([vertex, pixel], new('a', 64), new('b', 64));
        var result = owner.Read(false, D3D11PipelineDiagnostics.Request, _ => { }, default);
        Assert.False(result.Complete);
        Assert.Equal(2, result.Failures.Length);
        Assert.Same(original, result.Failures[0].Error);
        Assert.Same(original, result.Failures[1].Error);
        Assert.NotSame(result.Failures[0], result.Failures[1]);
        var transport = new GpuDiagnosticFailureException(result.Failures);
        reflection.Failure = transport;
        var carried = D3D11ShaderEvidence.Capture("source", new byte[] { 1, 2, 3 }, "compute", 0, options, reflection);
        Assert.Same(result.Failures[0], carried.Failures[0]);
        Assert.Same(result.Failures[1], carried.Failures[1]);
    }

    sealed class CompilerApi : ID3D11ShaderCompilerApi
    {
        internal string? LastCompute, LastVertex, LastPixel;
        internal bool FailPixel;
        internal readonly List<NativeHandle> Handles = [];
        public D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache)
        {
            LastVertex = vertex; LastPixel = pixel;
            Assert.Equal(0x8000u, flags);
            return new([1, 2, 3], [1, 2, 3]);
        }
        public D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache)
        {
            LastCompute = source;
            Assert.Equal(0x8000u, flags);
            return new([1, 2, 3], 8, 4, 2);
        }
        public object CreateShader(byte[] dxbc, D3D11ShaderStage stage)
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, dxbc);
            if (FailPixel && stage == D3D11ShaderStage.Fragment) throw new InvalidOperationException("native pixel creation failed");
            var handle = new NativeHandle();
            Handles.Add(handle);
            return handle;
        }
    }
}
