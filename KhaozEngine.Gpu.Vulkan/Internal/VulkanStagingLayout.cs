using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>The shape a staging texture's layout is computed from: everything
    /// <see cref="GpuTextureDescription"/> carries that the arithmetic reads, and nothing else.</summary>
    /// <param name="Width">Texel width of mip 0.</param>
    /// <param name="Height">Texel height of mip 0.</param>
    /// <param name="MipLevels">How many mip levels, at least 1.</param>
    /// <param name="ArrayLayers">How many array layers, at least 1.</param>
    /// <param name="Format">The pixel format, which supplies the bytes per texel.</param>
    internal readonly record struct VulkanStagingShape(
        uint Width, uint Height, uint MipLevels, uint ArrayLayers, GpuPixelFormat Format);

    /// <summary>One subresource's place in the staging buffer, the engine's own
    /// <c>VkSubresourceLayout</c>.</summary>
    /// <param name="Offset">Where the subresource starts, in bytes from the buffer's first byte.</param>
    /// <param name="RowPitch">Bytes between consecutive rows, which is what <see cref="MappedData.RowPitch"/>
    /// carries and what every golden de-strides with.</param>
    /// <param name="DepthPitch">Bytes for one whole depth slice, which for a 2D texture is the whole
    /// subresource.</param>
    /// <param name="ArrayPitch">Bytes between array layers OF THIS SUBRESOURCE. Equal to
    /// <paramref name="DepthPitch"/>, exactly as the incumbent sets it.</param>
    /// <param name="Size">How many bytes the subresource occupies, which is what
    /// <see cref="MappedData.SizeInBytes"/> carries.</param>
    internal readonly record struct VulkanSubresourceLayout(
        ulong Offset, ulong RowPitch, ulong DepthPitch, ulong ArrayPitch, ulong Size);

    /// <summary>One <c>VkBufferImageCopy</c> region, as plain numbers.</summary>
    /// <param name="BufferOffset">The first byte of the region inside the staging buffer.</param>
    /// <param name="BufferRowLength">The staging rows' length in TEXELS, not bytes.</param>
    /// <param name="BufferImageHeight">The staging image's height in texels.</param>
    /// <param name="MipLevel">Which mip level of the image the region touches.</param>
    /// <param name="ArrayLayer">Which array layer.</param>
    /// <param name="X">Left edge of the touched region in the image.</param>
    /// <param name="Y">Top edge.</param>
    /// <param name="Width">Width of the touched region, clamped to the mip's own width.</param>
    /// <param name="Height">Height of the touched region, clamped to the mip's own height.</param>
    internal readonly record struct VulkanBufferImageCopy(
        ulong BufferOffset, uint BufferRowLength, uint BufferImageHeight, uint MipLevel, uint ArrayLayer,
        uint X, uint Y, uint Width, uint Height);

    /// <summary>
    /// THE INCUMBENT'S SOFTWARE SUBRESOURCE LAYOUT, REPRODUCED BYTE FOR BYTE. Decision V-C7, section 13.
    ///
    /// <para><b>THIS IS THE HIGHEST-RISK PARITY SURFACE IN THE BACKEND.</b> Every golden in the suite reads back
    /// through <c>IGpuDevice.Map(staging, ...)</c> and consumes <see cref="MappedData.RowPitch"/>, so a DIFFERENT
    /// arithmetic here produces a garbled grid on every scene at once. Nothing about that failure is loud: the
    /// readback succeeds, the pointer is valid, and the pixels are simply in the wrong places.</para>
    ///
    /// <para><b>THE INCUMBENT BACKS A STAGING TEXTURE WITH A <c>VkBuffer</c> AND COMPUTES THE LAYOUT IN
    /// SOFTWARE.</b> Not a linear-tiled image, and not <c>vkGetImageSubresourceLayout</c>: the row pitch, the depth
    /// pitch, the array pitch and the subresource offset are all engine arithmetic. Reproducing that is therefore
    /// reproducing ARITHMETIC rather than agreeing with a driver, which is the one reason this can be pinned at all
    /// without a device.</para>
    ///
    /// <para><b>EVERY FORMULA BELOW CITES ITS SOURCE.</b> The incumbent is the vendored Veldrid fork this engine
    /// ships against (<c>4.9.103</c>, Vulkan tree <c>v4.9.0</c>), and the six functions this reproduces are
    /// <c>FormatSizeHelpers.GetSizeInBytes</c>, <c>FormatHelpers.GetRowPitch</c>, <c>FormatHelpers.GetNumRows</c>,
    /// <c>FormatHelpers.GetDepthPitch</c>, <c>FormatHelpers.GetRegionSize</c> and <c>Util.GetDimension</c>, plus the
    /// three that compose them: <c>Util.ComputeMipOffset</c>, <c>Util.ComputeArrayLayerOffset</c> and
    /// <c>Util.ComputeSubresourceOffset</c>. The two call sites that USE them are <c>VkTexture</c>'s staging branch
    /// (the total size) and <c>VkTexture.GetSubresourceLayout</c>'s staging branch (the layout).
    /// <c>VulkanStagingLayoutTableTests</c> carries the checked-in table those formulas produce, one row per
    /// format, size, mip level and array layer, and asserts this type against it.</para>
    ///
    /// <para><b>THE COMPRESSED BRANCH IS DELIBERATELY ABSENT rather than reproduced.</b>
    /// <see cref="GpuPixelFormat"/> has eight members and not one of them is a block-compressed format, so the
    /// incumbent's <c>blockSize</c> is 1 at every site this backend can reach and its
    /// <c>(width + 3) / 4</c> row arithmetic is unreachable code. Carrying an unreachable branch would be carrying
    /// a second thing to keep correct with no way to test it. If the seam ever gains a compressed format, the
    /// branch comes back HERE, with the incumbent's own version as the reference, and the table gains its rows.</para>
    ///
    /// <para><b>DEPTH IS ALWAYS 1</b>, because <see cref="GpuTextureDescription"/> has no depth at all: the seam
    /// expresses 2D textures, 2D arrays and cubemaps and nothing else. The incumbent's depth terms are therefore
    /// multiplications by one, kept visible in the formulas below rather than folded away, so a reader comparing
    /// the two sources sees the same shape.</para>
    ///
    /// <para><b>ARITHMETIC IS IN 64 BITS AND THE RESULT IS REFUSED ABOVE 32.</b> The incumbent computes in
    /// <c>uint</c> throughout and wraps silently on a texture large enough to overflow, which would create a
    /// staging buffer far too small and corrupt whatever sits after it. Every value this type produces is identical
    /// to the incumbent's everywhere the incumbent does not wrap, and above that it throws by name. That is a
    /// guard rather than a divergence: <see cref="MappedData.SizeInBytes"/> is a <c>uint</c>, so a staging texture
    /// past this bound cannot be described through the seam at all.</para>
    /// </summary>
    internal static class VulkanStagingLayout
    {
        /// <summary>
        /// Bytes per texel. <c>FormatSizeHelpers.GetSizeInBytes</c>, restricted to the eight formats the seam has.
        /// <para>
        /// <b><see cref="GpuPixelFormat.D32FloatS8UInt"/> IS FIVE BYTES, AND IT IS THE ONE VALUE A READER WILL
        /// DOUBT.</b> The real <c>VK_FORMAT_D32_SFLOAT_S8_UINT</c> image is eight bytes per texel with the stencil
        /// in a separate plane, and this number is not about that image. It is the incumbent's SOFTWARE layout for
        /// the staging buffer that mirrors it, and reproducing it is the whole point of this type: the goldens read
        /// back through the incumbent's stride, not through the driver's.
        /// </para>
        /// </summary>
        internal static uint BytesPerTexel(GpuPixelFormat format) => format switch
        {
            GpuPixelFormat.R8UNorm => 1,
            GpuPixelFormat.R16G16Float => 4,
            GpuPixelFormat.R32Float => 4,
            GpuPixelFormat.R8G8B8A8UNorm => 4,
            GpuPixelFormat.B8G8R8A8UNorm => 4,
            GpuPixelFormat.D24UNormS8UInt => 4,
            GpuPixelFormat.D32FloatS8UInt => 5,
            GpuPixelFormat.R16G16B16A16Float => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format,
                "The native Vulkan staging layout has no byte size for that pixel format. The seam gained a "
                + "member this arithmetic has not been taught, and guessing would garble every readback of it "
                + "rather than fail."),
        };

        /// <summary>Bytes between consecutive rows of one mip level. <c>FormatHelpers.GetRowPitch</c>'s
        /// uncompressed arm: <c>width * bytesPerTexel</c>.</summary>
        internal static ulong RowPitch(uint width, GpuPixelFormat format) => (ulong)width * BytesPerTexel(format);

        /// <summary>How many ROWS a mip level of <paramref name="height"/> texels has.
        /// <c>FormatHelpers.GetNumRows</c>'s uncompressed arm, which is the identity. Named rather than inlined
        /// because it is the one function that stops being the identity the day a compressed format arrives, and a
        /// reader comparing the two sources should find it.</summary>
        internal static uint NumRows(uint height, GpuPixelFormat format)
        {
            _ = BytesPerTexel(format);
            return height;
        }

        /// <summary>Bytes for one whole depth slice. <c>FormatHelpers.GetDepthPitch</c>:
        /// <c>rowPitch * numRows</c>.</summary>
        internal static ulong DepthPitch(ulong rowPitch, uint height, GpuPixelFormat format)
            => rowPitch * NumRows(height, format);

        /// <summary>Bytes for a whole region. <c>FormatHelpers.GetRegionSize</c>'s uncompressed arm:
        /// <c>width * height * depth * bytesPerTexel</c>.</summary>
        internal static ulong RegionSize(uint width, uint height, uint depth, GpuPixelFormat format)
            => (ulong)width * height * depth * BytesPerTexel(format);

        /// <summary>
        /// The dimension of <paramref name="mipLevel"/> given mip 0's. <c>Util.GetDimension</c>: halve once per
        /// level, then take at least 1.
        /// <para>
        /// THE LOOP IS REPRODUCED RATHER THAN REPLACED BY A SHIFT. The two agree for every level a real texture
        /// has, and they stop agreeing at a level count of 32 or more, where a shift is undefined and the loop
        /// simply keeps answering 1. A mip level that high is unreachable through the seam and the loop is what the
        /// incumbent does, which is the tie-breaker in a type whose whole job is agreeing with it.
        /// </para>
        /// </summary>
        internal static uint MipDimension(uint largestLevelDimension, uint mipLevel)
        {
            uint value = largestLevelDimension;
            for (uint i = 0; i < mipLevel; i++) value /= 2;

            return Math.Max(1, value);
        }

        /// <summary>
        /// The byte offset of <paramref name="mipLevel"/> WITHIN one array layer.
        /// <c>Util.ComputeMipOffset</c>: the summed region size of every earlier level.
        /// </summary>
        internal static ulong MipOffset(in VulkanStagingShape shape, uint mipLevel)
        {
            ulong offset = 0;
            for (uint level = 0; level < mipLevel; level++) offset += LevelSize(shape, level);

            return offset;
        }

        /// <summary>
        /// The bytes ONE array layer occupies: the summed region size of every mip level.
        /// <c>Util.ComputeArrayLayerOffset</c>'s <c>layerPitch</c>.
        /// </summary>
        internal static ulong LayerPitch(in VulkanStagingShape shape) => MipOffset(shape, shape.MipLevels);

        /// <summary>
        /// Where one subresource starts. <c>Util.ComputeSubresourceOffset</c>:
        /// <c>arrayLayerOffset + mipOffset</c>, where the layer offset is
        /// <c>layerPitch * arrayLayer</c> (<c>Util.ComputeArrayLayerOffset</c>, which short-circuits layer 0 to 0
        /// and reaches the same number).
        /// </summary>
        internal static ulong SubresourceOffset(in VulkanStagingShape shape, uint mipLevel, uint arrayLayer)
            => (LayerPitch(shape) * arrayLayer) + MipOffset(shape, mipLevel);

        /// <summary>
        /// The WHOLE staging buffer's size: <c>layerPitch * arrayLayers</c>, which is exactly what
        /// <c>VkTexture</c>'s staging branch accumulates level by level and then multiplies by
        /// <c>ArrayLayers</c>.
        /// <para>
        /// IT USES THE DESCRIPTION'S LAYER COUNT AND NOT THE CUBEMAP-EXPANDED ONE, reproducing the incumbent
        /// exactly. A cubemap staging texture would therefore be sized for a sixth of its faces, which is a defect
        /// this type inherits rather than one it introduces, and which nothing reaches: a staging texture is
        /// created with the staging bit alone (<see cref="VulkanViewPolicy.ForTexture"/> refuses any other
        /// combination), so it can never also be a cubemap.
        /// </para>
        /// </summary>
        internal static ulong TotalBytes(in VulkanStagingShape shape)
        {
            RequireShape(shape);
            return Fits(LayerPitch(shape) * shape.ArrayLayers, shape, "total size");
        }

        /// <summary>
        /// ONE SUBRESOURCE'S LAYOUT, which is what a <c>Map</c> answers with.
        /// <c>VkTexture.GetSubresourceLayout</c>'s staging branch, whose four assignments are reproduced verbatim:
        /// the row pitch from the MIP's own width, the depth pitch from that and the mip's height, the array pitch
        /// set EQUAL to the depth pitch, and the size set equal to the depth pitch too.
        /// </summary>
        internal static VulkanSubresourceLayout For(in VulkanStagingShape shape, uint mipLevel, uint arrayLayer)
        {
            RequireShape(shape);
            RequireSubresource(shape, mipLevel, arrayLayer);

            uint mipWidth = MipDimension(shape.Width, mipLevel);
            uint mipHeight = MipDimension(shape.Height, mipLevel);

            ulong rowPitch = RowPitch(mipWidth, shape.Format);
            ulong depthPitch = DepthPitch(rowPitch, mipHeight, shape.Format);
            ulong offset = Fits(SubresourceOffset(shape, mipLevel, arrayLayer), shape, "subresource offset");

            return new VulkanSubresourceLayout(offset, rowPitch, depthPitch, depthPitch, depthPitch);
        }

        /// <summary>
        /// The copy region between a staging buffer and an image, for a
        /// <paramref name="width"/> by <paramref name="height"/> rectangle at
        /// (<paramref name="x"/>, <paramref name="y"/>) of the image's
        /// <paramref name="mipLevel"/> and <paramref name="arrayLayer"/>.
        /// <para>
        /// REPRODUCED FROM <c>VkCommandList</c>'s buffer-to-image and image-to-buffer paths, which compute the same
        /// four numbers on both sides: <c>bufferRowLength</c> and <c>bufferImageHeight</c> are the STAGING mip's
        /// own dimensions in TEXELS (not bytes, which is the mistake available here), the buffer offset is the
        /// subresource offset plus the row and column offsets in bytes, and the extent is clamped to the mip's
        /// dimensions.
        /// </para>
        /// <para>
        /// THE CLAMP IS THE INCUMBENT'S AND IS WORTH KEEPING. A caller asking to upload a region wider than the mip
        /// gets the mip's width rather than a copy that runs off the end of the image, which is what
        /// <c>Math.Min(width, mipWidth)</c> does there.
        /// </para>
        /// </summary>
        internal static VulkanBufferImageCopy CopyRegion(in VulkanStagingShape shape, uint mipLevel,
            uint arrayLayer, uint x, uint y, uint width, uint height)
        {
            VulkanSubresourceLayout layout = For(shape, mipLevel, arrayLayer);

            uint mipWidth = MipDimension(shape.Width, mipLevel);
            uint mipHeight = MipDimension(shape.Height, mipLevel);
            uint texelBytes = BytesPerTexel(shape.Format);

            return new VulkanBufferImageCopy(
                layout.Offset + (y * layout.RowPitch) + ((ulong)x * texelBytes),
                mipWidth,
                mipHeight,
                mipLevel,
                arrayLayer,
                x,
                y,
                Math.Min(width, mipWidth),
                Math.Min(height, mipHeight));
        }

        /// <summary>
        /// How many TIGHTLY PACKED bytes a caller's <c>UpdateTexture</c> payload must carry to cover a
        /// <paramref name="width"/> by <paramref name="height"/> region: one row pitch per row, with no padding
        /// anywhere. That is the shape the seam's <c>byte[]</c> overloads document and the shape the incumbent
        /// copies out of, and checking it here turns a short array into a named refusal rather than a read past
        /// the end of it.
        /// </summary>
        internal static ulong RequiredUploadBytes(uint width, uint height, GpuPixelFormat format)
            => DepthPitch(RowPitch(width, format), height, format);

        /// <summary>
        /// The copy region for a device-level <c>UpdateTexture</c>, whose source is a TIGHTLY PACKED lease in the
        /// staging arena rather than a staging texture's subresource. The rows are the region's own width, so the
        /// buffer row length and image height are the region's dimensions and there is no clamping to do: the
        /// caller's bytes describe exactly the rectangle they asked to write.
        /// </summary>
        internal static VulkanBufferImageCopy UploadRegion(ulong bufferOffset, uint mipLevel, uint arrayLayer,
            uint x, uint y, uint width, uint height)
            => new(bufferOffset, width, height, mipLevel, arrayLayer, x, y, width, height);

        /// <summary>
        /// Which mip level and array layer a flat subresource index names.
        /// <c>Util.GetMipLevelAndArrayLayer</c>: the layer is the index divided by the mip count and the level is
        /// the remainder, so subresources run mip-major within a layer.
        /// </summary>
        internal static void MipLevelAndArrayLayer(uint subresource, uint mipLevels, out uint mipLevel,
            out uint arrayLayer)
        {
            if (mipLevels == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mipLevels), mipLevels,
                    "A texture has at least one mip level, so a subresource index cannot be resolved against "
                    + "zero of them.");
            }

            arrayLayer = subresource / mipLevels;
            mipLevel = subresource - (arrayLayer * mipLevels);
        }

        /// <summary>The inverse: the flat subresource index of one mip level and array layer.
        /// <c>Texture.CalculateSubresource</c>.</summary>
        internal static uint Subresource(uint mipLevel, uint arrayLayer, uint mipLevels)
            => (arrayLayer * mipLevels) + mipLevel;

        // One mip level's bytes within a layer, which is what both ComputeMipOffset and ComputeArrayLayerOffset
        // accumulate. Both call GetRegionSize with the mip's dimensions raised to at least the block size, which
        // for an uncompressed format is at least 1, and GetDimension already answers at least 1, so the Math.Max
        // in both incumbent loops is an identity here. Written out anyway, because dropping it would make the two
        // sources stop looking like each other.
        static ulong LevelSize(in VulkanStagingShape shape, uint level)
        {
            uint storageWidth = Math.Max(MipDimension(shape.Width, level), 1);
            uint storageHeight = Math.Max(MipDimension(shape.Height, level), 1);

            // Depth is 1 for every texture the seam can express: it has no depth parameter at all.
            return RegionSize(storageWidth, storageHeight, 1, shape.Format);
        }

        static void RequireShape(in VulkanStagingShape shape)
        {
            if (shape.Width != 0 && shape.Height != 0 && shape.MipLevels != 0 && shape.ArrayLayers != 0) return;

            throw new ArgumentOutOfRangeException(nameof(shape), shape,
                "A native Vulkan staging texture needs a non-zero width, height, mip level count and array layer "
                + "count. A zero in any of them produces a zero-byte buffer, which vkCreateBuffer refuses.");
        }

        static void RequireSubresource(in VulkanStagingShape shape, uint mipLevel, uint arrayLayer)
        {
            if (mipLevel < shape.MipLevels && arrayLayer < shape.ArrayLayers) return;

            throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel,
                "Mip level "
                + mipLevel.ToString(CultureInfo.InvariantCulture)
                + " of array layer "
                + arrayLayer.ToString(CultureInfo.InvariantCulture)
                + " is outside a native Vulkan staging texture with "
                + shape.MipLevels.ToString(CultureInfo.InvariantCulture)
                + " mip levels and "
                + shape.ArrayLayers.ToString(CultureInfo.InvariantCulture)
                + " array layers. The offset it would produce lands in whatever follows the buffer.");
        }

        // The 32-bit ceiling. See the class note: identical to the incumbent everywhere the incumbent does not
        // wrap, and named rather than silent above it.
        static ulong Fits(ulong value, in VulkanStagingShape shape, string what)
        {
            if (value <= uint.MaxValue) return value;

            throw new ArgumentOutOfRangeException(nameof(shape), shape,
                "The "
                + what
                + " of that native Vulkan staging texture is "
                + value.ToString(CultureInfo.InvariantCulture)
                + " bytes, which does not fit the 32-bit size the GPU seam describes a mapping with "
                + "(MappedData.SizeInBytes is a uint). The incumbent computes this in 32 bits throughout and "
                + "wraps here silently, which sizes the staging buffer far too small and corrupts whatever "
                + "follows it. Read back in tiles.");
        }
    }
}
