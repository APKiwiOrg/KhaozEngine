using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageOutlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outline_only_uses_the_final_surface(bool shadowOnly)
    {
        using var h = new CoverageSceneFixture();
        var bindings = new List<(string Pass, CoveragePulledDraw Draw)>();
        h.Scene.CoveragePullObserverForTests = (pass, draw) => bindings.Add((pass, draw));
        h.Begin();
        var frame = h.Stage();
        var draw = h.HostDraw(frame, shadowOnly);
        if (shadowOnly) h.Scene.DrawSkinned(draw, h.Palette);
        else h.Outline(draw);
        h.SubmitEquipment();
        h.Scene.PrepareFrame();
        var submittedCurrent = h.Scene.CoverageGpuBatchForTests!.Current;
        var submittedPrevious = h.Scene.CoverageGpuBatchForTests.Previous;
        h.Render(prepare: false);
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.DoesNotContain(bindings, binding => binding.Pass == "main");
        Assert.Equal(shadowOnly ? h.Scene.Post.Quality.Shadows.ShadowCascadeCount : 0,
            bindings.Count(binding => binding.Pass == "cascade"));
        Assert.Equal(shadowOnly ? 0 : 2, bindings.Count(binding => binding.Pass is "full" or "visible"));
        Assert.All(bindings, binding =>
        {
            Assert.Same(submittedCurrent, binding.Draw.Surface.Current);
            Assert.Same(submittedPrevious, binding.Draw.Surface.Previous);
            Assert.Equal(frame.Preparation!.GpuReservation.CurrentBase, binding.Draw.Surface.CurrentBase);
            Assert.Equal(frame.Preparation.GpuReservation.PreviousBase, binding.Draw.Surface.PreviousBase);
        });
        Assert.Same(submittedCurrent, h.Scene.CoverageGpuBatchForTests!.Previous);
        Assert.Same(submittedPrevious, h.Scene.CoverageGpuBatchForTests.Current);
        h.AssertCanonicalDraws(frame);
    }
}
