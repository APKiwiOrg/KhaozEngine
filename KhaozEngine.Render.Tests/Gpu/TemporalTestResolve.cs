using System;
using System.Reflection;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Install before the scene's first resolve. The scene owns and disposes the renderer as usual.
internal static class TemporalTestResolve
{
    internal static TemporalResolveRenderer Install(TemporalFixture fx, Func<string, string> rewrite,
        bool disableVisibility = false)
    {
        FieldInfo field = typeof(Scene3D).GetField("_temporalResolve", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scene3D no longer holds its resolve renderer in _temporalResolve");
        Assert.Null(field.GetValue(fx.Scene));
        var renderer = new TemporalResolveRenderer(fx.Device, rewrite, disableVisibility);
        fx.Scene.DisableTemporalVisibilityForTests = disableVisibility;
        field.SetValue(fx.Scene, renderer);
        return renderer;
    }
}
