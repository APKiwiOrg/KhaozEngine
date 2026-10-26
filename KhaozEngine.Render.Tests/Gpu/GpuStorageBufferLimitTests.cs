using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuStorageBufferLimitTests
{
    [Theory]
    [InlineData(0u, 0UL, 0UL, 0u)]
    [InlineData(0xa100u, 17179869184UL, 0UL, 0u)]
    [InlineData(0xb000u, 0UL, 134217728UL, 134217728u)]
    [InlineData(0xb000u, 268435456UL, 134217728UL, 134217728u)]
    [InlineData(0xb000u, 1073741824UL, 268435456UL, 268435456u)]
    [InlineData(0xb000u, 2147483648UL, 536870912UL, 536870912u)]
    [InlineData(0xb000u, 4294967296UL, 1073741824UL, 536870912u)]
    [InlineData(0xb000u, 17179869184UL, 2147483648UL, 536870912u)]
    [InlineData(0xb100u, ulong.MaxValue, 2147483648UL, 536870912u)]
    public void D3D11_storage_range_intersects_feature_adapter_and_raw_view_limits(
        uint featureLevel, ulong dedicatedBytes, ulong resourceBytes, uint storageBytes)
    {
        var limits = new D3D11BufferLimits(featureLevel, dedicatedBytes);

        Assert.Equal(resourceBytes, limits.ResourceBytes);
        var capabilities = D3D11CapabilityRead.Assemble("selected adapter", 1, true, true, bufferLimits: limits);
        Assert.Equal(storageBytes, capabilities.MaxStorageBufferRangeBytes);
    }
}
