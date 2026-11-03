using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShaderGateExactSizeTests
{
    [Theory]
    [InlineData(true, 1f, 1)]
    [InlineData(true, 0.5f, 0)]
    [InlineData(false, 1f, 0)]
    public void The_condition_reports_active_temporal_at_exact_size_independently_of_the_gate(bool temporal, float ratio, int expected)
    {
        using var scene = new MotionTestScene();
        scene.Scene.ForceTemporalForTests = temporal;
        scene.Scene.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Off;
        scene.Scene.Post.Temporal.UpscaleRatio = ratio;
        scene.Scene.Post.RenderScale = RenderScale.MatchViewport;
        Assert.Equal(-1, scene.Scene.ConsumeProbeExactSizeCondition());
        scene.Render();
        Assert.Equal(expected, scene.Scene.ConsumeProbeExactSizeCondition());
        Assert.Equal(-1, scene.Scene.ConsumeProbeExactSizeCondition());
    }
}
