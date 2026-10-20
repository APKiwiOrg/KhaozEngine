using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuBackendAdapterIdentityTests
{
    [Theory]
    [InlineData(0x10deu, 0x1eb8u)]
    [InlineData(0x1414u, 0x008cu)]
    [InlineData(0u, 0u)]
    public void DxgiIdentitySurvivesCapabilityAssemblyIncludingSoftwareAndZeroIds(uint vendor, uint device)
    {
        var identity = D3D11AdapterIdentity.Read(() => (vendor, device), () => 0x00200000000f1234L);
        var caps = D3D11CapabilityRead.Assemble("adapter", 1, true, true, identity: identity);
        Assert.Equal(vendor, caps.PciVendorId);
        Assert.Equal(device, caps.PciDeviceId);
        Assert.Equal("32.0.15.4660", caps.DriverVersion);
    }

    [Theory]
    [InlineData(0L, "0.0.0.0")]
    [InlineData(-1L, "65535.65535.65535.65535")]
    [InlineData(0x0001000200030004L, "1.2.3.4")]
    public void DxgiDriverVersionUsesAllFourUnsignedWords(long value, string expected)
        => Assert.Equal(expected, D3D11AdapterIdentity.Read(() => (0u, 0u), () => value).DriverVersion);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnansweredDxgiDriverDoesNotEraseTheAdapterIds(bool throws)
    {
        var identity = D3D11AdapterIdentity.Read(() => (0x10deu, 0x1eb8u),
            () => throws ? throw new InvalidOperationException("query failed") : null);
        Assert.Equal(0x10deu, identity.VendorId);
        Assert.Equal(0x1eb8u, identity.DeviceId);
        Assert.Null(identity.DriverVersion);
    }

    [Fact]
    public void UnansweredDxgiDescriptionDoesNotEraseTheDriverOrInventZeroIds()
    {
        var identity = D3D11AdapterIdentity.Read(() => throw new InvalidOperationException("no description"), () => 0);
        Assert.Null(identity.VendorId);
        Assert.Null(identity.DeviceId);
        Assert.Equal("0.0.0.0", identity.DriverVersion);
        var caps = D3D11CapabilityRead.Assemble("adapter", 1, true, true);
        Assert.Null(caps.PciVendorId);
        Assert.Null(caps.PciDeviceId);
        Assert.Null(caps.DriverVersion);
    }

    [Theory]
    [InlineData(0x00402000u, false, 0)]
    [InlineData(0x00403000u, false, 0)]
    [InlineData(0x00401000u, true, 1)]
    public void VulkanQueriesDriverPropertiesOnlyWhenCoreOrAdvertised(uint api, bool extension, int extensionReads)
    {
        int checkedExtension = 0, reads = 0;
        string driver = VulkanDriverIdentity.Read(api, 0x1234, () => { checkedExtension++; return extension; },
            () => { reads++; return ("llvmpipe", "Mesa 26.0.0 (LLVM 21.1.8)"); });
        var caps = VulkanCapabilityRead.Assemble("llvmpipe (LLVM 21.1.8, 256 bits)", false, true, GpuSampleCounts.One, 4,
            pciVendorId: 0x10005, pciDeviceId: 0, driverVersion: driver);
        Assert.Equal(extensionReads, checkedExtension);
        Assert.Equal(1, reads);
        Assert.Equal(0x10005u, caps.PciVendorId);
        Assert.Equal(0u, caps.PciDeviceId);
        Assert.Equal("llvmpipe: Mesa 26.0.0 (LLVM 21.1.8)", caps.DriverVersion);
    }

    [Theory]
    [InlineData(0u, "raw driverVersion=0x00000000 (implementation-defined)")]
    [InlineData(0x12345678u, "raw driverVersion=0x12345678 (implementation-defined)")]
    public void UnsupportedVulkanDriverPropertiesUseHonestRawVersionWithoutAQuery(uint raw, string expected)
        => Assert.Equal(expected, VulkanDriverIdentity.Read(0x00401000, raw, () => false,
            () => throw new InvalidOperationException("unsupported query")));

    [Theory]
    [InlineData(null, null, "raw driverVersion=0x00000001 (implementation-defined)")]
    [InlineData("", "", "raw driverVersion=0x00000001 (implementation-defined)")]
    [InlineData("driver", "", "driver (raw driverVersion=0x00000001 (implementation-defined))")]
    [InlineData("", "version", "version")]
    [InlineData("driver\0ignored", "version\0ignored", "driver: version")]
    public void VulkanKeepsAnsweredStringsAndFallsBackForMissingVersions(string? name, string? info, string expected)
        => Assert.Equal(expected, VulkanDriverIdentity.Read(0x00402000, 1,
            () => throw new InvalidOperationException("core needs no extension"), () => (name, info)));

    [Fact]
    public void MetalDoesNotSubstituteRegistryOrOsIdentityForUnexposedDriverAndPciValues()
    {
        var caps = MetalCapabilityRead.Assemble("Apple M2 Max", 4);
        Assert.Equal("Apple M2 Max", caps.DeviceName);
        Assert.Null(caps.PciVendorId);
        Assert.Null(caps.PciDeviceId);
        Assert.Null(caps.DriverVersion);
    }
}
