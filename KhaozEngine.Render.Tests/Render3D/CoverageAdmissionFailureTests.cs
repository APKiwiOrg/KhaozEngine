using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageAdmissionFailureTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void Unsupported_fallback_throws_before_staging_and_keeps_preparation_usable(int fallback, bool opened)
    {
        using var h = new CoverageSceneFixture();
        if (opened) h.Begin();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => h.Conformer.TryPrepare(h.Palette,
            Matrix4x4.Identity, h.CommandPoses, MotionKey.From(10), (CoverageFallback)fallback, out _, out _));
        Assert.Equal("fallback", error.ParamName);
        Assert.Empty(h.Scene.QueuedSkinnedInstancesForTests);
        if (!opened) h.Begin();
        _ = h.Stage();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_rebound_host_actor_is_an_identity_refusal(bool rebound)
    {
        using var h = new CoverageSceneFixture();
        if (rebound) { h.Begin(); _ = h.Stage(); }
        h.Begin();
        Assert.False(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, h.CommandPoses,
            rebound ? MotionKey.From(11) : MotionKey.None, CoverageFallback.UncoveredCurrentPose,
            out var frame, out var failure));
        Assert.Equal(default, frame);
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Empty(h.Scene.QueuedSkinnedInstancesForTests);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("instance")]
    [InlineData("command")]
    public void Invalid_equipment_identity_refuses_without_claiming_the_snapshot(string invalid)
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var sentinel = new RigidInstanceDraw(h.RigidHandle, Matrix4x4.CreateTranslation(77, 0, 0));
        var frozen = new[] { sentinel };
        Assert.False(h.Scene.TryStageCoverageEquipment(invalid == "actor" ? MotionKey.None : MotionKey.From(10),
            invalid == "command" ? 0u : 1u, invalid == "instance" ? 0ul : 1ul, h.Support,
            [new(h.RigidHandle, Matrix4x4.Identity)], frozen, out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Equal(sentinel, frozen[0]);
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new(h.RigidHandle, Matrix4x4.Identity)], frozen, out failure), failure.ToString());
    }

    [Fact]
    public void A_closed_or_unopened_arena_keeps_the_stale_frame_refusal()
    {
        using var h = new CoverageSceneFixture();
        Assert.False(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, h.CommandPoses, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out _, out var failure));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        Assert.False(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new(h.RigidHandle, Matrix4x4.Identity)], new RigidInstanceDraw[1], out failure));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
    }
}
