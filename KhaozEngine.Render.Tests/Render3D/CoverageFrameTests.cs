using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageFrameTests
{
    [Fact]
    public void Prepared_frame_is_owned_and_immutable()
    {
        using var h = new CoverageSceneFixture();
        using var other = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        var draw = h.HostDraw(frame);
        h.Scene.DrawSkinned(draw, h.Palette);
        int before = h.Scene.QueuedSkinnedInstancesForTests.Count;
        Assert.Throws<InvalidOperationException>(() => other.Scene.DrawSkinned(draw, h.Palette));
        Assert.Equal(before, h.Scene.QueuedSkinnedInstancesForTests.Count);
        Assert.Throws<InvalidOperationException>(() => h.Scene.DrawSkinned(
            new SkinnedInstanceDraw(h.Host[0], Matrix4x4.Identity) { Coverage = frame, Motion = MotionKey.From(99) }, h.Palette));
        h.Palette[0] = Matrix4x4.CreateScale(99);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        h.AssertCoverageDispatch(frame);
        h.Scene.Begin();
        Assert.Throws<InvalidOperationException>(() => h.Scene.GetCoverageOutcome(frame));
    }

    [Fact]
    public void Missing_actual_equipment_selects_fallback()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Render();
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(0, h.Scene.DispatchedHostPreparationCount);
        h.AssertFallbackDraws(frame);
    }
}
