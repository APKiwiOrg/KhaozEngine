using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    // Guards the diagnostic fields added to GpuCapabilities (device name + sampler feature flags), used by the
    // in-game debug panel to surface which physical GPU is rendering and whether the sampler anisotropy / mip LOD
    // bias levers are even supported on that device.
    public sealed class GpuCapabilitiesDiagTests
    {
        readonly ITestOutputHelper _out;
        public GpuCapabilitiesDiagTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void CtorStoresDiagnosticFields()
        {
            var c = new GpuCapabilities(clipSpaceYInverted: true, depthRangeZeroToOne: true,
                deviceName: "Test GPU", samplerAnisotropy: true, samplerLodBias: false);
            Assert.Equal("Test GPU", c.DeviceName);
            Assert.True(c.SamplerAnisotropy);
            Assert.False(c.SamplerLodBias);
        }

        [Fact]
        public void DeviceNameNeverNull()
        {
            Assert.Equal("", new GpuCapabilities(false, false, null!).DeviceName);
        }

        [GpuFact]
        public void LiveDeviceReportsNameAndSamplerFeatures()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            var caps = ctx.Capabilities;
            _out.WriteLine($"backend={ctx.Backend} device='{caps.DeviceName}' aniso={caps.SamplerAnisotropy} lodBias={caps.SamplerLodBias}");
            Assert.False(string.IsNullOrEmpty(caps.DeviceName));   // a real backend reports an adapter name
        }
    }
}
