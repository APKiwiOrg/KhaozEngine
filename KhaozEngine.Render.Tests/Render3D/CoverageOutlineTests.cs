using KhaozEngine.Render3D;
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
        h.Begin();
        var frame = h.Stage();
        var draw = h.HostDraw(frame, shadowOnly);
        if (shadowOnly) h.Scene.DrawSkinned(draw, h.Palette);
        else h.Outline(draw);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(h.Expected, h.Scene.CoverageVerticesForTests(frame).ToArray());
        h.AssertCanonicalDraws(frame);
    }
}
