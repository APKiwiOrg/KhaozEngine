using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The runtime present-mode apply path against a real device: flipping <see cref="IGpuDevice.SyncToVerticalBlank"/>
    /// round-trips and never throws. Headless (no swapchain) so it runs on the GPU CI matrix; the setter's in-place
    /// swapchain reconfigure (no recreate, no leak) is exercised live by the windowed smoke sample. This is the
    /// headless-testable half of the "flip vsync mid-session with no crash" acceptance.
    /// </summary>
    public sealed class SyncToVerticalBlankGpuTests
    {
        [GpuFact]
        public void SyncToVerticalBlank_round_trips_and_does_not_throw_headless()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;

            // Flip both ways repeatedly; the getter must reflect the last set and nothing may throw even though
            // there is no main swapchain to reconfigure on a headless device.
            device.SyncToVerticalBlank = false;
            Assert.False(device.SyncToVerticalBlank);

            device.SyncToVerticalBlank = true;
            Assert.True(device.SyncToVerticalBlank);

            device.SyncToVerticalBlank = false;
            Assert.False(device.SyncToVerticalBlank);
        }
    }
}
