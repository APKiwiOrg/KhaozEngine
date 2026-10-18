using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The broad luma change releases carried locks, while absolute darkness and missed features keep them.</summary>
public sealed class TemporalResolveLumaLockGpuTests : TemporalResolveGpuFacts
{
    [GpuTheory]
    [InlineData(0.1f, 0.4f, 0f)]
    [InlineData(0.4f, 0.1f, 0f)]
    // This change exceeds the corner floor but stays below the mean floor, so the latter is exercised.
    [InlineData(0.008f, 0.02f, 0.875f)]
    public void A_carried_lock_releases_only_on_a_material_mean_luma_change(float current, float previous, float expected)
    {
        using var rig = new Rig();
        rig.BeginFrame();
        rig.Fill(Grey((_, _) => Q(current)), Grey((_, _) => Q(current)), Motion((_, _) => Vector2.Zero));
        rig.FillHistory(Grey((_, _) => Q(previous)), State((_, _) => new Vector2(Q(8f / Max), 1f)), SceneLinear);
        rig.Resolve(Uniforms(historyValid: true));
        Assert.Equal(expected, rig.ReadState()[At(4, 3) * 2 + 1], 1e-3);
    }
}
