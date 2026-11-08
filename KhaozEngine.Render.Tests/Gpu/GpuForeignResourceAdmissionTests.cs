using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuForeignResourceAdmissionTests
{
    [Theory]
    [InlineData("map buffer", "staging")]
    [InlineData("unmap buffer", "staging")]
    [InlineData("update buffer", "buffer")]
    [InlineData("map texture", "texture")]
    [InlineData("unmap texture", "texture")]
    [InlineData("update texture", "texture")]
    public void VulkanForeignResourcesUseTheOwnershipFamily(string operation, string parameter)
    {
        using var fixture = new VulkanDeviceFixture();
        using var foreignBuffer = new FakeBuffer(256);
        using var foreignTexture = new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.Staging);
        using IGpuBuffer ownBuffer = fixture.StagingBuffer();
        using IGpuTexture ownTexture = fixture.StagingTexture();

        void Apply(IGpuBuffer buffer, IGpuTexture texture)
        {
            switch (operation)
            {
                case "map buffer": fixture.Device.Map(buffer, GpuMapMode.Write); break;
                case "unmap buffer": fixture.Device.Unmap(buffer); break;
                case "update buffer": fixture.Device.UpdateBuffer<byte>(buffer, 0, new byte[4]); break;
                case "map texture": fixture.Device.Map(texture, GpuMapMode.Write); break;
                case "unmap texture": fixture.Device.Unmap(texture); break;
                case "update texture": fixture.Device.UpdateTexture(texture, new byte[64], 0, 0, 4, 4, 0, 0); break;
                default: throw new ArgumentException(nameof(operation));
            }
        }

        var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(
            () => Apply(foreignBuffer, foreignTexture));
        Assert.Equal(GpuResourceOwnershipRefusal.ForeignBackend, refusal.Reason);
        Assert.IsAssignableFrom<ArgumentException>(refusal);
        Assert.Equal(parameter, refusal.ParamName);
        Assert.Equal(GpuBackendKind.VulkanNative, refusal.ExpectedBackend);
        bool textureOperation = operation.EndsWith("texture", StringComparison.Ordinal);
        Assert.Equal(textureOperation ? typeof(FakeTexture) : typeof(FakeBuffer), refusal.ActualType);
        Assert.Equal(textureOperation ? typeof(VulkanTexture)
            : operation == "update buffer" ? typeof(IVulkanUploadDestination) : typeof(VulkanBuffer),
            refusal.ExpectedType);
        Assert.Equal(textureOperation ? "texture" : "buffer", refusal.ResourceKind);
        Assert.Equal(textureOperation ? "texture access"
            : operation == "update buffer" ? "UpdateBuffer" : "staging map", refusal.Context);

        if (operation == "unmap buffer") fixture.Device.Map(ownBuffer, GpuMapMode.Write);
        if (operation == "unmap texture") fixture.Device.Map(ownTexture, GpuMapMode.Write);
        Apply(ownBuffer, ownTexture);
        if (operation == "map buffer") fixture.Device.Unmap(ownBuffer);
        if (operation == "map texture") fixture.Device.Unmap(ownTexture);
    }
}
