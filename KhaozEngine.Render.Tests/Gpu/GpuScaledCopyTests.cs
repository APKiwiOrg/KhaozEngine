using System;
using System.Collections.Generic;
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

        [Fact]
        public void A_replaced_set_is_not_disposed_during_the_recording_that_used_it()
        {
            // With retire: the first source's set goes to retire exactly once, and only then.
            using (var device = new FakeGpuDevice())
            {
                var factory = (FakeGpuResourceFactory)device.Factory;
                var retired = new List<IDisposable>();
                var copy = new GpuScaledCopy(device);
                using IGpuTexture first = Source(device);
                using IGpuTexture second = Source(device);
                using IGpuTexture target = Target(device);
                using IGpuFramebuffer destination = device.Factory.CreateFramebuffer(null, target);
                using IGpuCommandList commands = device.Factory.CreateCommandList();

                commands.Begin();
                copy.Record(commands, first, destination, retired.Add);
                FakeResourceSet firstSet = Assert.Single(factory.ResourceSets);
                copy.Record(commands, second, destination, retired.Add);
                commands.End();

                Assert.False(firstSet.Disposed, "the first set was disposed before its recording was submitted");
                Assert.Same(firstSet, Assert.Single(retired));
                device.Submit(commands);
                Assert.False(firstSet.Disposed);

                copy.Dispose();
                Assert.Single(retired);
                Assert.False(firstSet.Disposed, "GpuScaledCopy.Dispose freed a set it had already handed to retire");
                Assert.True(factory.ResourceSets[1].Disposed);
            }

            // Without retire: the first set stays alive until GpuScaledCopy.Dispose.
            using (var device = new FakeGpuDevice())
            {
                var factory = (FakeGpuResourceFactory)device.Factory;
                var copy = new GpuScaledCopy(device);
                using IGpuTexture first = Source(device);
                using IGpuTexture second = Source(device);
                using IGpuTexture target = Target(device);
                using IGpuFramebuffer destination = device.Factory.CreateFramebuffer(null, target);
                using IGpuCommandList commands = device.Factory.CreateCommandList();

                commands.Begin();
                copy.Record(commands, first, destination);
                copy.Record(commands, second, destination);
                commands.End();
                device.Submit(commands);

                Assert.Equal(2, factory.ResourceSets.Count);
                Assert.False(factory.ResourceSets[0].Disposed, "the first set was disposed before Dispose");

                copy.Dispose();
                Assert.True(factory.ResourceSets[0].Disposed);
                Assert.True(factory.ResourceSets[1].Disposed);
            }
        }

        static IGpuTexture Source(FakeGpuDevice device) => device.Factory.CreateTexture(
            GpuTextureDescription.Texture2D(64, 32, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));

        static IGpuTexture Target(FakeGpuDevice device) => device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
    }
}
