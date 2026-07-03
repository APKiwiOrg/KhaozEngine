using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Guards the per-material terrain sampler override surface (no GPU): the Default matches the shared sampler the
    // renderer builds (so a material with Sampler == null is byte-identical to prior behaviour), the ctor stores its
    // fields, and TerrainLayeredMaterial.Sampler defaults to null (opt-in).
    public sealed class TerrainSamplerConfigTests
    {
        [Fact]
        public void DefaultIsAnisotropic16Bias1()
        {
            var d = TerrainSamplerConfig.Default;
            Assert.Equal(GpuSamplerFilter.Anisotropic, d.Filter);
            Assert.Equal(16u, d.MaximumAnisotropy);
            Assert.Equal(1, d.MipLodBias);
        }

        [Fact]
        public void CtorStoresFields()
        {
            var c = new TerrainSamplerConfig(GpuSamplerFilter.MinLinearMagLinearMipLinear, 4, 3);
            Assert.Equal(GpuSamplerFilter.MinLinearMagLinearMipLinear, c.Filter);
            Assert.Equal(4u, c.MaximumAnisotropy);
            Assert.Equal(3, c.MipLodBias);
        }

        [Fact]
        public void TerrainLayeredMaterialSamplerDefaultsToNull()
        {
            Assert.Null(new TerrainLayeredMaterial().Sampler);
        }
    }
}
