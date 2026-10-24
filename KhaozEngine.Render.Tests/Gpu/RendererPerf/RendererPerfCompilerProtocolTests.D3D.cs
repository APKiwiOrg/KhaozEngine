using System;
using System.Collections.Immutable;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerProtocolTests
{
    [Fact]
    public void D3D_fixed_two_by_thirty_projection_fits_its_maximal_escaping_reservation()
    {
        var native = new ThirtyCounts();
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false);
        var vertex = D3D11ShaderEvidence.Capture("vertex", new byte[] { 1, 2, 3 }, "vertex", 0x8000, options, native);
        var pixel = D3D11ShaderEvidence.Capture("pixel", new byte[] { 1, 2, 4 }, "pixel", 0x8000, options, native);
        string layout = D3D11PipelineIdentity.Layout([]);
        Assert.Equal("df3f619804a92fdb4057192dc43dd748ea778adc52bc498ce80524c014b81119", layout);
        var owner = new D3D11PipelineDiagnostics([vertex, pixel], new('a', 64), layout);
        var reservation = RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.Direct3D11Native, 1048576)!;
        var result = owner.Read(false, reservation.Request, _ => { }, default);
        Assert.True(result.Complete);
        Assert.Equal(60, result.Counts.RetainedStatistics);
        Assert.All(result.Report.Executables, e => Assert.Equal(30, e.Statistics.Length));
        Assert.Equal((ulong)uint.MaxValue, result.Report.Executables[0].Statistics[0].Metric.Value!.UInt64);
        result = result with { Report = result.Report with { Identity = result.Report.Identity with { CompilerFlags = new('\x01', 16384) } } };
        int independent = JsonSerializer.SerializeToUtf8Bytes(result.Report, RendererPerfManifest.JsonOptions).Length
            + JsonSerializer.SerializeToUtf8Bytes(new RendererPerfCompilerReadEvidence(result.Request, result.Counts, result.Complete, result.RefusalReason), RendererPerfManifest.JsonOptions).Length;
        int charged = RendererPerfCompilerReadContract.EncodedResultBytes(result);
        Assert.Equal(independent + 256, charged);
        Assert.InRange(charged, 1, 524288);
        var root = RendererPerfProtocolTests.CompleteReport() with { Pipelines = [], Compiler = RendererPerfCompilerEvidence.Pending };
        var filled = root with
        {
            Pipelines = [result.Report],
            Compiler = root.Compiler with
            {
                Results = [new(0, RendererPerfCollectionState.Complete, null, 0, [], new(result.Request, result.Counts, true, null))]
            }
        };
        int rootIncrement = RendererPerfRawJson.Serialize(filled, RendererPerfReportJson.MaximumBytes).Length
            - RendererPerfRawJson.Serialize(root, RendererPerfReportJson.MaximumBytes).Length;
        Assert.InRange(rootIncrement, 1, charged);
        _output.WriteLine(JsonSerializer.Serialize(new { Backend = "d3d11", independent, charged, rootIncrement, Maximum = reservation.MaximumEncodedBytes }));
    }

    [Fact]
    public void Combined_flags_refuse_before_retention_and_keep_actual_construction_error()
    {
        var options = new D3D11DiagnosticOptions(true, D3D11RecordMode.Deferred, false, false);
        var native = new ThirtyCounts { Compiler = new('x', 8000) };
        var vertex = D3D11ShaderEvidence.Capture("vertex", new byte[] { 1, 2, 3 }, "vertex", 0x8000, options, native);
        var pixel = D3D11ShaderEvidence.Capture("pixel", new byte[] { 1, 2, 3 }, "pixel", 0x8000, options, native);
        Assert.Equal(GpuDiagnosticStatus.Available, vertex.Status);
        Assert.Equal(GpuDiagnosticStatus.Available, pixel.Status);
        var failure = Assert.Throws<GpuDiagnosticFailureException>(() => new D3D11PipelineDiagnostics([vertex, pixel], new('a', 64), new('b', 64)));
        Assert.Contains("16384", Assert.Single(failure.Failures).Error.Message);
    }

    sealed class ThirtyCounts : ID3D11ShaderReflectionApi
    {
        internal string Compiler = "Creator=synthetic reflection,compiler=synthetic";
        public D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes)
        {
            Assert.Equal(4096, maximumCreatorUtf8Bytes);
            var d = new D3D11ReflectionDescription { ConstantBuffers = uint.MaxValue };
            return new(Compiler, D3D11ReflectionStatistics.Read(d));
        }
    }
}
