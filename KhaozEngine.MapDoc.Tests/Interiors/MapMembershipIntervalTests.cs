using System;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipIntervalTests
{
    [Fact]
    public void Reservation_ExposesTheRequestBeforeEntryAndDoesNotEnterARefusedAllocation()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        int requests = 0, allocations = 0;
        admission.RequestingBytes = requested =>
        {
            Assert.Equal(requests == 0 ? 60m : 101m, requested);
            Assert.Equal(requests++ == 0 ? 0 : 60, admission.WorkspaceBytes);
        };
        admission.AllocationAdmitted = increment =>
        {
            Assert.Equal(60, increment);
            Assert.Equal(60, admission.WorkspaceBytes);
            allocations++;
        };
        using var owner = admission.Workspace();
        owner.Reserve(60);
        Assert.Throws<MapSceneCapacityException>(() => owner.Reserve(41));
        Assert.Equal(2, requests);
        Assert.Equal(1, allocations);
    }

    // 24 registers of 131 uint words, controls, one raw/included part and its cut/removal slots.
    internal static long EmptyHeader => 4096 + 12640 + 256 + 2 * Unsafe.SizeOf<MapMembershipPart>() + 5;

    [Theory]
    [InlineData("Header", 0)]
    [InlineData("Planner", 1024)]
    [InlineData("Publication", 5120)]
    [InlineData("Construction", 8000)]
    public void EmptyMembership_AdmitsEachStageAtItsIndependentThreshold(string stage, long extra)
    {
        using var native = NativeSceneFixtures.Create("empty");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(Scope()).Scene!;
        long threshold = EmptyHeader + extra;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: threshold));
        var exact = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.NotNull(exact.Counts);
        var exactStage = Stage(exact.Counts, stage).Root;
        Assert.Equal(threshold, exactStage.AdmittedPeakBytes);
        Assert.Equal(0m, exactStage.RefusedBytes);
        exact.Buffer?.PreparationOwnership?.Dispose();

        var below = MapRoofEvaluationBuffer.Prepare(scene, new(new(MaxPreparedBytes: threshold - 1)));
        Assert.Equal(MapSceneStatus.CapacityExceeded, below.Status);
        Assert.Null(below.Buffer);
        Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", threshold, threshold - 1), below.Capacity);
        Assert.NotNull(below.Counts);
        var refusedStage = Stage(below.Counts, stage).Root;
        Assert.Equal(threshold, refusedStage.RequestedBytes);
        Assert.Equal(threshold, refusedStage.RefusedBytes);
        Assert.True(refusedStage.AdmittedPeakBytes < threshold);
    }

    [Fact]
    public void HeaderRefusal_ReportsAttemptWithoutAdmittingItOrLosingEarlierOwners()
    {
        using var native = NativeSceneFixtures.Create("empty");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(Scope()).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000));
        using var earlier = admission.Workspace();
        earlier.Reserve(500000); earlier.Release(500000);
        earlier.Reserve(100);
        using var retained = earlier.Transfer(100);
        using var window = admission.LimitPreparation(200, "MembershipWindow", 50);
        window.ChargeExternal(200);
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Buffer);
        Assert.NotNull(result.Counts);
        long header = result.Counts.HeaderBytes;
        Assert.True(header > 0);
        var root = result.Counts.Membership.Root;
        var binding = result.Counts.Membership.BindingWindow!;
        Assert.Equal(300m, root.AdmittedPeakBytes);
        Assert.Equal(300 + header, root.RequestedBytes);
        Assert.Equal(300 + header, root.RefusedBytes);
        Assert.Equal(250m, binding.AdmittedPeakBytes);
        Assert.Equal(250 + header, binding.RequestedBytes);
        Assert.Equal(250 + header, binding.RefusedBytes);
        Assert.Equal(new MapSceneCapacity("MembershipWindow", 250 + header, 250), result.Capacity);
        Assert.Equal(500000, admission.PeakBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(100, admission.RetainedBytes);
    }

    [Fact]
    public void CompletedInterval_SeparatesRootAndWindowBaselinesFromHistoricalPeak()
    {
        using var native = NativeSceneFixtures.Create("empty");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(Scope()).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000));
        var prior = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        long priorBytes = prior.RetainedBytes;
        using var historical = admission.Workspace();
        historical.Reserve(500000); historical.Release(500000);
        using var window = admission.LimitPreparation(200000, "MembershipWindow", 50);
        window.ChargeExternal(200);
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.NotNull(result.Buffer);
        Assert.NotNull(result.Counts);
        var membership = result.Counts.Membership;
        var root = membership.Root;
        var binding = membership.BindingWindow!;
        Assert.Equal(priorBytes + 200, root.BaselineBytes);
        Assert.Equal(250m, binding.BaselineBytes);
        Assert.Equal(root.AdmittedPeakBytes - priorBytes + 50, binding.AdmittedPeakBytes);
        Assert.Equal(0m, root.RefusedBytes);
        Assert.True(root.AdmittedPeakBytes < 500000);
        Assert.False(result.Counts.CompleteAggregatePredictionAvailable);
        result.Buffer.PreparationOwnership!.Dispose();
        prior.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void RefusedGrowth_PreservesSuccessfulPeakAndReportsTheFullAttempt()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        using var interval = admission.ObservePeak();
        using var owner = admission.Workspace();
        owner.Reserve(60);
        owner.Reserve(40);
        var error = Assert.Throws<MapSceneCapacityException>(() => owner.Reserve(1));
        Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 101, 100), error.Capacity);
        Assert.Equal(100, interval.Bytes);
        var root = interval.Counts.Root;
        Assert.Equal(0m, root.BaselineBytes);
        Assert.Equal(100m, root.AdmittedPeakBytes);
        Assert.Equal(101m, root.RequestedBytes);
        Assert.Equal(101m, root.RefusedBytes);
        Assert.Equal(60m, root.LargestIncrementBytes);
    }

    [Fact]
    public void BorrowedOwner_IsCountedOnceAtRootAndInEveryUncoveredWindow()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000));
        using var prior = admission.Workspace();
        prior.Reserve(100);
        using var retained = prior.Transfer(100);
        using var outer = admission.LimitPreparation(301, "Outer", 25);
        using var inner = admission.LimitPreparation(301, "Inner", 50);
        inner.ChargeExternal(200);
        using var interval = admission.ObservePeak();
        using var product = admission.Workspace();
        using (var dependency = product.Borrow(retained))
        {
            product.Reserve(1);
            var refusal = Assert.Throws<MapSceneCapacityException>(() => product.Reserve(1));
            Assert.Equal(new MapSceneCapacity("Outer", 327, 326), refusal.Capacity);
        }
        Assert.Equal(301, admission.WorkspaceBytes + admission.RetainedBytes + 200);
        var counts = interval.Counts;
        var root = counts.Root;
        Assert.Equal(301m, root.AdmittedPeakBytes);
        Assert.Equal(302m, root.RequestedBytes);
        Assert.Equal(302m, root.RefusedBytes);
        var binding = counts.BindingWindow!;
        Assert.Equal(326m, binding.AdmittedPeakBytes);
        Assert.Equal(327m, binding.RefusedBytes);
    }

    internal static MapSceneScope Scope() => new(new(WorldFrame.Origin, new(-2, -2), new(2, 2), null, null,
        Enum.GetValues<MapSurfaceRole>(), null, new()), new(-2, -2, -2, 2, 8, 2), MapSceneChannels.All);
    static MapPreparationStageCounts Stage(MapRoofPreparationCounts counts, string stage) => stage switch
    {
        "Header" => counts.Header,
        "Planner" => counts.Planner,
        "Publication" => counts.Publication,
        "Construction" => counts.Construction,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}
