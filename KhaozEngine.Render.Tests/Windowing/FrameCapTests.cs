using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    // Pure, headless coverage of the backend-aware frame-cap resolution (FrameCap.Resolve) and how it feeds the
    // frame-cap warning. No window / GPU device needed - Resolve takes the display refresh as a plain int.
    public class FrameCapTests
    {
        [Fact]
        public void Default_value_is_Auto()
        {
            Assert.True(default(FrameCap).IsAuto);
            Assert.True(FrameCap.Auto.IsAuto);
            Assert.False(FrameCap.Auto.IsUncapped);
        }

        [Fact]
        public void Hz_factory_makes_a_fixed_cap_and_clamps_non_positive_to_uncapped()
        {
            FrameCap c = FrameCap.Hz(60);
            Assert.False(c.IsAuto);
            Assert.False(c.IsUncapped);
            Assert.Equal(60, c.Value);

            Assert.True(FrameCap.Hz(0).IsUncapped);
            Assert.True(FrameCap.Hz(-5).IsUncapped);
        }

        [Theory]
        [InlineData(GpuBackendKind.MetalNative, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.MetalNative, PresentMode.Immediate)]
        [InlineData(GpuBackendKind.Direct3D11Native, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.VulkanNative, PresentMode.Vsync)]
        public void Fixed_cap_resolves_to_its_value_on_any_backend(GpuBackendKind backend, PresentMode present)
        {
            Assert.Equal(90, FrameCap.Hz(90).Resolve(backend, present, displayRefreshHz: 144));
        }

        [Theory]
        [InlineData(GpuBackendKind.MetalNative, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.Direct3D11Native, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.VulkanNative, PresentMode.Immediate)]
        public void Uncapped_resolves_to_zero_on_any_backend(GpuBackendKind backend, PresentMode present)
        {
            Assert.Equal(0, FrameCap.Uncapped.Resolve(backend, present, displayRefreshHz: 144));
        }

        // Auto resolves to 0 on EVERY kind since 18.0.0, retired members included, because the only backend whose
        // present did not throttle the CPU from vsync alone was the Veldrid Metal incumbent and it was deleted.
        // Rollout gate 5 measured the engine's own MetalNative present on 2026-08-11 and it throttles (the
        // acquire blocks once per frame for 15.175 ms of a 16.669 ms frame, a 120 Hz pinned display paced at 120
        // fps, and vsync off mid-session free-ran past 700 fps with tearing), so it takes the uncapped arm with
        // the other two native backends. Decision M-W3 of docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md.
        [Theory]
        [InlineData(GpuBackendKind.MetalNative)]
        [InlineData(GpuBackendKind.Direct3D11Native)]
        [InlineData(GpuBackendKind.VulkanNative)]
        [InlineData(GpuBackendKind.Metal)]
        [InlineData(GpuBackendKind.Direct3D11)]
        [InlineData(GpuBackendKind.Vulkan)]
        [InlineData(GpuBackendKind.OpenGL)]
        public void Auto_stays_uncapped_on_every_kind_under_vsync(GpuBackendKind backend)
        {
            Assert.Equal(0, FrameCap.Auto.Resolve(backend, PresentMode.Vsync, displayRefreshHz: 144));
            Assert.Equal(0, FrameCap.Auto.Resolve(backend, PresentMode.Vsync, displayRefreshHz: 0));
        }

        [Fact]
        public void Auto_on_Immediate_stays_uncapped()
        {
            // Immediate = the consumer asked for an uncapped lowest-latency present. Auto respects it.
            Assert.Equal(0,
                FrameCap.Auto.Resolve(GpuBackendKind.MetalNative, PresentMode.Immediate, displayRefreshHz: 144));
        }

        // The constant survives the incumbent it was measured for, as public API a consumer can still ask for by
        // hand. What must NOT survive is Resolve reaching for it: an Auto cap that quietly reappeared at 120 Hz
        // on a Mac would look exactly like the pacing bug #380 exists to chase.
        [Fact]
        public void DefaultMetalAutoCapHz_is_still_published_and_no_longer_reached_by_Auto()
        {
            Assert.Equal(120, FrameCap.DefaultMetalAutoCapHz);
            Assert.Equal(0, FrameCap.Auto.Resolve(GpuBackendKind.MetalNative, PresentMode.Vsync, displayRefreshHz: 0));
            Assert.Equal(FrameCap.DefaultMetalAutoCapHz,
                FrameCap.Hz(FrameCap.DefaultMetalAutoCapHz)
                    .Resolve(GpuBackendKind.MetalNative, PresentMode.Vsync, displayRefreshHz: 0));
        }

        // Ties the two pure pieces together: the resolved cap is what the warning rule sees, and since 18.0.0
        // neither the Auto default nor an explicit uncapped choice trips it on any kind. The pair moves together
        // or a consumer is told to set a cap the backend-aware default would not have supplied.
        [Theory]
        [InlineData(GpuBackendKind.MetalNative)]
        [InlineData(GpuBackendKind.Direct3D11Native)]
        [InlineData(GpuBackendKind.VulkanNative)]
        [InlineData(GpuBackendKind.Metal)]
        public void No_resolved_cap_trips_the_frame_cap_warning(GpuBackendKind backend)
        {
            int autoCap = FrameCap.Auto.Resolve(backend, PresentMode.Vsync, displayRefreshHz: 60);
            Assert.False(DisplaySettings.RequiresFrameCapWarning(backend, PresentMode.Vsync, autoCap));

            int uncapped = FrameCap.Uncapped.Resolve(backend, PresentMode.Vsync, displayRefreshHz: 60);
            Assert.False(DisplaySettings.RequiresFrameCapWarning(backend, PresentMode.Vsync, uncapped));
        }
    }
}
