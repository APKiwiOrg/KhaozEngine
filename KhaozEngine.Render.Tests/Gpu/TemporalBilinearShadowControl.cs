using System;
using System.Reflection;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Paired bilinear-input control. Only visibility readiness is suppressed, colour history is retained.</summary>
internal static class TemporalBilinearShadowControl
{
    static readonly FieldInfo Seed = typeof(TemporalVisibilityHistory).GetField("_seededIndex",
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException("Visibility seed");

    internal static byte[][] Sequence(int width, int height, Action<Scene3D> setup,
        Action<Scene3D, int> draw, int warm, int count)
    {
        using var fx = new TemporalFixture(width, height, setup);
        void Control(Scene3D scene, int n)
        {
            draw(scene, n);
            if (scene.TemporalResolveRendererForTests is { } renderer)
                Seed.SetValue(renderer.VisibilityHistory, -1);
        }
        fx.Frames(warm, Control);
        var frames = new byte[count][];
        for (int t = 0; t < count; t++) frames[t] = fx.Frame(Control);
        Assert.True(fx.Scene.TemporalHistory.IsValid, "The paired control must retain colour history.");
        Assert.Equal(0, fx.Scene.TemporalResolveRendererForTests!.LastVisibilityUniforms.PreviousJitter.Z);
        return frames;
    }
}
