using System;
using System.IO;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class PointShadowAttributionEnvironmentTests
{
    [Theory]
    [InlineData("off")]
    [InlineData("timings")]
    public void Attribution_without_counts_refuses_the_recorder(string mode)
    {
        string? path = Environment.GetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH");
        string? shadow = Environment.GetEnvironmentVariable("KE_SHADOW_STAGE_MODE");
        string? attribution = Environment.GetEnvironmentVariable("KE_POINT_SHADOW_ATTRIBUTION");
        string? ring = Environment.GetEnvironmentVariable("KE_RING_WAIT_PROBE");
        try
        {
            Environment.SetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
            Environment.SetEnvironmentVariable("KE_SHADOW_STAGE_MODE", mode);
            Environment.SetEnvironmentVariable("KE_POINT_SHADOW_ATTRIBUTION", "1");
            Environment.SetEnvironmentVariable("KE_RING_WAIT_PROBE", null);
            using var recorder = FrameDiagnosticsRecorder.FromEnvironment();
            Assert.Null(recorder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH", path);
            Environment.SetEnvironmentVariable("KE_SHADOW_STAGE_MODE", shadow);
            Environment.SetEnvironmentVariable("KE_POINT_SHADOW_ATTRIBUTION", attribution);
            Environment.SetEnvironmentVariable("KE_RING_WAIT_PROBE", ring);
        }
    }
}
