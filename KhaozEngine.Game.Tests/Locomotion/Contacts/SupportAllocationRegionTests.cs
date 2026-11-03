using System;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportAllocationRegionTests
{
    [Fact]
    public void AnInducedCollectionFailsAsAnAllocationAssertion()
    {
        using var region = SupportAllocationRegion.Start();
        GC.Collect();
        var failure = Assert.Throws<AllocationMeasurementException>(region.Dispose);
        Assert.Equal(AllocationMeasurementRefusal.CollectionInterrupted, failure.Reason);
        Assert.Equal(nameof(AnInducedCollectionFailsAsAnAllocationAssertion), failure.Description);
    }
}
