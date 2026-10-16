using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageLifecycleTests
{
    [Fact]
    public void Equipment_snapshot_and_duplicate_staging_are_atomic()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        int before = h.Scene.QueuedInstancesForTests.Count;
        Assert.Throws<InvalidOperationException>(() => h.Scene.Draw(h.EquipmentDraw with { World = Matrix4x4.CreateScale(2) }));
        Assert.Equal(before, h.Scene.QueuedInstancesForTests.Count);
        var destination = new RigidInstanceDraw[1];
        Assert.False(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [h.EquipmentDraw with { Coverage = default }], destination, out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Null(destination[0].Coverage.Stage);
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
    }

    [Fact]
    public void Recycled_host_and_equipment_handles_reject_before_queue_mutation()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.UnloadSkinnedMesh(h.Host[0]);
        Assert.Throws<InvalidOperationException>(() => h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette));
        Assert.Empty(h.Scene.QueuedSkinnedInstancesForTests);
        var equipment = h.EquipmentDraw;
        h.Scene.UnloadMesh(equipment.Mesh);
        var replacement = h.Scene.LoadMesh(MeshPrimitives.Box(1));
        Assert.Equal(equipment.Mesh.Index, replacement.Index);
        Assert.NotEqual(equipment.Mesh.Generation, replacement.Generation);
        Assert.Throws<InvalidOperationException>(() => h.Scene.Draw(equipment));
        Assert.Empty(h.Scene.QueuedInstancesForTests);
    }

    [Fact]
    public void Disposal_invalidates_staged_tokens()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Conformer.Dispose();
        Assert.Throws<InvalidOperationException>(() => h.Scene.GetCoverageOutcome(frame));
        Assert.Throws<InvalidOperationException>(() => h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette));
        Assert.Empty(h.Scene.QueuedSkinnedInstancesForTests);
    }

    [Fact]
    public void Disposal_after_submission_freezes_explicit_fallback()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Outline(h.HostDraw(frame));
        h.SubmitEquipment();
        h.Conformer.Dispose();
        h.Render();
        var outcome = h.Scene.CoverageOutcomeForTests(frame);
        Assert.Equal(CoveragePresentation.UncoveredFallback, outcome.Presentation);
        Assert.Equal(CoverageFailureCode.StaleFrame, outcome.Failure.Code);
        Assert.Throws<InvalidOperationException>(() => h.Scene.GetCoverageOutcome(frame));
        h.AssertFallbackDraws(frame);
        h.AssertLegacyOutline(h.Scene.CoverageFallbackSliceForTests(frame, 0));
    }

    [Fact]
    public void Unloading_one_slice_after_submission_freezes_explicit_fallback()
    {
        using var h = new CoverageSceneFixture(seams: true);
        h.Begin();
        var frame = h.Stage();
        for (int slice = 0; slice < 2; slice++)
        {
            h.Scene.DrawSkinned(h.HostDraw(frame, slice: slice), h.Palette);
            h.Outline(h.HostDraw(frame, slice: slice));
        }
        h.SubmitEquipment();
        var unloaded = h.Scene.CoverageFallbackSliceForTests(frame, 1);
        h.Scene.UnloadSkinnedMesh(h.Host[1]);
        h.Render();
        var outcome = h.Scene.CoverageOutcomeForTests(frame);
        Assert.Equal(CoveragePresentation.UncoveredFallback, outcome.Presentation);
        Assert.Equal(CoverageFailureCode.IdentityMismatch, outcome.Failure.Code);
        h.AssertFallbackDraws(frame);
        h.AssertLegacyOutline(h.Scene.CoverageFallbackSliceForTests(frame, 0));
        Assert.DoesNotContain(h.Record.IndexedDraws, d => ReferenceEquals(d.VertexBuffer, unloaded.Vertices)
            || ReferenceEquals(d.IndexBuffer, unloaded.Indices));
    }

    [Fact]
    public void Partial_topology_allocation_releases_only_its_new_resources()
    {
        using var h = new CoverageSceneFixture(seams: true);
        var factory = h.Factory;
        int start = factory.Buffers.Count;
        factory.FailBufferCreationAfter(1);
        Assert.Throws<InvalidOperationException>(() => h.Scene.CreateHostConformer(
            new Skeleton([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1]), h.Host));
        Assert.Equal(start + 1, factory.Buffers.Count);
        Assert.True(factory.Buffers[start].Disposed);
        factory.ThrowOnBufferCreate = 0;
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Scene.DrawSkinned(h.HostDraw(frame, slice: 1), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
    }

    [Fact]
    public void Partial_frame_capacity_allocation_disposes_the_uncommitted_buffer()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        int start = h.Factory.Buffers.Count;
        h.Factory.FailBufferCreationAfter(1);
        Assert.Throws<InvalidOperationException>(() => h.Scene.PrepareFrame());
        Assert.True(h.Factory.Buffers[start].Disposed);
        h.Factory.ThrowOnBufferCreate = 0;
        h.Render();
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
    }
}
