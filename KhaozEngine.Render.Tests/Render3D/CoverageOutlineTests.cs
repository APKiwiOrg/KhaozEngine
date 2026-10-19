using System.Collections.Generic;
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
        var bindings = new List<CoveragePulledDraw>();
        h.Scene.CoveragePullObserverForTests = (_, draw) => bindings.Add(draw);
        h.Begin();
        var frame = h.Stage();
        var draw = h.HostDraw(frame, shadowOnly);
        if (shadowOnly) h.Scene.DrawSkinned(draw, h.Palette);
        else h.Outline(draw);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(shadowOnly ? 0 : 2, bindings.Count);
        Assert.All(bindings, draw => Assert.Same(h.Scene.CoverageGpuBatchForTests!.Current, draw.Surface.Current));
        h.AssertCanonicalDraws(frame);
    }
}
