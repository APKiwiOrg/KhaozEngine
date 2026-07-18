using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The terrain sampler uses anisotropy + a positive mip LOD bias to tame distance/grazing "fuzz" on a noisy
    // tiling albedo. LOD bias is a D3D11 / Vulkan feature; Metal's sampler has none and Veldrid THROWS on a
    // non-zero bias, so CreateSampler must feature-guard it to 0 there. This proves the sampler builds on every
    // backend (no throw) whether or not bias is supported - CI runs it on D3D11 + Vulkan (bias applied) and Metal
    // (bias dropped to 0), which is exactly the fallback we rely on.
    public sealed class SamplerLodBiasGpuTests
    {
        [GpuFact]
        public void AnisotropicSampler_WithMipLodBias_BuildsOnEveryBackend()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            using IGpuSampler s = gpu.GpuDevice.Factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap,
                maximumAnisotropy: 16, mipLodBias: 1));
            Assert.NotNull(s);
        }

        [GpuFact]
        public void PlainSampler_WithMipLodBias_BuildsOnEveryBackend()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            using IGpuSampler s = gpu.GpuDevice.Factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.MinLinearMagLinearMipLinear, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap,
                GpuSamplerAddress.Wrap, mipLodBias: 2));
            Assert.NotNull(s);
        }
    }
}
