using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerProtocolTests
{
    [Theory]
    [InlineData(0, 786432)]
    [InlineData(1, 790528)]
    [InlineData(12448, 51773440)]
    [InlineData(16384, 67895296)]
    public void Conditional_read_reservations_cover_maximal_escaping(int total, int maximum)
    {
        var reservation = RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.VulkanNative, maximum)!;
        Assert.Equal(total, reservation.Request.MaximumTotalStatistics);
        Assert.Equal(maximum, reservation.MaximumEncodedBytes);
        var report = RendererPerfProtocolTests.CompleteReport();
        var prototype = report.Pipelines[0];
        string escaped = new('\x01', 256);
        var metrics = new[] { new GpuDiagnosticValue(GpuDiagnosticValueType.Boolean, Boolean: true),
            new(GpuDiagnosticValueType.Int64, Int64: long.MinValue), new(GpuDiagnosticValueType.UInt64, UInt64: ulong.MaxValue),
            new(GpuDiagnosticValueType.Float64, Float64: double.MinValue) };
        var rows = Enumerable.Range(0, 64).Select(i => prototype.Executables[0] with
        {
            Name = escaped,
            Statistics = Enumerable.Range(0, Math.Clamp(total - i * 256, 0, 256)).Select(j =>
                new GpuPipelineStatistic(escaped, escaped, new(metrics[j % 4], "vendor-defined", "vkGetPipelineExecutableStatisticsKHR",
                    GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.VendorCompiler))).ToImmutableArray()
        }).ToImmutableArray();
        var pipeline = prototype with
        {
            Identity = prototype.Identity with { CompilerFlags = new('\x01', 16384) },
            Executables = rows,
            Status = total == 0 ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available,
            Reason = total == 0 ? "The driver returned no executable statistics." : null
        };
        var counts = new GpuPipelineCompilerReadCounts(true, 64, (ulong)total, 64, total, 0, 0, true, null, null);
        var result = new GpuPipelineCompilerReadResult(reservation.Request, pipeline, true, counts, null, []);
        int independent = JsonSerializer.SerializeToUtf8Bytes(pipeline, RendererPerfManifest.JsonOptions).Length
            + JsonSerializer.SerializeToUtf8Bytes(new RendererPerfCompilerReadEvidence(result.Request, counts, true, null), RendererPerfManifest.JsonOptions).Length;
        int charged = RendererPerfCompilerReadContract.EncodedResultBytes(result);
        Assert.Equal(independent + 256, charged);
        Assert.InRange(charged, 1, maximum);
        var baseline = report with
        {
            Pipelines = [],
            Compiler = RendererPerfCompilerEvidence.Pending,
            Assessment = report.Assessment with { ValidForPromotion = false, Reasons = ["Synthetic size contract."] }
        };
        var filled = baseline with
        {
            Pipelines = [pipeline],
            Compiler = baseline.Compiler with
            {
                Results = [new(0, RendererPerfCollectionState.Complete, null, 0, [], new(result.Request, counts, true, null))]
            }
        };
        int rootIncrement = RendererPerfRawJson.Serialize(filled, RendererPerfReportJson.MaximumBytes).Length
            - RendererPerfRawJson.Serialize(baseline, RendererPerfReportJson.MaximumBytes).Length;
        Assert.InRange(rootIncrement, 1, charged);
        var refused = result with
        {
            Complete = false,
            RefusalReason = escaped,
            Report = pipeline with { Status = GpuDiagnosticStatus.Invalid, Reason = escaped },
            Counts = counts with { RefusedExecutableIndex = 63, RefusedCount = uint.MaxValue },
            Failures = [new("read", new InvalidOperationException(new string('x', 1000000)))]
        };
        int refusalBytes = RendererPerfCompilerReadContract.EncodedResultBytes(refused);
        Assert.InRange(refusalBytes, 1, maximum);
        _output.WriteLine(JsonSerializer.Serialize(new { Backend = "vulkan", total, maximum, independent, charged, rootIncrement, refusalBytes }));
    }

    [Fact]
    public void Conditional_profiles_refuse_before_the_header_and_preserve_the_combined_cap()
    {
        Assert.Null(RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.VulkanNative, 786431));
        Assert.Null(RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.Direct3D11Native, 1048575));
        Assert.Equal(1048576, RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.Direct3D11Native, 1048576)!.MaximumEncodedBytes);
        var historical = RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.VulkanNative, 268435456L - 199883475 - 16777216)!;
        Assert.Equal(12448, historical.Request.MaximumTotalStatistics);
        Assert.Equal(51773440, historical.MaximumEncodedBytes);
        Assert.Throws<ArgumentException>(() => RendererPerfCompilerReadContract.ForAvailable(GpuBackendKind.MetalNative, long.MaxValue));
    }
}
