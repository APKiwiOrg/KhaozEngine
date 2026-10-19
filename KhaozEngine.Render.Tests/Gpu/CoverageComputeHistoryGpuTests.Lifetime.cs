using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeHistoryGpuTests
{
    [GpuFact]
    public void Abandoned_encoding_preserves_raw_committed_words_and_next_frame_seeds()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        var batch = h.Scene.CoverageGpuBatchForTests!;
        var committedBuffer = batch.Previous;
        uint[] committed = CoverageSelectedReadback.Read(h.NativeDevice, committedBuffer);
        h.SubmitNative = false;
        var abandoned = Draw(h, .2f, recession: .0001f); h.Render();
        Assert.Same(committedBuffer, batch.Previous);
        Assert.False(abandoned.Preparation!.MotionPresented);
        // Test-only replay challenges read-only history even when the abandoned kernels execute.
        h.NativeDevice.Submit(h.Record.Inner); h.NativeDevice.WaitForIdle();
        Assert.Equal(committed, CoverageSelectedReadback.Read(h.NativeDevice, committedBuffer));
        Assert.Same(committedBuffer, batch.Previous);
        h.SubmitNative = true;
        var next = Draw(h, .6f); h.Render();
        Assert.False(next.Preparation!.MotionReusable);
        Assert.NotSame(committedBuffer, batch.Previous);
        var actual = VerifySelection(h, h.NativeDraws.First());
        var current = CoverageSelectedReadback.Read(h.NativeDevice, batch.Previous);
        Assert.Equal(1u, current[next.Preparation.GpuReservation.CurrentBase + 15]);
        CoverageHistoryEvidence.Write(h.NativeDevice, "abandon", new
        {
            HistoryCommittedAfterAbandon = false,
            AbandonedKernelReplayIsTestOnly = true,
            PriorActuallySubmittedWords = committed,
            ActualCurrentHeader = current.Take(16),
            ActualEffectiveCornerWords = actual,
        });
    }

    [GpuFact]
    public void Cold_growth_and_actor_removal_migrate_the_exact_previous_window()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        using var second = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        second.ReserveSupports([h.Support]);
        var key = MotionKey.From(20);
        Draw(h); var firstSecond = DrawActor(h, second, key, .2f); h.Render();
        uint oldOffset = firstSecond.Preparation!.GpuReservation.CurrentBase;
        Assert.True(oldOffset > 0);
        Draw(h, .8f); var nextSecond = DrawActor(h, second, key, .4f); h.Render();
        var draw = h.NativeDraws.Single(d => d.Surface.CurrentBase == nextSecond.Preparation!.GpuReservation.CurrentBase);
        uint[] correct = VerifySelection(h, draw);
        uint[] wrong = CoverageHistorySelectionProbe.Read(h, draw, previousBase: 0);
        Assert.NotEqual(correct.Where((_, i) => i % 6 >= 3), wrong.Where((_, i) => i % 6 >= 3));
        var oldBatch = h.Scene.CoverageGpuBatchForTests!;
        uint[] previous = CoverageSelectedReadback.Read(h.NativeDevice, oldBatch.Previous);
        int words = 16 + (nextSecond.Preparation!.GpuReservation.SourceCount + nextSecond.Preparation.GpuReservation.VertexCount) * 16;
        uint[] expected = previous.AsSpan((int)oldOffset, words).ToArray();
        h.Conformer.Dispose();
        h.Begin(); var migrated = DrawActor(h, second, key, .6f); h.Render();
        var newBatch = h.Scene.CoverageGpuBatchForTests!;
        Assert.NotSame(oldBatch, newBatch);
        Assert.Equal(0u, migrated.Preparation!.GpuReservation.CurrentBase);
        Assert.True(migrated.Preparation.MotionReusable);
        var actualDraw = Assert.Single(h.NativeDraws);
        uint[] rawPrevious = CoverageSelectedReadback.Read(h.NativeDevice, actualDraw.Surface.Previous);
        Assert.Equal(expected, rawPrevious.AsSpan(0, words).ToArray());
        uint[] actual = VerifySelection(h, actualDraw);
        CoverageHistoryEvidence.Write(h.NativeDevice, "cold-window", new
        {
            OldPreviousOffset = oldOffset,
            NewPreviousOffset = 0,
            WrongPreviousWindowDetected = true,
            ExpectedPriorSubmittedWords = expected,
            ActualMigratedPriorWords = rawPrevious,
            ActualProductionSelectedCornerWords = actual,
            BoundDrawWords = actualDraw.DrawWords,
        });
    }

    [GpuFact]
    public void Abandoned_cold_migration_keeps_the_original_source_through_retry()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        var sourceBatch = h.Scene.CoverageGpuBatchForTests!;
        var sourceBuffer = sourceBatch.Previous;
        uint[] submitted = CoverageSelectedReadback.Read(h.NativeDevice, sourceBuffer);
        using var growth = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        growth.ReserveSupports([h.Support]);
        h.SubmitNative = false;
        var abandoned = Draw(h, .4f); h.Render();
        Assert.True(abandoned.Preparation!.MotionReusable);
        Assert.False(abandoned.Preparation.MotionPresented);
        Assert.NotSame(sourceBatch, h.Scene.CoverageGpuBatchForTests);
        h.NativeDevice.Submit(h.Record.Inner); h.NativeDevice.WaitForIdle();
        Assert.Equal(submitted, CoverageSelectedReadback.Read(h.NativeDevice, sourceBuffer));
        h.SubmitNative = true;
        var retry = Draw(h, .2f); h.Render();
        Assert.False(retry.Preparation!.MotionReusable);
        Assert.Equal(submitted, CoverageSelectedReadback.Read(h.NativeDevice, sourceBuffer));
        var draw = Assert.Single(h.NativeDraws);
        uint[] actual = VerifySelection(h, draw);
        CoverageHistoryEvidence.Write(h.NativeDevice, "abandoned-cold-retry", new
        {
            ActualRetainedOriginalWords = submitted,
            ActualRetrySelectedWords = actual,
            HistoryCommittedAfterAbandon = false,
            RetrySeedsAfterTheFrameGap = true,
        });
    }

    [GpuFact]
    public void Unpresented_actor_retires_an_old_generation_and_can_return_without_a_new_warm_lease()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        using var growth = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        growth.ReserveSupports([h.Support]);
        h.Begin(); h.Render();
        Assert.Null(h.Conformer.Gpu.HistoryBatch);
        var returned = Draw(h, .4f); h.Render();
        Assert.False(returned.Preparation!.MotionReusable);
        Assert.Same(h.Scene.CoverageGpuBatchForTests, h.Conformer.Gpu.HistoryBatch);
        _ = VerifySelection(h, Assert.Single(h.NativeDraws));
        h.Begin(); h.Render();
        var sameBatch = h.Conformer.Gpu.HistoryBatch;
        Assert.NotNull(sameBatch);
        returned = Draw(h, .2f); h.Render();
        Assert.False(returned.Preparation!.MotionReusable);
        Assert.Same(sameBatch, h.Conformer.Gpu.HistoryBatch);
        _ = VerifySelection(h, Assert.Single(h.NativeDraws));
    }

    static SkinnedCoverageFrame DrawActor(CoverageSceneFixture h, SkinnedHostConformer conformer, MotionKey actor, float weight, Matrix4x4? worldOverride = null)
    {
        var world = worldOverride ?? Matrix4x4.Identity;
        var frozen = new RigidInstanceDraw[1];
        Assert.True(h.Scene.TryStageCoverageEquipment(actor, 1, 1, h.Support,
            [new RigidInstanceDraw(h.RigidHandle, world)], frozen, out var failure), failure.ToString());
        var commands = h.CommandPoses.Select(c => c with
        {
            Topology = conformer.Topology,
            TransitionWeight = weight,
            EquipmentWorld = world,
            CoverageWorld = h.Support.CoverageLocal * world
        }).ToArray();
        Assert.True(conformer.TryPrepare(h.Palette, world, commands, actor,
            CoverageFallback.UncoveredCurrentPose, out var frame, out failure), failure.ToString());
        for (int slice = 0; slice < h.Host.Length; slice++)
            h.Scene.DrawSkinned(h.HostDraw(frame, slice: slice) with { Motion = actor }, h.Palette);
        h.Scene.Draw(frozen[0]); h.LastCoverageFrame = frame;
        return frame;
    }
}
