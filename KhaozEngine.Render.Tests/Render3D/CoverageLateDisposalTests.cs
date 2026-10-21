using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageLateDisposalTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Released_actor_after_encoding_cannot_publish_history(bool unload, bool withSibling, bool seeded)
    {
        using var h = new CoverageSceneFixture();
        var siblingHosts = h.LoadHost(2, out var skeleton);
        using var sibling = withSibling ? h.Scene.CreateHostConformer(skeleton, siblingHosts) : null;
        sibling?.ReserveSupports([h.Support]);
        using var submission = new GpuFrameSubmission(h.Capture);
        using var commands = new NullGpuCommandList();
        SkinnedCoverageFrame other = default;
        SkinnedCoverageFrame Queue()
        {
            h.Begin();
            var frame = h.Stage();
            h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
            h.SubmitEquipment();
            if (sibling is not null) other = QueueSibling(h, sibling, siblingHosts[0]);
            return frame;
        }
        if (seeded)
        {
            Queue();
            h.Render();
            Assert.NotNull(h.Conformer.Gpu.HistoryBatch);
        }
        var encoded = Queue();
        h.Scene.PrepareFrame(32, 24);
        submission.Begin();
        h.RecordUncaptured(commands, submission);
        Assert.True(encoded.Preparation!.MotionPresented);
        if (sibling is not null) Assert.True(other.Preparation!.MotionPresented);
        var batch = h.Scene.CoverageGpuBatchForTests!;
        var current = batch.Current;
        ulong revision = h.Conformer.MotionRevision;

        if (unload) h.Scene.UnloadSkinnedMesh(h.Host[0]);
        else h.Conformer.Dispose();
        Assert.False(h.Conformer.Gpu.Live);
        Assert.Null(h.Conformer.Gpu.HistoryBatch);
        if (sibling is null) Assert.Null(h.Scene.CoverageGpuBatchForTests);
        else Assert.Same(batch, h.Scene.CoverageGpuBatchForTests);

        Assert.Null(Record.Exception(() => { submission.Submit(commands); }));
        Assert.Null(h.Conformer.Gpu.HistoryBatch);
        Assert.Equal(revision, h.Conformer.MotionRevision);
        if (sibling is not null)
        {
            Assert.True(sibling.Gpu.Live);
            Assert.Same(batch, sibling.Gpu.HistoryBatch);
            Assert.Same(current, batch.Previous);
            Assert.Equal(other.Preparation!.MotionRevision, sibling.MotionRevision);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Binding_disposal_after_encoding_preserves_only_the_sibling_history(bool seeded)
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        using var sibling = f.Binding(92);
        Assert.True(binding.TrySetEquipment([equipment], out _));
        Assert.True(sibling.TrySetEquipment([equipment with { InstanceId = 7 }], out _));
        using var submission = new GpuFrameSubmission(f.Capture);
        using var commands = new NullGpuCommandList();
        SkinnedCoverageFrame other = default;
        SkinnedCoverageFrame Queue()
        {
            f.Scene.Begin();
            Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var frame, out _));
            f.Submit(frame, binding.PreparedEquipmentDraws.Span);
            Assert.True(sibling.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out other, out _));
            f.Submit(other, sibling.PreparedEquipmentDraws.Span, 92);
            return frame;
        }
        if (seeded)
        {
            var first = Queue();
            f.Render();
            Assert.NotNull(first.Preparation!.Owner.Gpu.HistoryBatch);
        }
        var encoded = Queue();
        f.Scene.PrepareFrame(32, 24);
        submission.Begin();
        f.RecordUncaptured(commands, submission);
        Assert.True(encoded.Preparation!.MotionPresented);
        Assert.True(other.Preparation!.MotionPresented);
        var retired = encoded.Preparation!.Owner;
        var survivor = other.Preparation!.Owner;
        var batch = f.Scene.CoverageGpuBatchForTests!;
        var current = batch.Current;
        binding.Dispose();

        Assert.Null(Record.Exception(() => { submission.Submit(commands); }));
        Assert.True(retired.Disposed);
        Assert.False(retired.Gpu.Live);
        Assert.Null(retired.Gpu.HistoryBatch);
        Assert.True(survivor.Gpu.Live);
        Assert.Same(batch, survivor.Gpu.HistoryBatch);
        Assert.Same(current, batch.Previous);
    }

    static SkinnedCoverageFrame QueueSibling(CoverageSceneFixture h, SkinnedHostConformer sibling, SkinnedMeshHandle host)
    {
        var equipment = new RigidInstanceDraw[1];
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(20), 1, 2, h.Support,
            [new(h.RigidHandle, Matrix4x4.Identity)], equipment, out var failure), failure.ToString());
        var poses = h.CommandPoses;
        poses[0] = poses[0] with { Topology = sibling.Topology, EquipmentInstanceId = 2 };
        Assert.True(sibling.TryPrepare(h.Palette, Matrix4x4.Identity, poses, MotionKey.From(20),
            CoverageFallback.UncoveredCurrentPose, out var frame, out failure), failure.ToString());
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(host, Matrix4x4.Identity) { Motion = MotionKey.From(20), Coverage = frame }, h.Palette);
        h.Scene.Draw(equipment[0]);
        return frame;
    }
}
