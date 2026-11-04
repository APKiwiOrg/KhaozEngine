using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuForeignResourceSetAdmissionTests
{
    [Theory]
    [InlineData(true, GpuResourceKind.UniformBuffer)]
    [InlineData(true, GpuResourceKind.TextureReadOnly)]
    [InlineData(false, GpuResourceKind.UniformBuffer)]
    [InlineData(false, GpuResourceKind.TextureReadOnly)]
    [InlineData(false, GpuResourceKind.Sampler)]
    public void AForeignResourceOfTheDeclaredKindUsesTheOwnershipFamily(bool metal, GpuResourceKind kind)
    {
        using var buffer = new FakeBuffer(256);
        using var texture = new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
        using var sampler = new FakeSampler();
        IGpuBindableResource foreign = kind switch
        {
            GpuResourceKind.UniformBuffer => buffer,
            GpuResourceKind.TextureReadOnly => texture,
            _ => sampler,
        };
        var description = new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Resource", kind, GpuShaderStages.Fragment));

        void Check(IGpuBindableResource own, Func<IGpuBindableResource, IGpuResourceSet> create,
            GpuBackendKind backend, Type expectedType)
        {
            var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(() => create(foreign));
            Assert.Equal(GpuResourceOwnershipRefusal.ForeignBackend, refusal.Reason);
            Assert.Equal("resource", refusal.ParamName);
            Assert.Equal(backend, refusal.ExpectedBackend);
            Assert.Equal(foreign.GetType(), refusal.ActualType);
            Assert.Equal(expectedType, refusal.ExpectedType);
            Assert.Equal("Resource", refusal.ElementName);
            Assert.Equal(0, refusal.ResourceIndex);
            Assert.Equal(kind.ToString(), refusal.ResourceKind);
            Assert.Equal("resource set", refusal.Context);
            Assert.IsAssignableFrom<ArgumentException>(refusal);
            using IGpuResourceSet valid = create(own);
        }

        if (metal)
        {
            using var harness = new MetalRingHarness();
            using var layout = new MetalResourceLayout(harness.Liveness, description);
            using var ownTexture = harness.NewTexture(new GpuTextureDescription(4, 4,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            IGpuBindableResource own = kind == GpuResourceKind.UniformBuffer
                ? harness.NewBuffer(256, GpuBufferUsage.UniformBuffer) : ownTexture;
            Check(own, resource => new MetalResourceSet(harness.Liveness,
                new GpuResourceSetDescription(layout, resource)), GpuBackendKind.MetalNative,
                kind == GpuResourceKind.UniformBuffer ? typeof(MetalBuffer) : typeof(MetalTexture));
        }
        else
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuResourceLayout layout = fixture.Device.Factory.CreateResourceLayout(description);
            using IGpuBuffer ownBuffer = fixture.Device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using IGpuTexture ownTexture = fixture.Device.Factory.CreateTexture(
                new GpuTextureDescription(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuSampler ownSampler = fixture.Device.Factory.CreateSampler(GpuSamplerDescription.Linear);
            IGpuBindableResource own = kind switch
            {
                GpuResourceKind.UniformBuffer => ownBuffer,
                GpuResourceKind.TextureReadOnly => ownTexture,
                _ => ownSampler,
            };
            Check(own, resource => fixture.Device.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, resource)), GpuBackendKind.VulkanNative,
                kind == GpuResourceKind.UniformBuffer ? typeof(VulkanBuffer)
                    : kind == GpuResourceKind.TextureReadOnly ? typeof(VulkanTexture) : typeof(VulkanSampler));
        }
    }
}
