using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Temporary test-assembly-only token injection for the archive's one hosted diagnostic dispatch.
/// The workflow has no environment input. Normal runs and the engine library never select a token here.</summary>
public sealed class TemporalVisibilityArchiveGpuTests(ITestOutputHelper output)
{
    internal const string HostedToken = "vis-release-0.1";
    static bool ArchiveDispatch => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") == "cross-platform-gpu"
        && Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") == "workflow_dispatch"
        && Environment.GetEnvironmentVariable("GITHUB_REF_NAME") == "fix/taa-grain-switches";

    [ModuleInitializer]
    internal static void SelectHostedDiagnostic()
    {
        if (ArchiveDispatch && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_TEMPORAL_DIAG")))
            Environment.SetEnvironmentVariable("KE_TEMPORAL_DIAG", HostedToken);
    }

    [GpuFact]
    public void Hosted_archive_dispatch_has_the_selected_visibility_signal_enabled()
    {
        output.WriteLine($"Visibility archive token {Environment.GetEnvironmentVariable("KE_TEMPORAL_DIAG") ?? "unset"}, "
            + $"startup mask {TemporalResolveDiagnostics.InitialMask}, MRT enabled {TemporalVisibilityDiagnostics.Enabled}");
        if (!ArchiveDispatch) return;
        Assert.Equal(HostedToken, Environment.GetEnvironmentVariable("KE_TEMPORAL_DIAG"));
        Assert.Equal(16777216, TemporalResolveDiagnostics.InitialMask);
        Assert.True(TemporalVisibilityDiagnostics.Enabled);
    }
}
