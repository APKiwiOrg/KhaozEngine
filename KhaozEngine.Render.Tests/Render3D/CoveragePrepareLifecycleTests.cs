using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoveragePrepareLifecycleTests
{
    [Fact]
    public void Registered_fallback_draws_after_preparation_are_rejected_before_queueing()
    {
        using var fixture = new CoverageBindingFixture();
        using var binding = fixture.Binding();
        Assert.True(binding.TrySetEquipment([fixture.Equipment()], out _));
        fixture.Scene.Begin();
        Assert.False(fixture.Prepare(binding, out _, out var failure, 30));
        Assert.Equal(CoverageFailureCode.TableHole, failure.Code);
        fixture.Scene.PrepareFrame();
        var error = Assert.Throws<CoverageFrameStateException>(() =>
            fixture.Scene.DrawSkinned(fixture.Host(default), fixture.Palette));
        Assert.Equal(CoverageFailureCode.StaleFrame, error.Reason);
        Assert.Empty(fixture.Scene.QueuedSkinnedInstancesForTests);
    }
    [Fact]
    public void Equipment_cache_disposal_after_preparation_restores_the_current_host_pose()
    {
        using var fixture = new CoveragePublicationFixture();
        fixture.Begin(); fixture.Queue(); fixture.Scene.PrepareFrame(128, 96);
        fixture.Host.Cache.Dispose();
        fixture.Record();
        Assert.Empty(fixture.Evidence);
        Assert.True(fixture.Scene.DrawnSkinnedInstances > 0);
        fixture.Submit(); Assert.True(fixture.History.Discontinuity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Anchored_equipment_preserves_coverage_admission_and_submitted_surface_history(bool rebased)
    {
        using var fixture = new CoveragePublicationFixture();
        int anchor = rebased ? 1 << 24 : 16;
        var model = rebased ? Matrix4x4.CreateTranslation(anchor, 0, 0) : Matrix4x4.Identity;
        if (rebased) fixture.Scene.RenderOrigin = new(anchor, 0, 0);
        fixture.Begin();
        Assert.True(fixture.Binding.TryPrepare(fixture.Host.Skeleton, fixture.Host.Palette, model,
            new(10), CoverageBindingFixture.Template, out var frame, out _));
        var equipment = Assert.Single(fixture.Binding.PreparedEquipmentDraws.ToArray()).Rigid;
        var local = equipment.World;
        local.M41 -= anchor;
        if (rebased)
        {
            // At 2^24 the absolute float has a two-unit ULP. This local quarter-unit offset rounds
            // to the same frozen absolute matrix, but moves the rendered equipment after rebasing.
            var shifted = local;
            shifted.M41 += .25f;
            var mismatched = Assert.Throws<CoverageFrameStateException>(() =>
                fixture.Scene.DrawAnchored(equipment with { World = shifted }, new(anchor, 0, 0)));
            Assert.Equal(CoverageFailureCode.StaleFrame, mismatched.Reason);
            Assert.Empty(fixture.Scene.QueuedInstancesForTests);
        }
        fixture.Scene.DrawAnchored(equipment with { World = local }, new(anchor, 0, 0));
        var duplicate = Assert.Throws<CoverageFrameStateException>(() => fixture.Scene.Draw(equipment));
        Assert.Equal(CoverageFailureCode.StaleFrame, duplicate.Reason);
        Assert.Single(fixture.Scene.QueuedInstancesForTests);
        foreach (var mesh in fixture.Host.Hosts)
            fixture.Scene.DrawSkinned(new(mesh, model) { Motion = fixture.Host.Actor, Coverage = frame }, fixture.Host.Palette);
        fixture.Record();
        Assert.NotEmpty(fixture.Evidence);
        Assert.Null(fixture.History.SurfaceToken);
        fixture.Submit();
        Assert.Same(fixture.Evidence[0].Surface, fixture.History.SurfaceToken);
    }

    [Fact]
    public void A_preparation_retry_cannot_replace_a_queued_raw_fallback_pose()
    {
        using var fixture = new CoveragePublicationFixture();
        fixture.Begin(); fixture.Queue(30);
        fixture.Host.Palette[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        Assert.False(fixture.Host.Prepare(fixture.Binding, out _, out var failure, 20));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        fixture.Record(); Assert.NotEmpty(fixture.Evidence); fixture.Submit();
        Assert.Equal(-1, fixture.History.Entry);
        fixture.Begin(); fixture.Queue(20); fixture.Record(); fixture.Submit();
        Assert.Equal(1, fixture.History.Entry);
    }

}
