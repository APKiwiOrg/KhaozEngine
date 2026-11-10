using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageAllocationTests
{
    [Theory]
    [InlineData(10ul)]
    [InlineData(20ul)]
    [InlineData(30ul)]
    public void Warm_preparation_publication_and_submission_allocate_zero_managed_bytes(ulong key)
    {
        using var fixture = new CoveragePublicationFixture();
        // Exercise both surface routes and retire a predecessor before measuring one real recording.
        fixture.Begin(); fixture.Queue(); fixture.Record(); fixture.Submit();
        fixture.Begin(); fixture.Queue(30); fixture.Record(); fixture.Submit();
        fixture.Begin(); fixture.Queue(key); fixture.Record(); fixture.Submit();
        fixture.Begin();
        long allocated;
        using (AllocationRegion.Start("coverage prepare publish submit"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            fixture.Queue(key); fixture.Record(); fixture.Submit();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.NotEmpty(fixture.Evidence);
        Assert.NotNull(fixture.History.SurfaceToken);
        Assert.Equal(0, allocated);
    }
    [Fact]
    public void Warm_per_actor_ordinary_fallback_and_discontinuity_allocate_zero_managed_bytes()
    {
        using var fixture = new CoveragePublicationFixture();
        fixture.Begin(); fixture.Queue(); fixture.Record(); fixture.Submit();
        fixture.Begin(); fixture.QueueUnsupportedPose(); fixture.Record(); fixture.Submit();
        fixture.Begin();
        long allocated;
        using (AllocationRegion.Start("coverage ordinary fallback discontinuity"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            fixture.QueueUnsupportedPose(); fixture.Record(); fixture.Submit();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Empty(fixture.Evidence); Assert.True(fixture.History.Discontinuity);
        Assert.Equal(0, allocated);
    }

}
