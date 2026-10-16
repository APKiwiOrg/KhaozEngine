using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The device-free half of <see cref="GpuScaledCopy"/>: its shader pair compiles and cross-compiles
    /// through the shipped toolchain, and a destination its pipeline cannot render into is refused by name before
    /// anything is recorded. The pixels are pinned by <see cref="GpuScaledCopyGpuTests"/>.</summary>
    public sealed class GpuScaledCopyTests
    {
        [Fact]
        public void The_sources_validate_through_the_shipped_toolchain()
            => ShaderValidation.ValidatePair(GpuScaledCopy.VertexSource, GpuScaledCopy.FragmentSource, "GpuScaledCopy");

        [Fact]
        public void A_destination_that_is_not_one_RGBA8_attachment_is_refused()
        {
            using var device = new FakeGpuDevice();
            IGpuResourceFactory f = device.Factory;
            using var copy = new GpuScaledCopy(device);
            using IGpuTexture source = f.CreateTexture(GpuTextureDescription.Texture2D(
                64, 32, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuTexture bgra = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 8, GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer destination = f.CreateFramebuffer(null, bgra);
            using IGpuCommandList commands = f.CreateCommandList();

            ArgumentException refused = Assert.Throws<ArgumentException>(
                () => copy.Record(commands, source, destination));

            Assert.Equal("destination", refused.ParamName);
        }
    }
}
