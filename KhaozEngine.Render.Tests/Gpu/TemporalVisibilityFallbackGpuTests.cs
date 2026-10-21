using System;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Compile and bind the outside-cost controls on a real backend without entering a timing fixture.
public sealed class TemporalVisibilityFallbackGpuTests(ITestOutputHelper output)
{
    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void VisibilityFreeControlsRenderThroughBothEntries(int entry, bool removeBarriers)
    {
        var stage = new FrontStage(96, 54, 4.5f);
        using var fx = new TemporalFixture(96, 54, s => stage.Setup(s, AntiAliasing.Temporal, TemporalUpscale.Quality));
        fx.Scene.TemporalResolveEntryForTests = (TemporalResolveEntry)entry;
        Func<string, string> rewrite = removeBarriers ? TemporalVisibilityCostPrograms.WithoutVisibilityOrBarriers
            : TemporalVisibilityCostPrograms.WithoutVisibility;
        var renderer = TemporalTestResolve.Install(fx, rewrite, disableVisibility: true);
        output.WriteLine($"{fx.Device.Backend} on {fx.Device.Capabilities.DeviceName}, entry {entry}, "
            + $"lock release barriers removed {removeBarriers}");
        var draw = TemporalResolveCostPerfGpuTests.BoxesScene(stage);
        foreach (TemporalUpscale preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
        {
            fx.Scene.Post.Temporal.Upscale = preset;
            fx.Frames(3, draw);
            Assert.True(fx.Scene.ResolvedLastRenderForTests);
            Assert.Equal((TemporalResolveEntry)entry, renderer.LastEntry);
            Assert.Null(fx.Scene.ResolvedVisibilityTarget);
            Assert.False(renderer.VisibilityHistory.Allocated);
            Assert.False(renderer.VisibilityHistory.StoredThisFrame);
            Assert.Equal(default, renderer.LastVisibilityUniforms);
        }
    }
}
