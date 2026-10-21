using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePipelineStatisticsTests
{
    [Fact]
    public void MergedDiagnosticIdentityRetainsTheCoveragePrecisionPolicy()
    {
        var ordinary = VulkanShaderIdentity.Compute("source", [1, 2, 3], "compute shader");
        var precise = VulkanShaderIdentity.Compute("source", [1, 2, 3], "compute shader", new(PreciseMath: true));
        Assert.NotEqual(ordinary.CompilerFlags, precise.CompilerFlags);
        Assert.Equal(ordinary.Artifact, precise.Artifact);
    }

    [Fact]
    public void StatisticsPresetHasNoFramesAndCannotEstablishPerformanceProof()
    {
        var run = new CoveragePerfFakeRun("S1");
        var manifest = CoveragePerfManifest.Parse(run.Json);
        Assert.Empty(manifest.Blocks);
        Assert.Equal((600, 30, "gpu-t4-linux", "linux-vulkan"),
            (manifest.MaxUnitSeconds, manifest.JobMinutes, manifest.Runner, manifest.Leg));
        var report = run.Run();
        Assert.Equal(CoveragePerfRunStatus.Completed, report.Status);
        Assert.Empty(run.Sessions);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.CpuStatus);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.GpuStatus);
    }

    [Fact]
    public void PlanUsesTheProductionGroupCountsAndQueriesOverlayFirst()
    {
        var plan = CoveragePipelineStatisticsCapture.Plan();
        Assert.Equal(36, plan.Kernels.Length);
        Assert.Equal("CorrectFacesOverlay", plan.Kernels[0].Phase);
        Assert.Equal(Enum.GetNames<CoverageGpuPhase>().Order(), plan.Kernels.Select(k => k.Phase).Order());
        Assert.All(plan.Kernels, k => Assert.Equal(new CoverageDispatchGroups(64, 1, 1), k.LocalSize));
        Assert.All(plan.Kernels, k => Assert.StartsWith("#version 450\nlayout(local_size_x = 64) in;\n",
            CoverageGpuSkinRefineGlsl.Source(Enum.Parse<CoverageGpuPhase>(k.Phase)), StringComparison.Ordinal));
        Assert.All(plan.Kernels, k => Assert.Equal((32u, 1u), (k.Groups.Y, k.Groups.Z)));
        Assert.Equal(37u, plan.Kernels.Single(k => k.Phase == "CorrectFacesOverlay").Groups.X);
        Assert.Equal(11, plan.Kernels.Count(k => CoverageGpuSkinRefineGlsl.IsReducer(Enum.Parse<CoverageGpuPhase>(k.Phase))));
        Assert.All(plan.Kernels.Where(k => CoverageGpuSkinRefineGlsl.IsReducer(Enum.Parse<CoverageGpuPhase>(k.Phase))),
            k => Assert.Equal(1u, k.Groups.X));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdCaptureFailureOrDeadlineCannotPublishSuccessfulEvidence(bool timeout)
    {
        var run = new CoveragePerfFakeRun("S1");
        var report = CoveragePerfDriver.Run(run.Json, run.Identity,
            (_, _, _) => throw new InvalidOperationException("No frame session is allowed"), run.Time.Clock(), _ => { }, _ =>
            {
                if (timeout) run.Time.Advance(600000);
                else throw new IOException("Cold capture failed");
            });
        Assert.Equal(CoveragePerfRunStatus.Failed, report.Status);
        Assert.False(report.FinalUnitPublished);
        Assert.Empty(report.Blocks);
    }

    [Fact]
    public void JsonRetainsEveryRawValueWithoutInventingPhysicalStatistics()
    {
        var metric = new GpuDiagnosticMetric(new(GpuDiagnosticValueType.UInt64, UInt64: ulong.MaxValue),
            "vendor-defined", "driver", GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.VendorCompiler);
        var missing = new GpuDiagnosticMetric(null, "bytes", "driver", GpuDiagnosticStatus.Unsupported,
            "No documented mapping", GpuDiagnosticProvenance.Unknown);
        var executable = new GpuPipelineExecutableReport("compute", "ComputeBit", "main", 64, 1, 1,
            [new("raw statistic", "unaltered driver description", metric)], missing, missing, missing, missing, missing);
        var compiler = new GpuPipelineCompilerReport("pipeline", "source", "artifact", "layout", "configuration", "precise",
            GpuDiagnosticStatus.Available, null, [executable]);
        using var document = JsonDocument.Parse(CoveragePipelineStatisticsCapture.Serialize(compiler));
        var value = document.RootElement.GetProperty("executables")[0].GetProperty("statistics")[0];
        Assert.Equal("raw statistic", value.GetProperty("name").GetString());
        Assert.Equal(ulong.MaxValue, value.GetProperty("metric").GetProperty("value").GetProperty("uInt64").GetUInt64());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("executables")[0]
            .GetProperty("spillBytes").GetProperty("value").ValueKind);
    }
}

[Collection("NativeDeviceLifecycle")]
public sealed class CoveragePipelineStatisticsGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void ColdCaptureReportsAvailableOrUnsupportedWithoutTimingFrames()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("KE_VULKAN_PIPELINE_DIAGNOSTICS"));
        using var context = GpuDeviceContext.CreateHeadless();
        var capture = CoveragePipelineStatisticsCapture.Capture(context.GpuDevice);
        string directory = Environment.GetEnvironmentVariable("KE_COVERAGE_PIPELINE_STATS_RESULTS")
            ?? throw new InvalidDataException("A scratch output directory is required.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "coverage-pipeline-statistics.json"), CoveragePipelineStatisticsCapture.Serialize(capture));
        output.WriteLine($"{capture.Status}: {capture.Reason}, adapter={capture.Device.Adapter}, kernels={capture.Kernels.Length}");
        Assert.NotEqual(GpuDiagnosticStatus.Invalid, capture.Status);
        if (capture.Status == GpuDiagnosticStatus.Unsupported)
        {
            Assert.False(string.IsNullOrWhiteSpace(capture.Reason));
            Assert.True(capture.Kernels.Length is 0 or 36);
        }
        else Assert.Equal(36, capture.Kernels.Length);
    }
}
