using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SHAPE <c>GpuReadback.ToRgba</c> REQUIRES OF ITS SOURCE, REFUSED AT THE READBACK
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/83">#83</see>). The readback allocates a
    /// single-mip, single-sample <c>R8G8B8A8UNorm</c> staging texture of the size it was asked for, whole-texture
    /// copies into it and de-strides at a fixed four bytes per texel. A whole copy names every subresource on both
    /// sides, so a source that disagrees on format, mip count, sample count or size is not a copy any backend can
    /// narrow.
    ///
    /// <para><b>WHY THE CHECK IS NOT LEFT TO THE BACKENDS.</b> Native Metal and Vulkan refuse a mismatched whole
    /// copy from their own <c>RequireMatchingShape</c>. Direct3D 11's <c>CopyResource</c> does not, so before this
    /// the same call threw on two backends and read back channel-swapped or garbage bytes on the third. The three
    /// agree now, and the message names the readback and the source's actual format and mip count rather than a
    /// copy the caller never wrote.</para>
    ///
    /// <para>Device-free on purpose: the guard reads only what is on the texture handle, so it runs before anything
    /// is allocated, recorded or submitted, and this covers it on the ordinary push-path suite rather than only on
    /// a leg with a GPU. Same shape as <c>CopyBufferOffsetContractTests</c>, which pins the sibling copy rule.</para>
    /// </summary>
    public sealed class GpuReadbackSourceShapeTests : IDisposable
    {
        const uint W = 64, H = 32;

        readonly FakeGpuDevice _device = new();

        /// <inheritdoc/>
        public void Dispose() => _device.Dispose();

        IGpuTexture Texture(GpuPixelFormat format = GpuPixelFormat.R8G8B8A8UNorm, uint mips = 1,
            uint samples = 1, uint w = W, uint h = H)
            => _device.Factory.CreateTexture(new GpuTextureDescription(
                w, h, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, mips, 1, samples));

        /// <summary>
        /// The half the incumbent backends were silent about: a swapchain-format source read back as RGBA8. The
        /// refusal has to NAME the format, because "the copy failed" sends the caller to inspect the copy.
        /// </summary>
        [Fact]
        public void ABgraSource_IsRefused_AndTheMessageNamesTheFormat()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => GpuReadback.ToRgba(_device, Texture(GpuPixelFormat.B8G8R8A8UNorm), (int)W, (int)H));

            Assert.Equal("src", ex.ParamName);
            Assert.Contains("GpuReadback.ToRgba", ex.Message);
            Assert.Contains("B8G8R8A8UNorm", ex.Message);
        }

        /// <summary>A mipped source, with the mip count in the message and the per-level readback named as the
        /// way out.</summary>
        [Fact]
        public void AMippedSource_IsRefused_AndTheMessageNamesTheMipCount()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => GpuReadback.ToRgba(_device, Texture(mips: 4), (int)W, (int)H));

            Assert.Equal("src", ex.ParamName);
            Assert.Contains("4 mip level(s)", ex.Message);
            Assert.Contains("ToRgbaMip", ex.Message);
        }

        /// <summary>A multisampled source, which no backend can whole-copy into a single-sample staging texture.
        /// The resolve is the way out and the message says so.</summary>
        [Fact]
        public void AMultisampledSource_IsRefused()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => GpuReadback.ToRgba(_device, Texture(samples: 4), (int)W, (int)H));

            Assert.Equal("src", ex.ParamName);
            Assert.Contains("4 sample(s)", ex.Message);
            Assert.Contains("ResolveTexture", ex.Message);
        }

        /// <summary>A size the source does not have. The staging texture is built from the CALLER's numbers, so a
        /// mismatch here is the same unnarrowable whole copy, and both shapes belong in the message.</summary>
        [Fact]
        public void ASizeTheSourceDoesNotHave_IsRefused()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => GpuReadback.ToRgba(_device, Texture(), (int)W / 2, (int)H));

            Assert.Equal("src", ex.ParamName);
            Assert.Contains("32x32", ex.Message);   // what was asked for
            Assert.Contains("64x32", ex.Message);   // what the source is
        }

        /// <summary>
        /// AND THE GUARD DOES NOT REFUSE THE ORDINARY READBACK. Without this row a guard that rejected everything
        /// would satisfy the four above. The fake has no pixels to map, so a source of the right shape gets all the
        /// way past the guard, the staging allocation and the copy, and stops at the map it cannot serve.
        /// </summary>
        [Fact]
        public void AMatchingSource_ReachesTheMap()
        {
            Assert.Throws<NotSupportedException>(
                () => GpuReadback.ToRgba(_device, Texture(), (int)W, (int)H));
        }

        /// <summary>A null source is still an ArgumentNullException, named, rather than a dereference deeper in.</summary>
        [Fact]
        public void ANullSource_IsRefusedByName()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => GpuReadback.ToRgba(_device, null!, (int)W, (int)H));

            Assert.Equal("src", ex.ParamName);
        }
    }
}
