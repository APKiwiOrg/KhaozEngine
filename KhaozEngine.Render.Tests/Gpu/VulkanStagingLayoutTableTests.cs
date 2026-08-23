using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STAGING SUBRESOURCE LAYOUT TABLE (V-C7), and the highest-risk parity surface in the native Vulkan
    /// backend converted into a checked fact BEFORE a single golden runs.
    ///
    /// <para><b>WHY A TABLE AND NOT AN ASSERTION.</b> Every golden in the suite reads back through
    /// <c>IGpuDevice.Map(staging, ...)</c> and consumes <see cref="MappedData.RowPitch"/>. The incumbent backed a
    /// staging texture with a <c>VkBuffer</c> and computed the row pitch, the depth pitch, the array pitch and the
    /// subresource offset IN SOFTWARE, so a different arithmetic here garbles all 36 goldens at once and does it
    /// silently. One draft of the design asserted the rows are tightly packed and moved on, which may well be right
    /// and is the wrong posture either way, because the goldens depend on the arithmetic and not on the
    /// assertion.</para>
    ///
    /// <para><b>PROVENANCE: EVERY NUMBER BELOW WAS PRODUCED BY THE INCUMBENT'S OWN FORMULAS, TRANSCRIBED
    /// INDEPENDENTLY OF THE CODE UNDER TEST.</b> The incumbent was the vendored Veldrid fork this engine shipped
    /// against, <c>4.9.103</c>, whose Vulkan tree is <c>v4.9.0</c>. A throwaway generator transcribed these nine
    /// functions line by line and emitted the rows, so the table is a second derivation rather than a snapshot of
    /// what <see cref="VulkanStagingLayout"/> happens to answer:
    /// <list type="bullet">
    /// <item><description><c>src/Veldrid/FormatSizeHelpers.cs:15</c> <c>GetSizeInBytes</c>, through what
    /// <c>KhaozEngine.Gpu/Internal/VeldridMap.cs</c> lines 13 to 20 held until 18.0.0, which is where
    /// <see cref="GpuPixelFormat.D32FloatS8UInt"/> became FIVE bytes per texel.</description></item>
    /// <item><description><c>src/Veldrid/FormatHelpers.cs:107</c> <c>GetRowPitch</c>, uncompressed arm at line
    /// 133.</description></item>
    /// <item><description><c>src/Veldrid/FormatHelpers.cs:182</c> <c>GetNumRows</c>, uncompressed arm at line
    /// 206.</description></item>
    /// <item><description><c>src/Veldrid/FormatHelpers.cs:210</c> <c>GetDepthPitch</c>.</description></item>
    /// <item><description><c>src/Veldrid/FormatHelpers.cs:215</c> <c>GetRegionSize</c>, uncompressed arm at lines
    /// 227 and 230.</description></item>
    /// <item><description><c>src/Veldrid/Util.cs:153</c> <c>GetDimension</c>, the repeated halving with its
    /// <c>Math.Max(1, ...)</c>.</description></item>
    /// <item><description><c>src/Veldrid/Util.cs:170</c> <c>ComputeMipOffset</c> and
    /// <c>src/Veldrid/Util.cs:185</c> <c>ComputeArrayLayerOffset</c>, summed by
    /// <c>src/Veldrid/Util.cs:164</c> <c>ComputeSubresourceOffset</c>.</description></item>
    /// <item><description><c>src/Veldrid/Vk/VkTexture.cs:146-162</c>, the staging branch of the constructor, for
    /// the WHOLE buffer size.</description></item>
    /// <item><description><c>src/Veldrid/Vk/VkTexture.cs:269</c> <c>GetSubresourceLayout</c>, staging arm at lines
    /// 288-305, whose four assignments are the row pitch, the depth pitch, an array pitch set EQUAL to the depth
    /// pitch, and a size set equal to it too.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para><b>WHAT THE SPREAD COVERS AND WHY.</b> One baseline row per pixel format, so every byte size in the
    /// mapping is exercised including the five-byte one. Full mip sweeps, where the offsets stack. ODD DIMENSIONS,
    /// where the repeated halving reaches 1 and stops, which is the arm a shift-based rewrite would get wrong.
    /// Array layers with and without a mip chain, which is the only place the layer pitch is observable. The
    /// single-texel edge, where every level is 1x1. And three real readback surfaces, including the 1920x1080 one
    /// whose total is eight megabytes.
    /// </para>
    /// </summary>
    public sealed class VulkanStagingLayoutTableTests
    {
        /// <summary>
        /// THE TABLE. Each row is a format, a shape, one subresource of it, and the six numbers the incumbent's
        /// arithmetic produces for that subresource plus the whole buffer's size.
        /// </summary>
        [Theory]
        [InlineData(GpuPixelFormat.R8UNorm, 4, 4, 1, 1, 0, 0, 4, 16, 16, 16, 0, 16)]
        [InlineData(GpuPixelFormat.R8UNorm, 7, 3, 1, 1, 0, 0, 7, 21, 21, 21, 0, 21)]
        [InlineData(GpuPixelFormat.R16G16Float, 4, 4, 1, 1, 0, 0, 16, 64, 64, 64, 0, 64)]
        [InlineData(GpuPixelFormat.R16G16Float, 7, 3, 1, 1, 0, 0, 28, 84, 84, 84, 0, 84)]
        [InlineData(GpuPixelFormat.R32Float, 4, 4, 1, 1, 0, 0, 16, 64, 64, 64, 0, 64)]
        [InlineData(GpuPixelFormat.R32Float, 7, 3, 1, 1, 0, 0, 28, 84, 84, 84, 0, 84)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 4, 4, 1, 1, 0, 0, 16, 64, 64, 64, 0, 64)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 7, 3, 1, 1, 0, 0, 28, 84, 84, 84, 0, 84)]
        [InlineData(GpuPixelFormat.B8G8R8A8UNorm, 4, 4, 1, 1, 0, 0, 16, 64, 64, 64, 0, 64)]
        [InlineData(GpuPixelFormat.B8G8R8A8UNorm, 7, 3, 1, 1, 0, 0, 28, 84, 84, 84, 0, 84)]
        [InlineData(GpuPixelFormat.D24UNormS8UInt, 4, 4, 1, 1, 0, 0, 16, 64, 64, 64, 0, 64)]
        [InlineData(GpuPixelFormat.D24UNormS8UInt, 7, 3, 1, 1, 0, 0, 28, 84, 84, 84, 0, 84)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 4, 4, 1, 1, 0, 0, 20, 80, 80, 80, 0, 80)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 7, 3, 1, 1, 0, 0, 35, 105, 105, 105, 0, 105)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, 4, 4, 1, 1, 0, 0, 32, 128, 128, 128, 0, 128)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, 7, 3, 1, 1, 0, 0, 56, 168, 168, 168, 0, 168)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 16, 16, 5, 1, 0, 0, 64, 1024, 1024, 1024, 0, 1364)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 16, 16, 5, 1, 1, 0, 32, 256, 256, 256, 1024, 1364)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 16, 16, 5, 1, 2, 0, 16, 64, 64, 64, 1280, 1364)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 16, 16, 5, 1, 3, 0, 8, 16, 16, 16, 1344, 1364)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 16, 16, 5, 1, 4, 0, 4, 4, 4, 4, 1360, 1364)]
        [InlineData(GpuPixelFormat.R8UNorm, 16, 16, 5, 1, 0, 0, 16, 256, 256, 256, 0, 341)]
        [InlineData(GpuPixelFormat.R8UNorm, 16, 16, 5, 1, 1, 0, 8, 64, 64, 64, 256, 341)]
        [InlineData(GpuPixelFormat.R8UNorm, 16, 16, 5, 1, 2, 0, 4, 16, 16, 16, 320, 341)]
        [InlineData(GpuPixelFormat.R8UNorm, 16, 16, 5, 1, 3, 0, 2, 4, 4, 4, 336, 341)]
        [InlineData(GpuPixelFormat.R8UNorm, 16, 16, 5, 1, 4, 0, 1, 1, 1, 1, 340, 341)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 16, 16, 5, 1, 0, 0, 80, 1280, 1280, 1280, 0, 1705)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 16, 16, 5, 1, 1, 0, 40, 320, 320, 320, 1280, 1705)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 16, 16, 5, 1, 2, 0, 20, 80, 80, 80, 1600, 1705)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 16, 16, 5, 1, 3, 0, 10, 20, 20, 20, 1680, 1705)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 16, 16, 5, 1, 4, 0, 5, 5, 5, 5, 1700, 1705)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 7, 3, 4, 1, 0, 0, 28, 84, 84, 84, 0, 104)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 7, 3, 4, 1, 1, 0, 12, 12, 12, 12, 84, 104)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 7, 3, 4, 1, 2, 0, 4, 4, 4, 4, 96, 104)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 7, 3, 4, 1, 3, 0, 4, 4, 4, 4, 100, 104)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, 100, 60, 4, 1, 0, 0, 800, 48000, 48000, 48000, 0, 63672)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, 100, 60, 4, 1, 1, 0, 400, 12000, 12000, 12000, 48000, 63672)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, 100, 60, 4, 1, 2, 0, 200, 3000, 3000, 3000, 60000, 63672)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, 100, 60, 4, 1, 3, 0, 96, 672, 672, 672, 63000, 63672)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 1, 4, 0, 0, 32, 256, 256, 256, 0, 1024)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 1, 4, 0, 1, 32, 256, 256, 256, 256, 1024)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 1, 4, 0, 2, 32, 256, 256, 256, 512, 1024)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 1, 4, 0, 3, 32, 256, 256, 256, 768, 1024)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 0, 0, 32, 256, 256, 256, 0, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 1, 0, 16, 64, 64, 64, 256, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 2, 0, 8, 16, 16, 16, 320, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 3, 0, 4, 4, 4, 4, 336, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 0, 1, 32, 256, 256, 256, 340, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 1, 1, 16, 64, 64, 64, 596, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 2, 1, 8, 16, 16, 16, 660, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 3, 1, 4, 4, 4, 4, 676, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 0, 2, 32, 256, 256, 256, 680, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 1, 2, 16, 64, 64, 64, 936, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 2, 2, 8, 16, 16, 16, 1000, 1020)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 8, 8, 4, 3, 3, 2, 4, 4, 4, 4, 1016, 1020)]
        [InlineData(GpuPixelFormat.R8UNorm, 5, 5, 3, 2, 2, 0, 1, 1, 1, 1, 29, 60)]
        [InlineData(GpuPixelFormat.R8UNorm, 5, 5, 3, 2, 2, 1, 1, 1, 1, 1, 59, 60)]
        [InlineData(GpuPixelFormat.R32Float, 1, 1, 3, 1, 0, 0, 4, 4, 4, 4, 0, 12)]
        [InlineData(GpuPixelFormat.R32Float, 1, 1, 3, 1, 1, 0, 4, 4, 4, 4, 4, 12)]
        [InlineData(GpuPixelFormat.R32Float, 1, 1, 3, 1, 2, 0, 4, 4, 4, 4, 8, 12)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 640, 360, 1, 1, 0, 0, 2560, 921600, 921600, 921600, 0, 921600)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 1000, 1000, 1, 1, 0, 0, 4000, 4000000, 4000000, 4000000, 0, 4000000)]
        [InlineData(GpuPixelFormat.B8G8R8A8UNorm, 1920, 1080, 1, 1, 0, 0, 7680, 8294400, 8294400, 8294400, 0, 8294400)]
        public void TheSoftwareSubresourceLayout_MatchesTheIncumbentsOwnArithmetic(
            GpuPixelFormat format, uint width, uint height, uint mipLevels, uint arrayLayers, uint mipLevel,
            uint arrayLayer, ulong rowPitch, ulong depthPitch, ulong arrayPitch, ulong size, ulong offset,
            ulong totalBytes)
        {
            var shape = new VulkanStagingShape(width, height, mipLevels, arrayLayers, format);

            VulkanSubresourceLayout layout = VulkanStagingLayout.For(shape, mipLevel, arrayLayer);

            Assert.Equal(rowPitch, layout.RowPitch);
            Assert.Equal(depthPitch, layout.DepthPitch);
            Assert.Equal(arrayPitch, layout.ArrayPitch);
            Assert.Equal(size, layout.Size);
            Assert.Equal(offset, layout.Offset);
            Assert.Equal(totalBytes, VulkanStagingLayout.TotalBytes(shape));
        }

        /// <summary>
        /// THE ARRAY PITCH IS THE DEPTH PITCH, which is the one field of the incumbent's four that looks like a
        /// mistake and is not. It sets <c>arrayPitch = depthPitch</c> for a staging texture, so the field does NOT
        /// mean "distance to the next array layer" the way its name suggests, and the real distance between layers
        /// is the LAYER PITCH, which is the summed size of every mip level. Reproducing the name's obvious meaning
        /// instead would place every layer after the first at the wrong offset on any mipped texture.
        /// </summary>
        [Fact]
        public void TheArrayPitch_IsTheDepthPitch_AndIsNotTheDistanceBetweenLayers()
        {
            var shape = new VulkanStagingShape(8, 8, 4, 3, GpuPixelFormat.R8G8B8A8UNorm);

            VulkanSubresourceLayout level0 = VulkanStagingLayout.For(shape, 0, 0);
            Assert.Equal(level0.DepthPitch, level0.ArrayPitch);

            // The real distance between layers, which is 340 bytes for this shape rather than the 256 the array
            // pitch reports, and which the offsets in the table above are built out of.
            ulong layerPitch = VulkanStagingLayout.For(shape, 0, 1).Offset - level0.Offset;
            Assert.Equal(340UL, layerPitch);
            Assert.NotEqual(layerPitch, level0.ArrayPitch);
        }

        /// <summary>
        /// THE MIP DIMENSION IS A REPEATED HALVING WITH A FLOOR OF 1, not a shift, and the two agree for every
        /// level a real texture has. The loop is reproduced rather than replaced because it keeps answering 1 at
        /// level counts where a shift is undefined, and because agreeing with the incumbent is this type's whole
        /// job.
        /// </summary>
        [Theory]
        [InlineData(16u, 0u, 16u)]
        [InlineData(16u, 4u, 1u)]
        [InlineData(16u, 9u, 1u)]
        [InlineData(7u, 1u, 3u)]
        [InlineData(7u, 2u, 1u)]
        [InlineData(7u, 3u, 1u)]
        [InlineData(1u, 5u, 1u)]
        [InlineData(1000u, 3u, 125u)]
        [InlineData(1000u, 4u, 62u)]
        public void TheMipDimension_HalvesAndFloorsAtOne(uint largest, uint mipLevel, uint expected)
            => Assert.Equal(expected, VulkanStagingLayout.MipDimension(largest, mipLevel));

        /// <summary>
        /// EVERY FORMAT'S BYTE SIZE IS PINNED, including the one a reader will doubt.
        /// <see cref="GpuPixelFormat.D32FloatS8UInt"/> is FIVE bytes here. The real
        /// <c>VK_FORMAT_D32_SFLOAT_S8_UINT</c> image is eight bytes per texel with the stencil in its own plane,
        /// and this number is not about that image: it is the incumbent's software layout for the staging buffer
        /// that mirrors it, and the goldens read back through that stride rather than through the driver's.
        /// </summary>
        [Theory]
        [InlineData(GpuPixelFormat.R8UNorm, 1u)]
        [InlineData(GpuPixelFormat.R16G16Float, 4u)]
        [InlineData(GpuPixelFormat.R32Float, 4u)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, 4u)]
        [InlineData(GpuPixelFormat.B8G8R8A8UNorm, 4u)]
        [InlineData(GpuPixelFormat.D24UNormS8UInt, 4u)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, 5u)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, 8u)]
        public void EveryFormatsByteSize_IsTheIncumbents(GpuPixelFormat format, uint bytes)
            => Assert.Equal(bytes, VulkanStagingLayout.BytesPerTexel(format));

        /// <summary>
        /// SUBRESOURCES RUN MIP-MAJOR WITHIN A LAYER, which is what <c>Util.GetMipLevelAndArrayLayer</c> encodes
        /// and what a copy that walks a texture's subresources depends on. Round-tripped both ways, because an
        /// index scheme is only useful if the two directions agree.
        /// </summary>
        [Theory]
        [InlineData(0u, 4u, 0u, 0u)]
        [InlineData(3u, 4u, 3u, 0u)]
        [InlineData(4u, 4u, 0u, 1u)]
        [InlineData(9u, 4u, 1u, 2u)]
        public void TheSubresourceIndex_IsMipMajorWithinALayer(uint subresource, uint mipLevels, uint mipLevel,
            uint arrayLayer)
        {
            VulkanStagingLayout.MipLevelAndArrayLayer(subresource, mipLevels, out uint mip, out uint layer);

            Assert.Equal(mipLevel, mip);
            Assert.Equal(arrayLayer, layer);
            Assert.Equal(subresource, VulkanStagingLayout.Subresource(mip, layer, mipLevels));
        }

        /// <summary>
        /// A COPY REGION NAMES ITS BUFFER ROWS IN TEXELS, NOT BYTES, which is the mistake most available here: the
        /// layout above is entirely in bytes and <c>VkBufferImageCopy.bufferRowLength</c> is not.
        /// <c>VkCommandList</c> computes it as the staging mip's own width, and the extent is CLAMPED to the mip's
        /// dimensions so a caller asking for more than the mip holds gets the mip rather than a copy running off
        /// the end of the image.
        /// </summary>
        [Fact]
        public void ACopyRegion_CountsBufferRowsInTexels_AndClampsToTheMip()
        {
            var shape = new VulkanStagingShape(16, 16, 5, 2, GpuPixelFormat.R8G8B8A8UNorm);

            VulkanBufferImageCopy region = VulkanStagingLayout.CopyRegion(shape, 2, 1, 1, 2, 99, 99);

            // Mip 2 of a 16x16 texture is 4x4, so the buffer rows are FOUR texels and the extent clamps to 4x4.
            Assert.Equal(4u, region.BufferRowLength);
            Assert.Equal(4u, region.BufferImageHeight);
            Assert.Equal(4u, region.Width);
            Assert.Equal(4u, region.Height);

            // The offset is the subresource's own plus two rows of 16 bytes plus one texel of 4.
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(shape, 2, 1);
            Assert.Equal(layout.Offset + (2 * layout.RowPitch) + 4, region.BufferOffset);
        }

        /// <summary>
        /// AN UPLOAD REGION IS TIGHTLY PACKED AND IS THE OTHER SHAPE. Its source is a lease in the staging arena
        /// rather than a staging texture's subresource, so its rows are the REGION's own width and nothing is
        /// clamped: the caller's bytes describe exactly the rectangle they asked to write.
        /// </summary>
        [Fact]
        public void AnUploadRegion_IsTightlyPackedAtTheLeaseOffset()
        {
            VulkanBufferImageCopy region = VulkanStagingLayout.UploadRegion(4096, 1, 2, 8, 16, 32, 64);

            Assert.Equal(4096UL, region.BufferOffset);
            Assert.Equal(32u, region.BufferRowLength);
            Assert.Equal(64u, region.BufferImageHeight);
            Assert.Equal(1u, region.MipLevel);
            Assert.Equal(2u, region.ArrayLayer);
            Assert.Equal(8u, region.X);
            Assert.Equal(16u, region.Y);
            Assert.Equal(32u, region.Width);
            Assert.Equal(64u, region.Height);
        }

        /// <summary>An upload's payload is one row pitch per row with no padding, which is what the seam's
        /// <c>byte[]</c> overloads carry.</summary>
        [Fact]
        public void TheRequiredUploadBytes_AreTightlyPackedRows()
        {
            Assert.Equal(64UL * 32 * 4,
                VulkanStagingLayout.RequiredUploadBytes(64, 32, GpuPixelFormat.R8G8B8A8UNorm));
            Assert.Equal(64UL * 32 * 5,
                VulkanStagingLayout.RequiredUploadBytes(64, 32, GpuPixelFormat.D32FloatS8UInt));
        }

        /// <summary>
        /// A SUBRESOURCE OUTSIDE THE TEXTURE IS REFUSED rather than answered with an offset that lands in whatever
        /// follows the buffer. The incumbent's arithmetic has no bound check at all: it would compute a plausible
        /// number for mip 9 of a 3-mip texture and hand back a pointer into the next allocation.
        /// </summary>
        [Fact]
        public void ASubresourceOutsideTheTexture_IsRefused()
        {
            var shape = new VulkanStagingShape(8, 8, 3, 2, GpuPixelFormat.R8G8B8A8UNorm);

            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanStagingLayout.For(shape, 3, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanStagingLayout.For(shape, 0, 2));
        }

        /// <summary>
        /// A ZERO IN ANY DIMENSION IS REFUSED, because it produces a zero-byte buffer and <c>vkCreateBuffer</c>
        /// rejects that outright.
        /// </summary>
        [Theory]
        [InlineData(0u, 4u, 1u, 1u)]
        [InlineData(4u, 0u, 1u, 1u)]
        [InlineData(4u, 4u, 0u, 1u)]
        [InlineData(4u, 4u, 1u, 0u)]
        public void AZeroDimension_IsRefused(uint width, uint height, uint mipLevels, uint arrayLayers)
        {
            var shape = new VulkanStagingShape(width, height, mipLevels, arrayLayers,
                GpuPixelFormat.R8G8B8A8UNorm);

            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanStagingLayout.TotalBytes(shape));
        }

        /// <summary>
        /// A STAGING TEXTURE TOO LARGE FOR A 32-BIT SIZE IS REFUSED BY NAME. The incumbent computes this in 32
        /// bits throughout and WRAPS silently, which sizes the buffer far too small and corrupts whatever follows
        /// it. Every value below that bound is identical to the incumbent's, which is what the table above is.
        /// This is the one place the two deliberately differ, in the direction of saying so.
        /// </summary>
        [Fact]
        public void AStagingTextureTooLargeForTheSeamsSize_IsRefusedRatherThanWrapped()
        {
            // 40000 x 40000 at four bytes is 6.4 GB, which does not fit MappedData.SizeInBytes.
            var shape = new VulkanStagingShape(40000, 40000, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);

            ArgumentOutOfRangeException ex =
                Assert.Throws<ArgumentOutOfRangeException>(() => VulkanStagingLayout.TotalBytes(shape));

            Assert.Contains("32-bit", ex.Message, StringComparison.Ordinal);
        }
    }
}
