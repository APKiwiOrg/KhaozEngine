using System;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportAllocationRegionTests
{
    [Fact]
    public void AnInducedCollectionFailsAsAnAllocationAssertion()
    {
        using var region = SupportAllocationRegion.Start();
        GC.Collect();
        Exception? failure = Record.Exception(region.Dispose);
        Assert.IsAssignableFrom<XunitException>(failure);
        Assert.Contains("collection", failure!.Message, StringComparison.OrdinalIgnoreCase);
    }
}
