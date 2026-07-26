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

        // Two things surface the same capability set off the same device: the context and the IGpuDevice the
        // context hands out. They used to be built independently, and the device's copy silently dropped the
        // adapter name and both sampler-feature flags (nothing read them, so nothing caught it). Both now come
        // from one reader; this pins that, so adding a member in one place only cannot go unnoticed again.
        [GpuFact]
        public void ContextAndDeviceReportTheSameCapabilities()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            GpuCapabilities fromContext = ctx.Capabilities;
            GpuCapabilities fromDevice = ctx.GpuDevice.Capabilities;

            Assert.Equal(fromContext.ClipSpaceYInverted, fromDevice.ClipSpaceYInverted);
            Assert.Equal(fromContext.DepthRangeZeroToOne, fromDevice.DepthRangeZeroToOne);
            Assert.Equal(fromContext.DeviceName, fromDevice.DeviceName);
            Assert.Equal(fromContext.SamplerAnisotropy, fromDevice.SamplerAnisotropy);
            Assert.Equal(fromContext.SamplerLodBias, fromDevice.SamplerLodBias);
            Assert.Equal(fromContext.MaxMsaaSampleCount, fromDevice.MaxMsaaSampleCount);
            Assert.Equal(fromContext.SupportsShadowMaps, fromDevice.SupportsShadowMaps);
            Assert.Equal(fromContext.SupportsCompute, fromDevice.SupportsCompute);

            // Metal, Direct3D11 and Vulkan all support compute; this is the only backend set CI runs.
            Assert.True(fromDevice.SupportsCompute, $"{ctx.Backend} reports no compute support");
        }
    }
}
