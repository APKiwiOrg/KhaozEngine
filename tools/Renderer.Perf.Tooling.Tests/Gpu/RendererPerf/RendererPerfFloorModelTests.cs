using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFloorModelTests
{
    // Catches mip/sample/read-plus-write undercounts, seconds/ns conversion and an unmatched instruction rate.
    [Fact]
    public void Matches_units_bytes_and_instruction_class()
    {
        Assert.Equal(1344, RendererPerfFloorModel.TextureBytes(4, 4, 3, 2, 4, GpuPixelFormat.R16G16B16A16Float));
        var model = Model(1344, 1344, 81000);
        var rates = new RendererPerfReferenceRates(320e9, 8.1e12, 64e9, 2.7e12, null, 4, "fp32-fma", "synthetic explicit inputs");
        var result = RendererPerfFloorModel.Evaluate(model, rates, 100);
        Assert.Equal(10, result.PeakLowerBoundNs.Value);
        Assert.Equal(42, result.CalibratedReferenceNs.Value);
        Assert.Equal(58, result.AbsoluteExcessNs.Value);
        Assert.Equal(100.0 / 42, result.Ratio.Value);
        var mismatch = RendererPerfFloorModel.Evaluate(model, rates with { InstructionClass = "divide-root" }, 100);
        Assert.Equal(RendererPerfMetricStatus.ModelIncomplete, mismatch.Status);
        Assert.Contains(mismatch.MissingEvidence, s => s.Contains("instruction", StringComparison.Ordinal));
    }

    // Catches double counting when dispatch latency and throughput can overlap.
    [Fact]
    public void Never_adds_overlapping_launch_and_throughput_terms()
    {
        var rates = new RendererPerfReferenceRates(2e9, null, 1e9, null, null, 40, "fp32-fma", "synthetic explicit inputs");
        Assert.Equal(100, RendererPerfFloorModel.Evaluate(Model(50, 50, 0), rates, 200).CalibratedReferenceNs.Value);
        Assert.Equal(140, RendererPerfFloorModel.Evaluate(Model(50, 50, 0), rates, 200,
            RendererPerfLaunchRelation.DisjointSerial).CalibratedReferenceNs.Value);
    }

    // Catches treating a partial traffic estimate or missing evidence as a precise gap.
    [Fact]
    public void Unknown_traffic_yields_model_incomplete()
    {
        var model = Model(64, 64, 0) with { MissingEvidence = ["Texture cache and alpha invocation range are unknown."] };
        var result = RendererPerfFloorModel.Evaluate(model,
            new(1e9, null, 1e9, null, null, 0, "fp32-fma", "synthetic explicit inputs"), 256);
        Assert.Equal(RendererPerfMetricStatus.ModelIncomplete, result.Status);
        Assert.Equal(128, result.PeakLowerBoundNs.Value);
        Assert.Null(result.Ratio.Value);
        Assert.Null(result.AbsoluteExcessNs.Value);
        Assert.Contains("Texture cache and alpha invocation range are unknown.", result.MissingEvidence);
    }

    // Catches hiding an invalid lower bound by clamping a negative excess to zero.
    [Fact]
    public void Observed_below_bound_flags_invalid_model()
    {
        var result = RendererPerfFloorModel.Evaluate(Model(50, 50, 0),
            new(1e9, null, 1e9, null, null, 0, "fp32-fma", "synthetic explicit inputs"), 99);
        Assert.Equal(RendererPerfMetricStatus.Invalid, result.Status);
        Assert.Null(result.AbsoluteExcessNs.Value);
        Assert.Null(result.PeakLowerBoundNs.Value);
        Assert.Equal(-1, result.UnclampedExcessNs);
        Assert.Equal(100, result.ClaimedPeakLowerBoundNs);
        Assert.Contains(result.MissingEvidence, s => s.Contains("below", StringComparison.Ordinal));
    }

    internal static RendererPassWorkModel Model(double reads, double writes, double ops) => new("synthetic", 0,
        ImmutableDictionary<string, RendererPerfWorkRange>.Empty
            .Add("readBytes", new(reads, reads, "bytes", "synthetic vector", "exact", null))
            .Add("writeBytes", new(writes, writes, "bytes", "synthetic vector", "exact", null))
            .Add("fp32-fma", new(ops, ops, "operations", "synthetic vector", "exact", null)), [], []);
}
