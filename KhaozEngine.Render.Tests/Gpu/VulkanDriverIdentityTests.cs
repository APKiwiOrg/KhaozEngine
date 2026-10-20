using System;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanDriverIdentityTests
{
    const string RawVersion = "raw driverVersion=0x12345678 (implementation-defined)";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptionalQueryFailureFallsBackToTheRawVersion(bool extensionCheckThrows)
    {
        string? driver = null;
        Exception? failure = Record.Exception(() => driver = Read(0x00403000, extensionCheckThrows ? 0x00401000u : 0x00403000u,
            () => throw new InvalidOperationException("extension query failed"),
            () => throw new EntryPointNotFoundException("properties query unavailable")));

        Assert.Null(failure);
        Assert.Equal(RawVersion, driver);
    }

    [Theory]
    [InlineData(0u, 0x00403000u)]
    [InlineData(0x00400000u, 0x00403000u)]
    [InlineData(0x00400000u, 0x00401000u)]
    public void InstanceWithoutProperties2NeverQueriesEvenWhenTheDeviceSupportsDriverProperties(uint instanceApi, uint deviceApi)
    {
        int extensionChecks = 0, propertyReads = 0;
        string driver = Read(instanceApi, deviceApi,
            () => { extensionChecks++; return true; },
            () => { propertyReads++; return ("driver", "version"); });

        Assert.Equal(RawVersion, driver);
        Assert.Equal(0, extensionChecks);
        Assert.Equal(0, propertyReads);
    }

    [Theory]
    [InlineData(0x00402000u, false)]
    [InlineData(0x00401000u, true)]
    public void Instance11CanQueryCoreOrAdvertisedDeviceProperties(uint deviceApi, bool extension)
    {
        string driver = Read(0x00401000, deviceApi, () => extension, () => ("driver", "version"));

        Assert.Equal("driver: version", driver);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" \t", "\r\n")]
    [InlineData("\0ignored", "\0ignored")]
    public void NoUsableDriverStringsFallBackToTheRawVersion(string? name, string? info)
        => Assert.Equal(RawVersion, Read(0x00403000, 0x00403000,
            () => throw new InvalidOperationException("core needs no extension"), () => (name, info)));

    static string Read(uint instanceApi, uint deviceApi, Func<bool> hasExtension,
        Func<(string? Name, string? Info)> readProperties)
        => VulkanDriverIdentity.Read(instanceApi, deviceApi, 0x12345678, hasExtension, readProperties);
}
