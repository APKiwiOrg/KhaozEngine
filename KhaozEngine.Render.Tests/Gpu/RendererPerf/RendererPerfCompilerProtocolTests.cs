using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu.Internal;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerProtocolTests
{
    readonly ITestOutputHelper _output;

    public RendererPerfCompilerProtocolTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Typed_availability_preserves_valid_companions()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var pipeline = report.Pipelines[0];
        var absent = new GpuDiagnosticDigest(null, "metal.native-artifact/1", GpuDiagnosticStatus.Unsupported, "MSL is source.");
        pipeline = pipeline with { Identity = pipeline.Identity with { ShaderArtifactSha256 = absent, Stages = [pipeline.Identity.Stages[0] with { ShaderArtifactSha256 = absent }] }, Status = GpuDiagnosticStatus.Unsupported, Reason = absent.Reason };
        report = report with { Pipelines = [pipeline] };
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.Equal(absent, restored.Pipelines[0].Identity.ShaderArtifactSha256);
        Assert.Equal(pipeline.Identity.SourceSha256, restored.Pipelines[0].Identity.SourceSha256);
        Assert.Equal(ulong.MaxValue, restored.Pipelines[0].Executables[0].Statistics[0].Metric.Value!.UInt64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Known_hashes_and_report_versions_are_strict(string hash)
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var pipeline = report.Pipelines[0];
        pipeline = pipeline with { Identity = pipeline.Identity with { SourceSha256 = pipeline.Identity.SourceSha256 with { Sha256 = hash } } };
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report with { Pipelines = [pipeline] }));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report with { Schema = "khaozengine.renderer-perf-report/1" }));
    }

    [Fact]
    public void Invalid_or_incomplete_collection_cannot_promote()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var compiler = report.Compiler with { State = RendererPerfCollectionState.Incomplete, Reason = "Capture is not connected." };
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report with { Compiler = compiler }));
        report = report with { Compiler = compiler, Assessment = report.Assessment with { ValidForPromotion = false, Reasons = [compiler.Reason] } };
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.False(restored.Assessment.ValidForPromotion);
        Assert.Equal(report.Blocks[0].Measured.Length, restored.Blocks[0].Measured.Length);
        Assert.Equal(ulong.MaxValue, restored.Pipelines[0].Executables[0].Statistics[0].Metric.Value!.UInt64);
    }

    [Fact]
    public void Failure_transport_keeps_original_aggregate_and_repeated_events()
    {
        var original = new AggregateException(new InvalidOperationException("first"));
        var first = new GpuDiagnosticFailure("read", original);
        var second = new GpuDiagnosticFailure("cleanup", original);
        var transport = new GpuDiagnosticFailureException([first, second]);
        Assert.Same(first, transport.Failures[0]);
        Assert.Same(second, transport.Failures[1]);
        Assert.Same(original, transport.Failures[0].Error);
        Assert.Throws<ArgumentException>(() => new GpuDiagnosticFailureException([]));
    }
}
