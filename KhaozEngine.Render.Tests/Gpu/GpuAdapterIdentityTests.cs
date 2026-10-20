using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuAdapterIdentityTests
{
    [Theory]
    [InlineData("PciVendorId", typeof(uint?))]
    [InlineData("PciDeviceId", typeof(uint?))]
    [InlineData("DriverVersion", typeof(string))]
    public void CreationFixedIdentityPreservesUnansweredValues(string name, Type expectedType)
    {
        var property = typeof(GpuCapabilities).GetProperty(name);
        Assert.NotNull(property);
        Assert.Equal(expectedType, property.PropertyType);
        Assert.Null(property.GetValue(new GpuCapabilities(false, true)));
        Assert.Null(property.GetValue(default(GpuCapabilities)));
    }
}
