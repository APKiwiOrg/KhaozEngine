using System;
using KhaozEngine.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Logging;

[Collection("AllocSensitive")]
public sealed class AmbientScopeTests(ITestOutputHelper output)
{
    [Fact]
    public void FreshSlotReadsAllocateNothingBeforeFirstUseDuringScopeAndAfterDisposal()
    {
        var slot = new AmbientScope<string?>();
        var untouched = new AmbientScope<string?>();
        string? result = null;
        Action read = () =>
        {
            for (int i = 0; i < 1024; i++) result = slot.GetValueOrDefault("fallback");
        };
        read();
        long before = AllocationMeasurement.Measure("fresh ambient slot", read);
        Assert.Equal("fallback", result);
        long scoped;
        using (slot.Use(null))
        {
            read();
            scoped = AllocationMeasurement.Measure("ambient null override", read);
            Assert.Null(result);
            Assert.Equal("independent", untouched.GetValueOrDefault("independent"));
        }
        read();
        long restored = AllocationMeasurement.Measure("restored ambient slot", read);
        Assert.Equal("fallback", result);
        Assert.Equal("updated", slot.GetValueOrDefault("updated"));
        output.WriteLine($"1024 reads each, before={before}, scoped={scoped}, restored={restored} bytes");
        Assert.Equal(0, before);
        Assert.Equal(0, scoped);
        Assert.Equal(0, restored);
    }
}
