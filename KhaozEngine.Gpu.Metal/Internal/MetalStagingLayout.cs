using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>The shape a staging texture's layout is computed from: everything
    /// <see cref="GpuTextureDescription"/> carries that the arithmetic reads, and nothing else.</summary>
    /// <param name="Width">Texel width of mip 0.</param>
    /// <param name="Height">Texel height of mip 0.</param>
    /// <param name="MipLevels">How many mip levels, at least 1.</param>
    /// <param name="ArrayLayers">How many array layers, at least 1.</param>
    /// <param name="Format">The pixel format, which supplies the bytes per texel.</param>
    internal readonly record struct MetalStagingShape(
        uint Width, uint Height, uint MipLevels, uint ArrayLayers, GpuPixelFormat Format);

    /// <summary>One subresource's place in the staging buffer.</summary>
    /// <param name="Offset">Where the subresource starts, in bytes from the buffer's first byte, which is what
    /// <c>Util.ComputeSubresourceOffset</c> answers and what <c>MapTexture</c> adds to <c>contents()</c>.</param>
    /// <param name="RowPitch">Bytes between consecutive rows, which is what <see cref="MappedData.RowPitch"/>
    /// carries and what every golden de-strides with.</param>
    /// <param name="DepthPitch">Bytes for one whole depth slice, which for a 2D texture is the whole
    /// subresource.</param>
    /// <param name="Size">How many bytes the subresource occupies, which is what
    /// <see cref="MappedData.SizeInBytes"/> carries. <c>MTLTexture.GetSubresourceSize</c>.</param>
    internal readonly record struct MetalSubresourceLayout(
        ulong Offset, ulong RowPitch, ulong DepthPitch, ulong Size);

    /// <summary>
    /// THE SOFTWARE SUBRESOURCE LAYOUT THE ENGINE SHIPPED UNTIL <c>18.0.0</c>, REPRODUCED BYTE FOR BYTE.
    /// Decision M-C5, section 13. THE INCUMBENT throughout this file means that backend, the vendored Veldrid
    /// fork the engine pinned at <c>4.9.104</c> and deleted in <c>18.0.0</c>. Its arithmetic is reproduced rather
    /// than replaced because every golden in the suite was baked through it and the seam states its byte contract
    /// in its terms, so the citations below are provenance for numbers that are still live.
    ///
    /// <para><b>THIS IS THE HIGHEST-RISK PARITY SURFACE IN THE BACKEND, and the design says so in as many
    /// words.</b> Every golden in the suite reads back through <c>IGpuDevice.Map(staging, ...)</c> and consumes
    /// <see cref="MappedData.RowPitch"/>, so a DIFFERENT arithmetic here garbles all 36 at once. Nothing about
    /// that failure is loud: the readback succeeds, the pointer is valid, and the pixels are simply in the wrong
    /// places.</para>
    ///
    /// <para><b>IT BACKED A STAGING TEXTURE WITH AN <c>MTLBuffer</c> AND COMPUTED THE LAYOUT IN SOFTWARE.</b>
    /// Not a linear texture, and there is no Metal call that answers the question either: the row
    /// pitch, the depth pitch, the subresource size and the subresource offset are all engine arithmetic. So
    /// reproducing it is reproducing ARITHMETIC rather than agreeing with a driver, which is the one reason it can
    /// be pinned at all with no device in the room. <c>MetalStagingLayoutTableTests</c> carries the checked-in
    /// table those formulas produce and asserts this type against it, which converts "should be identical" into a
    /// checked fact BEFORE a single golden runs.</para>
    ///
    /// <para><b>EVERY FORMULA CITES ITS SOURCE BY MEMBER NAME</b> (V-I6). The six functions this reproduces are
    /// <c>FormatSizeHelpers.GetSizeInBytes</c>, <c>FormatHelpers.GetRowPitch</c>, <c>FormatHelpers.GetNumRows</c>,
    /// <c>FormatHelpers.GetDepthPitch</c>, <c>FormatHelpers.GetRegionSize</c> and <c>Util.GetDimension</c>, plus
    /// the three that compose them: <c>Util.ComputeMipOffset</c>, <c>Util.ComputeArrayLayerOffset</c> and
    /// <c>Util.ComputeSubresourceOffset</c>. The three call sites that USE them are <c>MTLTexture</c>'s staging
    /// branch (the buffer's total size), <c>MTLTexture.GetSubresourceLayout</c> and
    /// <c>MTLTexture.GetSubresourceSize</c> (what a <c>Map</c> answers with).</para>
    ///
    /// <para><b>ONE INCUMBENT INCONSISTENCY IS INHERITED AS A NO-OP, and it is worth naming so a later reader does
    /// not think this type simplified it.</b> The total-size loop accumulates from the mip's RAW dimensions while
    /// the per-subresource functions accumulate from the same dimensions raised to at least the block size. For an
    /// uncompressed format the block size is 1 and <c>Util.GetDimension</c> already answers at least 1, so the two
    /// are the same number at every level and <see cref="TotalBytes"/> is genuinely
    /// <see cref="LayerPitch"/> times the layer count. They would differ only for a block-compressed format, and
    /// there is not one.</para>
    ///
    /// <para><b>THE COMPRESSED BRANCH IS DELIBERATELY ABSENT rather than reproduced.</b>
    /// <see cref="GpuPixelFormat"/> has eight members and not one is block-compressed, so the incumbent's
    /// <c>blockSize</c> is 1 at every site this backend can reach and its <c>(width + 3) / 4</c> row arithmetic is
    /// unreachable code. Carrying an unreachable branch would be carrying a second thing to keep correct with no
    /// way to test it. If the seam ever gains a compressed format the branch comes back HERE, with the incumbent's
    /// own version as the reference, and the table gains its rows.</para>
    ///
    /// <para><b>DEPTH IS ALWAYS 1</b>, because <see cref="GpuTextureDescription"/> has no depth at all: the seam
    /// expresses 2D textures, 2D arrays and cubemaps and nothing else. The incumbent's depth terms were therefore
    /// multiplications by one, kept visible below rather than folded away, so a reader comparing the two sources
    /// sees the same shape.</para>
    ///
    /// <para><b>ARITHMETIC IS IN 64 BITS AND THE RESULT IS REFUSED ABOVE 32.</b> The incumbent computed in
    /// <c>uint</c> throughout and wrapped silently on a texture large enough to overflow, which would create a
    /// staging buffer far too small and corrupt whatever sits after it. Every value here is identical to the
    /// incumbent's everywhere the incumbent did not wrap, and above that it throws by name. That is a guard
    /// rather than a divergence: <see cref="MappedData.SizeInBytes"/> is a <c>uint</c>, so a staging texture past
    /// this bound cannot be described through the seam at all. It is the Vulkan sibling's ruling inherited
    /// deliberately, because the two backends reproduce the same incumbent arithmetic and disagreeing about its
    /// ceiling would be a difference with no reason behind it.</para>
    /// </summary>
    internal static class MetalStagingLayout
    {
        /// <summary>
        /// Bytes per texel. <c>FormatSizeHelpers.GetSizeInBytes</c>, restricted to the eight formats the seam has.
        /// <para>
        /// <b><see cref="GpuPixelFormat.D32FloatS8UInt"/> IS FIVE BYTES, AND IT IS THE ONE VALUE A READER WILL
        /// DOUBT.</b> The real <c>MTLPixelFormatDepth32Float_Stencil8</c> texture is eight bytes per texel with
        /// the stencil in its own plane, and this number is not about that texture. It is the incumbent's SOFTWARE
        /// layout for the <c>MTLBuffer</c> that mirrors it, and reproducing it is the whole point of this type:
        /// the goldens read back through the incumbent's stride, not through the driver's.
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
                "The native Metal staging layout has no byte size for that pixel format. The seam gained a "
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
        /// has and they stop agreeing at a level count of 32 or more, where a shift is undefined and the loop
        /// simply keeps answering 1. A mip level that high is unreachable through the seam, and the loop is what
        /// the incumbent did, which is the tie-breaker in a type whose whole job is agreeing with it.
        /// </para>
        /// </summary>
        internal static uint MipDimension(uint largestLevelDimension, uint mipLevel)
        {
            uint value = largestLevelDimension;
            for (uint i = 0; i < mipLevel; i++) value /= 2;

            return Math.Max(1, value);
        }

        /// <summary>The byte offset of <paramref name="mipLevel"/> WITHIN one array layer.
        /// <c>Util.ComputeMipOffset</c>: the summed region size of every earlier level.</summary>
        internal static ulong MipOffset(in MetalStagingShape shape, uint mipLevel)
        {
            ulong offset = 0;
            for (uint level = 0; level < mipLevel; level++) offset += LevelSize(shape, level);

            return offset;
        }

        /// <summary>The bytes ONE array layer occupies: the summed region size of every mip level.
        /// <c>Util.ComputeArrayLayerOffset</c>'s <c>layerPitch</c>.</summary>
        internal static ulong LayerPitch(in MetalStagingShape shape) => MipOffset(shape, shape.MipLevels);

        /// <summary>
        /// Where one subresource starts. <c>Util.ComputeSubresourceOffset</c>:
        /// <c>arrayLayerOffset + mipOffset</c>, where the layer offset is <c>layerPitch * arrayLayer</c>
        /// (<c>Util.ComputeArrayLayerOffset</c>, which short-circuits layer 0 to 0 and reaches the same number).
        /// </summary>
        internal static ulong SubresourceOffset(in MetalStagingShape shape, uint mipLevel, uint arrayLayer)
            => (LayerPitch(shape) * arrayLayer) + MipOffset(shape, mipLevel);

        /// <summary>
        /// The WHOLE staging buffer's size, which is what <c>-newBufferWithLength:options:</c> is asked for.
        /// <c>MTLTexture</c>'s staging branch accumulates one level at a time and then multiplies by
        /// <c>ArrayLayers</c>, which is <see cref="LayerPitch"/> times the layer count (see the class note on the
        /// one inconsistency that makes those the same number).
        /// <para>
        /// IT USES THE DESCRIPTION'S LAYER COUNT AND NOT A CUBEMAP-EXPANDED ONE, reproducing the incumbent
        /// exactly. A cubemap staging texture would therefore be sized for a sixth of its faces, which is a defect
        /// this type inherits rather than one it introduces, and which nothing reaches: a staging texture is
        /// created with the staging bit alone (<see cref="MetalViewPolicy.ForTexture"/> refuses any other
        /// combination), so it can never also be a cubemap.
        /// </para>
        /// </summary>
        internal static ulong TotalBytes(in MetalStagingShape shape)
        {
            RequireShape(shape);
            return Fits(LayerPitch(shape) * shape.ArrayLayers, shape, "total size");
        }

        /// <summary>
        /// ONE SUBRESOURCE'S LAYOUT, which is what a <c>Map</c> answers with.
        /// <c>MTLTexture.GetSubresourceLayout</c> gives the row and depth pitches from the MIP's own dimensions,
        /// <c>MTLTexture.GetSubresourceSize</c> gives the size (the depth pitch times a depth of 1), and
        /// <c>Util.ComputeSubresourceOffset</c> gives the offset. All four are reproduced verbatim.
        /// </summary>
        internal static MetalSubresourceLayout For(in MetalStagingShape shape, uint mipLevel, uint arrayLayer)
        {
            RequireShape(shape);
            RequireSubresource(shape, mipLevel, arrayLayer);

            uint mipWidth = MipDimension(shape.Width, mipLevel);
            uint mipHeight = MipDimension(shape.Height, mipLevel);

            ulong rowPitch = RowPitch(mipWidth, shape.Format);
            ulong depthPitch = DepthPitch(rowPitch, mipHeight, shape.Format);
            ulong offset = Fits(SubresourceOffset(shape, mipLevel, arrayLayer), shape, "subresource offset");

            // The size is depth * depthPitch and the depth is 1, which is GetSubresourceSize with the seam's own
            // constraint applied rather than a simplification of it.
            return new MetalSubresourceLayout(offset, rowPitch, depthPitch, depthPitch * 1);
        }

        /// <summary>
        /// How many TIGHTLY PACKED bytes a caller's <c>UpdateTexture</c> payload must carry to cover a
        /// <paramref name="width"/> by <paramref name="height"/> region: one row pitch per row with no padding
        /// anywhere. That is the shape the seam's <c>byte[]</c> overloads document and the shape the incumbent
        /// copies out of, and checking it turns a short array into a named refusal rather than a read past the end
        /// of it.
        /// </summary>
        internal static ulong RequiredUploadBytes(uint width, uint height, GpuPixelFormat format)
            => DepthPitch(RowPitch(width, format), height, format);

        /// <summary>
        /// REFUSE A REGION THAT DOES NOT FIT ITS DESTINATION SUBRESOURCE. The texture sibling of
        /// <c>MetalBufferPolicy.RequireWriteFits</c>, and the check both upload paths were missing: they checked
        /// only that the SOURCE array was long enough, which says nothing about where the bytes land.
        /// <para>
        /// WHAT IT PREVENTS IS DIFFERENT ON THE TWO PATHS, and neither is loud. A staging texture is an
        /// <c>MTLBuffer</c> written by a software strided copy, so an over-large region writes past the
        /// subresource into whatever follows it in the same allocation, or past the allocation itself. A Private
        /// texture is written by a blit the DRIVER validates, so an over-large region is a validation failure at
        /// best and texels in the wrong place at worst. The incumbent checked neither.
        /// </para>
        /// <para>
        /// The mip level and array layer are checked first, through the same <c>RequireSubresource</c> the layout
        /// arithmetic uses, because a region cannot be compared against a subresource that does not exist.
        /// </para>
        /// </summary>
        internal static void RequireRegionFits(in MetalStagingShape shape, uint mipLevel, uint arrayLayer,
            uint x, uint y, uint width, uint height)
        {
            RequireShape(shape);
            RequireSubresource(shape, mipLevel, arrayLayer);

            uint mipWidth = MipDimension(shape.Width, mipLevel);
            uint mipHeight = MipDimension(shape.Height, mipLevel);

            if ((ulong)x + width > mipWidth) throw Outside(nameof(x), x, width, mipWidth, "wide", mipLevel);
            if ((ulong)y + height > mipHeight) throw Outside(nameof(y), y, height, mipHeight, "tall", mipLevel);
        }

        /// <summary>
        /// Which mip level and array layer a flat subresource index names. <c>Util.GetMipLevelAndArrayLayer</c>:
        /// the layer is the index divided by the mip count and the level is the remainder, so subresources run
        /// mip-major within a layer.
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

        // One mip level's bytes within a layer, which is what both ComputeMipOffset and ComputeArrayLayerOffset
        // accumulate. Both call GetRegionSize with the mip's dimensions raised to at least the block size, which
        // for an uncompressed format is at least 1, and GetDimension already answers at least 1, so the Math.Max
        // in both incumbent loops is an identity here. Written out anyway, because dropping it would make the two
        // sources stop looking like each other.
        static ulong LevelSize(in MetalStagingShape shape, uint level)
        {
            uint storageWidth = Math.Max(MipDimension(shape.Width, level), 1);
            uint storageHeight = Math.Max(MipDimension(shape.Height, level), 1);

            // Depth is 1 for every texture the seam can express: it has no depth parameter at all.
            return RegionSize(storageWidth, storageHeight, 1, shape.Format);
        }

        static void RequireShape(in MetalStagingShape shape)
        {
            if (shape.Width != 0 && shape.Height != 0 && shape.MipLevels != 0 && shape.ArrayLayers != 0) return;

            throw new ArgumentOutOfRangeException(nameof(shape), shape,
                "A native Metal staging texture needs a non-zero width, height, mip level count and array layer "
                + "count. A zero in any of them produces a zero-byte buffer, which -newBufferWithLength:options: "
                + "refuses.");
        }

        static void RequireSubresource(in MetalStagingShape shape, uint mipLevel, uint arrayLayer)
        {
            if (mipLevel < shape.MipLevels && arrayLayer < shape.ArrayLayers) return;

            throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel,
                "Mip level "
                + mipLevel.ToString(CultureInfo.InvariantCulture)
                + " of array layer "
                + arrayLayer.ToString(CultureInfo.InvariantCulture)
                + " is outside a native Metal staging texture with "
                + shape.MipLevels.ToString(CultureInfo.InvariantCulture)
                + " mip levels and "
                + shape.ArrayLayers.ToString(CultureInfo.InvariantCulture)
                + " array layers. The offset it would produce lands in whatever follows the buffer.");
        }

        // One axis of the region refusal, as one sentence a caller can act on: which edge it crossed, by how
        // much, and what the mip level it was aimed at actually measures.
        static ArgumentOutOfRangeException Outside(string axis, uint origin, uint extent, uint bound,
            string dimension, uint mipLevel)
            => new(axis, origin,
                "A native Metal texture upload of "
                + extent.ToString(CultureInfo.InvariantCulture)
                + " texels from "
                + axis
                + " = "
                + origin.ToString(CultureInfo.InvariantCulture)
                + " runs to "
                + ((ulong)origin + extent).ToString(CultureInfo.InvariantCulture)
                + ", past a mip level "
                + mipLevel.ToString(CultureInfo.InvariantCulture)
                + " that is only "
                + bound.ToString(CultureInfo.InvariantCulture)
                + " texels "
                + dimension
                + ". On a staging texture that writes past the subresource into whatever follows it, and on a "
                + "Private texture the driver either refuses the blit or puts the texels somewhere else.");

        // The 32-bit ceiling. See the class note: the arithmetic runs in 64 bits, so a size the seam cannot
        // describe is named here rather than wrapping silently.
        static ulong Fits(ulong value, in MetalStagingShape shape, string what)
        {
            if (value <= uint.MaxValue) return value;

            throw new ArgumentOutOfRangeException(nameof(shape), shape,
                "The "
                + what
                + " of that native Metal staging texture is "
                + value.ToString(CultureInfo.InvariantCulture)
                + " bytes, which does not fit the 32-bit size the GPU seam describes a mapping with "
                + "(MappedData.SizeInBytes is a uint). The same arithmetic in 32 bits wraps here silently, "
                + "which sizes the staging buffer far too small and corrupts whatever follows it, so it is "
                + "refused by name instead. Read back in tiles.");
        }
    }
}
