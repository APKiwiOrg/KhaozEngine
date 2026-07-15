using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    // Pure, headless coverage of the backend-aware frame-cap resolution (FrameCap.Resolve) and how it feeds the
    // Metal-vsync warning. No window / GPU device needed - Resolve takes the display refresh as a plain int.
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
        [InlineData(GpuBackendKind.Metal, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.Metal, PresentMode.Immediate)]
        [InlineData(GpuBackendKind.Direct3D11, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.Vulkan, PresentMode.Vsync)]
        public void Fixed_cap_resolves_to_its_value_on_any_backend(GpuBackendKind backend, PresentMode present)
        {
            Assert.Equal(90, FrameCap.Hz(90).Resolve(backend, present, displayRefreshHz: 144));
        }

        [Theory]
        [InlineData(GpuBackendKind.Metal, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.Direct3D11, PresentMode.Vsync)]
        [InlineData(GpuBackendKind.Vulkan, PresentMode.Immediate)]
        public void Uncapped_resolves_to_zero_on_any_backend(GpuBackendKind backend, PresentMode present)
        {
            Assert.Equal(0, FrameCap.Uncapped.Resolve(backend, present, displayRefreshHz: 144));
        }

        [Fact]
        public void Auto_on_Metal_vsync_uses_the_display_refresh_when_known()
        {
            Assert.Equal(144, FrameCap.Auto.Resolve(GpuBackendKind.Metal, PresentMode.Vsync, displayRefreshHz: 144));
            Assert.Equal(60, FrameCap.Auto.Resolve(GpuBackendKind.Metal, PresentMode.Vsync, displayRefreshHz: 60));
        }

        [Fact]
        public void Auto_on_Metal_vsync_falls_back_to_the_default_cap_when_refresh_unknown()
        {
            Assert.Equal(FrameCap.DefaultMetalAutoCapHz,
                FrameCap.Auto.Resolve(GpuBackendKind.Metal, PresentMode.Vsync, displayRefreshHz: 0));
            Assert.Equal(120, FrameCap.DefaultMetalAutoCapHz);
        }

        [Fact]
        public void Auto_on_Metal_Immediate_stays_uncapped()
        {
            // Immediate = the consumer asked for an uncapped lowest-latency present; Auto respects it.
            Assert.Equal(0, FrameCap.Auto.Resolve(GpuBackendKind.Metal, PresentMode.Immediate, displayRefreshHz: 144));
        }

        [Theory]
        [InlineData(GpuBackendKind.Direct3D11)]
        [InlineData(GpuBackendKind.Vulkan)]
        public void Auto_on_D3D11_or_Vulkan_vsync_stays_uncapped_because_vsync_throttles(GpuBackendKind backend)
        {
            Assert.Equal(0, FrameCap.Auto.Resolve(backend, PresentMode.Vsync, displayRefreshHz: 144));
        }

        // Ties the two pure pieces together: the resolved cap is what the warning rule sees. The Auto default never
        // trips the Metal-vsync warning (it resolves to a positive cap there); only an explicit uncapped choice does.
        [Fact]
        public void Resolved_Auto_default_does_not_trip_the_metal_vsync_warning()
        {
            int autoCap = FrameCap.Auto.Resolve(GpuBackendKind.Metal, PresentMode.Vsync, displayRefreshHz: 60);
            Assert.False(DisplaySettings.RequiresFrameCapWarning(GpuBackendKind.Metal, PresentMode.Vsync, autoCap));
        }

        [Fact]
        public void Resolved_explicit_uncapped_still_trips_the_metal_vsync_warning()
        {
            int uncapped = FrameCap.Uncapped.Resolve(GpuBackendKind.Metal, PresentMode.Vsync, displayRefreshHz: 60);
            Assert.True(DisplaySettings.RequiresFrameCapWarning(GpuBackendKind.Metal, PresentMode.Vsync, uncapped));
        }
    }
}
