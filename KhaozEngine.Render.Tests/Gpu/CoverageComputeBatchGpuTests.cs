using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class CoverageComputeBatchGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Warm_changed_poses_dispatch_resolution_and_readback_allocate_zero()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var f = new CoverageBatchNativeFixture(gpu.GpuDevice, 2);
        f.Scene.ForceTemporalForTests = true;
        // Occupy all three diagnostic slots and both physical history generations before measuring.
        for (int warm = 0; warm < 3; warm++)
        {
            f.Pose(warm % 2, warm == 1, reference: false);
            f.Record(reverse: warm % 2 != 0);
        }
        f.Complete();
        Assert.Equal(6, f.OutcomeCount);
        var allocations = new CoverageBatchAllocation[2];
        var observations = new CoverageBatchObservation[2][];
        var growths = new int[2]; var waits = new int[2];
        var generations = new IGpuBuffer[2];
        int readbacks = f.Device.Readbacks;
        for (int frame = 0; frame < 2; frame++)
        {
            f.Pose(frame + 1, refusal: frame == 0);
            f.Record(reverse: frame == 0);
            generations[frame] = f.SubmittedCurrent;
            Assert.All(f.Actors, actor => Assert.True(actor.Frame.Preparation!.MotionReusable));
            f.Complete();
            allocations[frame] = f.Allocation; growths[frame] = f.Growths; waits[frame] = f.ProductionWaits;
            Assert.Equal(2, f.OutcomeCount);
            observations[frame] = f.Observe();
        }
        Write(f, "warm", new
        {
            Allocations = allocations.Select(a => a.ToString()),
            CompleteRenderAllocatedBytes = allocations.Select(a => a.RenderTotal).ToArray(),
            CompleteSubmissionAllocatedBytes = allocations.Select(a => a.SubmissionTotal).ToArray(),
            TestWaitStackCaptureEnabled = allocations.Select(a => a.TestWaitStackCaptureEnabled).ToArray(),
            Growths = growths,
            ProductionWaits = waits,
            CompletedDeviceReads = f.Device.Readbacks - readbacks,
            ComparedActors = 2,
            Frames = 2
        }, observations.SelectMany(r => r).ToArray());
        Assert.All(observations.SelectMany(r => r), row => row.AssertExact());
        Assert.Equal(CoverageFailureCode.RecessionExceeded, observations[0][0].ExpectedFailure.Code);
        Assert.True(observations[1][0].ExpectedFailure.IsSuccess);
        Assert.NotEqual(observations[0][1].ActualWords, observations[1][1].ActualWords);
        Assert.NotSame(generations[0], generations[1]);
        Assert.Equal(2, f.Device.Readbacks - readbacks);
        Assert.All(allocations, a => a.AssertZero());
        Assert.All(growths, n => Assert.Equal(0, n)); Assert.All(waits, n => Assert.Equal(0, n));
    }

    [GpuFact]
    public void Thirty_two_mixed_actors_are_independent()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var f = new CoverageBatchNativeFixture(gpu.GpuDevice, 32);
        f.Pose(1, refusal: true);
        // One correctness batch. Reverse actor and material submission while mixing topology and command order.
        f.Device.CaptureWaitSites = true;
        f.Record(reverse: true);
        f.Device.CaptureWaitSites = false;
        int readbacks = f.Device.Readbacks;
        f.Complete();
        int completedDeviceReads = f.Device.Readbacks - readbacks;
        var rows = f.Observe();
        int comparedActors = rows.Length;
        int crossActorSurfaceOrOutcomeMismatches = rows.Sum(r => r.WordMismatches + r.IdentityMismatches);
        Write(f, "mixed32", new
        {
            ComparedActors = comparedActors,
            CrossActorSurfaceOrOutcomeMismatches = crossActorSurfaceOrOutcomeMismatches,
            ColdBatchAllocationObservations = f.Allocation.ToString(),
            ColdCompleteRenderAllocatedBytes = f.Allocation.RenderTotal,
            ColdCompleteSubmissionAllocatedBytes = f.Allocation.SubmissionTotal,
            ColdCompleteRenderIncludesTestWaitStackCapture = f.Allocation.TestWaitStackCaptureEnabled,
            ColdConfigurationResourceCreations = f.Growths,
            TotalProductionWaitsIncludingColdConfiguration = f.ProductionWaits,
            ColdWaitCallStacks = f.Device.WaitSites.Take(f.Device.WaitSiteCount).ToArray(),
            ProductionReadbackWaits = f.ReadbackWaits,
            SubmittedBatchCount = f.FrameSubmissions,
            OtherColdSeamSubmissions = f.OtherProductionSubmissions,
            AssertionCopySubmissions = f.AssertionCopySubmissions,
            CompletedDeviceReads = completedDeviceReads
        }, rows);
        Assert.Equal(32, comparedActors); Assert.Equal(32, f.OutcomeCount);
        Assert.Equal(32, rows.Select(r => r.Actor).Distinct().Count());
        Assert.True(rows.Select(r => r.RefinedVertices).Distinct().Count() > 1);
        Assert.True(rows.Select(r => r.MaterialSlices).Distinct().Count() > 1);
        Assert.Contains(rows, r => r.CommandCount == 1); Assert.Contains(rows, r => r.CommandCount == 2);
        Assert.All(rows, row => Assert.Equal(31 - (int)(row.Actor - 100), row.ActorSlot));
        Assert.Contains(rows, r => !r.ExpectedFailure.IsSuccess); Assert.Contains(rows, r => r.ExpectedFailure.IsSuccess);
        Assert.Equal(0, crossActorSurfaceOrOutcomeMismatches);
        Assert.All(rows, row => row.AssertExact());
        Assert.Equal(1, f.FrameSubmissions);
        Assert.Equal(1, f.AssertionCopySubmissions);
        Assert.Equal(1, completedDeviceReads);
        Assert.Equal(0, f.ReadbackWaits);
    }

    [GpuFact]
    public void Warm_full_ring_drops_only_diagnostics_without_allocating_or_waiting()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var f = new CoverageBatchNativeFixture(gpu.GpuDevice, 2);
        // Warm completed enumeration before the independently measured full-ring refusal.
        for (int warm = 0; warm < 3; warm++)
        {
            f.Pose(warm % 2, refusal: warm == 1, reference: false);
            f.Record(reverse: warm % 2 != 0);
        }
        f.Complete();
        Assert.Equal(6, f.OutcomeCount);
        for (int frame = 0; frame < 3; frame++)
        {
            f.Pose(frame % 2, refusal: frame == 1, reference: false);
            f.Record(reverse: frame % 2 != 0);
        }
        f.Pose(1, refusal: false, reference: false);
        f.Record(reverse: true);
        ulong droppedFrame = f.Actors[0].Generation;
        var recording = f.Allocation;
        int growths = f.Growths, waits = f.ProductionWaits;
        f.Complete();
        var completed = f.CompletedOutcomes();
        Write(f, "full-ring", new
        {
            Allocation = recording.ToString(),
            CompleteRenderAllocatedBytes = recording.RenderTotal,
            CompleteSubmissionAllocatedBytes = recording.SubmissionTotal,
            TestWaitStackCaptureEnabled = recording.TestWaitStackCaptureEnabled,
            CompletedEnumeration = f.Allocation.OutcomeEnumeration,
            WarmCapacityGrowths = growths,
            ProductionWaits = waits,
            DroppedFrame = droppedFrame,
            DiagnosticFrames = completed.Select(r => r.FrameId).Distinct().Count(),
            CompletedOutcomes = completed.Length,
            Outcomes = completed
        }, []);
        Assert.Equal(6, completed.Length);
        Assert.DoesNotContain(completed, row => row.FrameId == droppedFrame);
        Assert.All(completed, row => Assert.InRange(row.FrameId, droppedFrame - 3, droppedFrame - 1));
        Assert.Equal(3, completed.Select(r => r.FrameId).Distinct().Count());
        recording.AssertZero(); Assert.Equal(0, f.Allocation.OutcomeEnumeration);
        Assert.Equal(0, growths); Assert.Equal(0, waits);
    }

    void Write(CoverageBatchNativeFixture fixture, string name, object facts, CoverageBatchObservation[] rows)
    {
        string file = "coverage-batch-" + name + "." + fixture.Device.Backend + ".json";
        CoverageArtifacts.WriteJson(file, new
        {
            EvidenceKind = "native-selected-words-and-completed-outcomes",
            Backend = fixture.Device.Backend.ToString(),
            Device = fixture.Device.Capabilities.DeviceName,
            Facts = facts,
            Actors = rows,
            CpuTimingGate = "unmeasured",
            GpuTimingGate = "unmeasured"
        });
        output.WriteLine($"{fixture.Device.Backend} {name}: {facts}");
        output.WriteLine($"Actual device words and completed outcomes: {file}");
    }
}
