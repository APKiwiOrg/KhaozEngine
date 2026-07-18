using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class PredictionSettingsTests
{
    [Fact]
    public void Default_correction_dead_zone_is_small_so_human_scale_corrections_smooth()
    {
        // A 1.5-unit dead-zone snapped every realistic latency misprediction (all well under 1.5 m) instead of
        // gliding it, which read as jitter while moving. The default is now a few centimetres.
        Assert.Equal(0.03f, PredictionSettings.Default.CorrectionDeadZone, 5);
        Assert.True(PredictionSettings.Default.CorrectionDeadZone < 0.1f);
    }

    [Fact]
    public void Default_hard_snap_and_correction_rate_are_unchanged()
    {
        Assert.Equal(100f, PredictionSettings.Default.HardSnapDistance, 3);
        Assert.Equal(8f, PredictionSettings.Default.CorrectionRate, 3);
    }
}
