using System;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageInstanceAdmissionTests
{
    [Fact]
    public void Generation_AdmitsAndSettlesItsActualBuilderAndFrozenAllocations()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.TwoSamplesPerChunk());
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000));
        using var active = admission.Enter();
        using var operation = admission.BeginPreparation();
        int allocations = 0, releases = 0;
        var limits = new MapFoliageLimits
        {
            AllocateInstances = bytes => { allocations++; admission.ReserveBytes(bytes); },
            ReleaseInstances = bytes => { releases++; admission.ReleaseUnusedBytes(bytes); },
        };
        MapFoliageInstanceBatch batch;
        using (MapSceneAdmission.Suspend()) batch = MapFoliageDistribution.GenerateInstances(context, "cover-a", default, limits);
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.True(allocations > 8);
        Assert.True(releases > 0);
        Assert.Equal(batch.Work.RetainedBytes, operation.Bytes);
        Assert.True(admission.PeakBytes > operation.Bytes);
        using var receipt = operation.Complete(batch.Work.RetainedBytes, publishToParent: false);
        receipt.Dispose(); Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void Generation_PropagatesPreallocationRefusalWithoutPublishingPartialColumns()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.TwoSamplesPerChunk());
        int calls = 0;
        var limits = new MapFoliageLimits { AllocateInstances = _ => { calls++; throw new AllocationRefusal(); } };
        Assert.Throws<AllocationRefusal>(() => MapFoliageDistribution.GenerateInstances(context, "cover-a", default, limits));
        Assert.Equal(1, calls);
    }
    sealed class AllocationRefusal : Exception { }
}
