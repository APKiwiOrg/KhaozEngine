using System;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Install before the scene's first resolve. The scene owns and disposes the renderer as usual.
internal static class TemporalTestResolve
{
    internal static TemporalResolveRenderer Install(TemporalFixture fx, Func<string, string> rewrite)
    {
        Assert.Null(fx.Scene.TemporalResolveRendererForTests);
        return fx.Scene.InstallTemporalResolveForTests(rewrite);
    }
}
