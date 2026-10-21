using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfRecordingModeTests
{
    [Theory]
    [InlineData("immediate", "immediate")]
    [InlineData("deferred", "deferred")]
    [InlineData("stream", "deferred")]
    [InlineData(null, "deferred")]
    public void Native_session_identity_reads_the_selected_device_mode(string? request, string expected)
    {
        using var host = CoveragePerfSceneProbe.OpenD3D(request);
        var info = host.Session.ReadInfo();
        Assert.Equal(GpuBackendKind.Direct3D11Native, info.Backend);
        Assert.Equal(expected, info.D3DRecordingMode);
        if (!OperatingSystem.IsWindows()) D3D11InteropLoad.AssertNotLoaded();
    }

    [Theory]
    [InlineData("immediate")]
    [InlineData("deferred")]
    public void Complete_G2_vectors_pass_validity_with_the_native_session_identity(string request)
    {
        using var host = CoveragePerfSceneProbe.OpenD3D(request);
        host.ReportedCapabilities = new GpuCapabilities(false, true, "NVIDIA T4")
        { PciVendorId = 0x10de, PciDeviceId = 0x1eb8, DriverVersion = "fictional driver", SupportsTimestamps = true };
        // Only identity comes through the retained host. Fixed completed samples remain fictional evidence.
        var info = host.Session.ReadInfo() with { Synthetic = false, NativeDevice = true, TimestampPeriodNanoseconds = .001 };
        var report = CoveragePerfReportVectors.Create("G2");
        report = report with { Blocks = report.Blocks.Select(block => block with { Session = info }).ToImmutableArray() };

        Assert.Empty(CoveragePerfValidity.Reasons(report));
        Assert.Equal(CoveragePerfBudgetStatus.Pass, CoveragePerfBudgets.Evaluate(report).Gpu.Status);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("")]
    [InlineData("unknown")]
    public void Unresolved_D3D_mode_refuses_before_any_frame(string mode)
    {
        var run = new CoveragePerfFakeRun("G2")
        { Setup = session => session.Info = session.Info with { D3DRecordingMode = mode } };
        var report = run.Run();

        Assert.Equal(CoveragePerfRunStatus.Refused, report.Status);
        var block = Assert.Single(report.Blocks);
        Assert.Contains("D3D recording mode", block.Reason, StringComparison.Ordinal);
        Assert.Empty(block.Warmup);
        Assert.Empty(block.Samples);
        Assert.Equal(0, Assert.Single(run.Sessions).Frames);
        Assert.True(run.Sessions[0].Disposed);
    }
}
