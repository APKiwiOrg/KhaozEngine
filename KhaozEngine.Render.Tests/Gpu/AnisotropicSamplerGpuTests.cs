using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class AnisotropicSamplerGpuTests
    {
        [GpuFact]
        public void AnisotropicSamplerCreatesOrFallsBack()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            using var sampler = gpu.GpuDevice.Factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: 8));
            Assert.NotNull(sampler);
        }
    }
}
