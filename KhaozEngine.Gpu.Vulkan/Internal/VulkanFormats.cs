using System;
using System.Globalization;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE WHOLE ENGINE-TO-VULKAN MAPPING: pixel formats and their depth readings, image and buffer usage bits,
    /// image layouts, aspect masks, sample counts, view types and every sampler enum. One place, so a wrapper is
    /// thin and the mapping cannot drift between two of them. The same shape <c>D3D11Formats</c> takes on the other
    /// backend.
    ///
    /// <para><b>THE DERIVATIONS ARE NOT HERE.</b> Which usage bits and which views a resource gets is
    /// <see cref="VulkanViewPolicy"/>'s, in the backend's own enums, so the interesting half is tested with no
    /// loader. What is left below is a switch per enum, reproduced from the incumbent's <c>VkFormats</c>.</para>
    ///
    /// <para><b>ONLY THE ENGINE'S OWN FORMATS ARE MAPPED.</b> <see cref="GpuPixelFormat"/> has eight members and
    /// the seam cannot express a ninth, so an unmapped value is a seam change that has not reached here yet, and it
    /// throws rather than guessing.</para>
    ///
    /// <para><b>ONE MAPPING DIVERGES FROM THE INCUMBENT ON PURPOSE, and it is a defect there rather than a
    /// decision.</b> The incumbent maps <c>PixelFormat.R16_G16_Float</c> to
    /// <c>VK_FORMAT_R16G16B16A16_SFLOAT</c> (<c>VkFormats.VdToVkPixelFormat</c>, the
    /// <c>R16_G16_Float</c> arm), a FOUR-channel format for a two-channel request. It is invisible in the shipped
    /// engine because the one texture that uses the format is the distortion offset target, which is written and
    /// sampled through its red and green channels alone and never read back, so the extra two channels are storage
    /// nothing reads. It is not invisible HERE: <see cref="VulkanStagingLayout"/> reproduces the incumbent's
    /// software layout, which sizes that format at four bytes per texel, so an image at eight would make every
    /// copy and every readback of one read the wrong bytes. This backend maps it to
    /// <c>VK_FORMAT_R16G16_SFLOAT</c>, which is what the seam asked for, halves the target's memory and makes the
    /// image agree with the arithmetic. The goldens are unaffected because the two extra channels were never read.
    /// The divergence is recorded in the package README rather than left for a reader of this switch to
    /// notice.</para>
    /// </summary>
    internal static class VulkanFormats
    {
        /// <summary>
        /// The <c>VkFormat</c> for a pixel format. <paramref name="depthStencil"/> asks for the DEPTH reading,
        /// which is what the incumbent passes when the texture carries
        /// <see cref="GpuTextureUsage.DepthStencil"/>: it turns a single-channel float into a real depth format
        /// and leaves everything else alone. Reproduced from <c>VkFormats.VdToVkPixelFormat</c>.
        /// </summary>
        internal static Format ToVkFormat(GpuPixelFormat format, bool depthStencil) => format switch
        {
            GpuPixelFormat.R8UNorm => Format.R8Unorm,
            GpuPixelFormat.R32Float => depthStencil ? Format.D32Sfloat : Format.R32Sfloat,
            // See the class note: the incumbent answers R16G16B16A16Sfloat here and that is its bug, not its
            // contract.
            GpuPixelFormat.R16G16Float => Format.R16G16Sfloat,
            GpuPixelFormat.R8G8B8A8UNorm => Format.R8G8B8A8Unorm,
            GpuPixelFormat.B8G8R8A8UNorm => Format.B8G8R8A8Unorm,
            GpuPixelFormat.R16G16B16A16Float => Format.R16G16B16A16Sfloat,
            // The two combined depth formats carry their own depth reading whatever the flag says, exactly as the
            // incumbent's switch does: neither has a colour spelling to fall back to.
            GpuPixelFormat.D32FloatS8UInt => Format.D32SfloatS8Uint,
            GpuPixelFormat.D24UNormS8UInt => Format.D24UnormS8Uint,
            _ => throw Unmapped(format),
        };

        /// <summary>The real <c>VkImageUsageFlags</c> for a derived usage set.</summary>
        internal static ImageUsageFlags ToImageUsage(VulkanImageUsage usage)
        {
            ImageUsageFlags flags = ImageUsageFlags.None;
            if ((usage & VulkanImageUsage.TransferSrc) != 0) flags |= ImageUsageFlags.TransferSrcBit;
            if ((usage & VulkanImageUsage.TransferDst) != 0) flags |= ImageUsageFlags.TransferDstBit;
            if ((usage & VulkanImageUsage.Sampled) != 0) flags |= ImageUsageFlags.SampledBit;
            if ((usage & VulkanImageUsage.ColorAttachment) != 0) flags |= ImageUsageFlags.ColorAttachmentBit;
            if ((usage & VulkanImageUsage.DepthStencilAttachment) != 0)
                flags |= ImageUsageFlags.DepthStencilAttachmentBit;
            if ((usage & VulkanImageUsage.Storage) != 0) flags |= ImageUsageFlags.StorageBit;
            return flags;
        }

        /// <summary>The real <c>VkBufferUsageFlags</c> for a derived binding set.</summary>
        internal static BufferUsageFlags ToBufferUsage(VulkanBufferBinding binding)
        {
            BufferUsageFlags flags = BufferUsageFlags.None;
            if ((binding & VulkanBufferBinding.TransferSrc) != 0) flags |= BufferUsageFlags.TransferSrcBit;
            if ((binding & VulkanBufferBinding.TransferDst) != 0) flags |= BufferUsageFlags.TransferDstBit;
            if ((binding & VulkanBufferBinding.Vertex) != 0) flags |= BufferUsageFlags.VertexBufferBit;
            if ((binding & VulkanBufferBinding.Index) != 0) flags |= BufferUsageFlags.IndexBufferBit;
            if ((binding & VulkanBufferBinding.Uniform) != 0) flags |= BufferUsageFlags.UniformBufferBit;
            if ((binding & VulkanBufferBinding.Storage) != 0) flags |= BufferUsageFlags.StorageBufferBit;
            if ((binding & VulkanBufferBinding.Indirect) != 0) flags |= BufferUsageFlags.IndirectBufferBit;
            return flags;
        }

        /// <summary>The real <c>VkImageLayout</c> for a canonical resting layout (V-F7).</summary>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="VulkanRestingLayout.None"/>, which is a staging
        /// texture: it is a <c>VkBuffer</c> with no image, so asking for its layout is a caller that lost track of
        /// which kind of resource it holds.</exception>
        internal static ImageLayout ToImageLayout(VulkanRestingLayout resting) => resting switch
        {
            VulkanRestingLayout.ShaderReadOnlyOptimal => ImageLayout.ShaderReadOnlyOptimal,
            VulkanRestingLayout.General => ImageLayout.General,
            VulkanRestingLayout.ColorAttachmentOptimal => ImageLayout.ColorAttachmentOptimal,
            VulkanRestingLayout.DepthStencilAttachmentOptimal => ImageLayout.DepthStencilAttachmentOptimal,
            _ => throw new ArgumentOutOfRangeException(nameof(resting), resting,
                "A native Vulkan staging texture has no image and therefore no layout: it is a VkBuffer with a "
                + "software subresource layout (V-C7). Asking for one means a caller lost track of which kind of "
                + "resource it is holding."),
        };

        /// <summary>
        /// The aspect mask a view and a barrier name. DEPTH ALONE for a depth-stencil texture, reproduced from
        /// <c>VkTextureView</c>, which does the same and does NOT add the stencil aspect: nothing in this engine
        /// samples or clears a stencil plane, and a view carrying both aspects cannot be bound as a sampled image
        /// at all.
        /// </summary>
        internal static ImageAspectFlags ToAspect(bool depthStencil)
            => depthStencil ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;

        /// <summary>
        /// The sample-count bit for a requested count, reproduced from <c>VkFormats.VdToVkSampleCount</c> over the
        /// six counts the incumbent's own enum has.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A count that is not one of 1, 2, 4, 8, 16 or 32. It
        /// THROWS rather than falling silently to 1, which is decision C4's departure inherited for the reason it
        /// gives: the engine clamps upstream (<c>AntiAliasing.ResolveFor</c>), so a count arriving here came from a
        /// caller that skipped it, and a silent downgrade presents as a golden mismatch that reads like a rendering
        /// bug.</exception>
        internal static SampleCountFlags ToSampleCount(uint sampleCount) => sampleCount switch
        {
            1 => SampleCountFlags.Count1Bit,
            2 => SampleCountFlags.Count2Bit,
            4 => SampleCountFlags.Count4Bit,
            8 => SampleCountFlags.Count8Bit,
            16 => SampleCountFlags.Count16Bit,
            32 => SampleCountFlags.Count32Bit,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount,
                "A native Vulkan texture sample count must be 1, 2, 4, 8, 16 or 32. "
                + sampleCount.ToString(CultureInfo.InvariantCulture)
                + " is not a sample count any Vulkan device can be asked for, and rounding it down would make a "
                + "framebuffer quietly not multisampled rather than saying so."),
        };

        /// <summary>
        /// The view type for a texture. Reproduced from <c>VkTextureView</c>: a cubemap is
        /// <c>VK_IMAGE_VIEW_TYPE_CUBE</c> at one logical layer and <c>CUBE_ARRAY</c> above it, and everything else
        /// is a 2D view at one layer and a 2D array above it. The seam expresses no 1D and no 3D texture at all, so
        /// those two arms of the incumbent's switch have no reachable caller here.
        /// </summary>
        internal static ImageViewType ToViewType(bool cubemap, uint arrayLayers) => cubemap
            ? (arrayLayers == 1 ? ImageViewType.TypeCube : ImageViewType.TypeCubeArray)
            : (arrayLayers == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray);

        /// <summary>Sampler addressing. Reproduced from <c>VkFormats.VdToVkSamplerAddressMode</c>.</summary>
        internal static SamplerAddressMode ToAddressMode(GpuSamplerAddress address) => address switch
        {
            GpuSamplerAddress.Wrap => SamplerAddressMode.Repeat,
            GpuSamplerAddress.Mirror => SamplerAddressMode.MirroredRepeat,
            GpuSamplerAddress.Clamp => SamplerAddressMode.ClampToEdge,
            GpuSamplerAddress.Border => SamplerAddressMode.ClampToBorder,
            _ => throw Unmapped(address),
        };

        /// <summary>
        /// The three filter values one <see cref="GpuSamplerFilter"/> becomes. Reproduced from
        /// <c>VkFormats.GetFilterParams</c>, restricted to the three filters the seam has: the engine's shadow path
        /// does manual PCF and never asks for a comparison sampler, so the mixed min and mag combinations of the
        /// incumbent's switch are unreachable here.
        /// </summary>
        internal static void GetFilterParams(GpuSamplerFilter filter, out Filter min, out Filter mag,
            out SamplerMipmapMode mip)
        {
            switch (filter)
            {
                case GpuSamplerFilter.MinPointMagPointMipPoint:
                    min = Filter.Nearest;
                    mag = Filter.Nearest;
                    mip = SamplerMipmapMode.Nearest;
                    return;
                case GpuSamplerFilter.MinLinearMagLinearMipLinear:
                case GpuSamplerFilter.Anisotropic:
                    // ANISOTROPIC IS LINEAR ON ALL THREE plus anisotropyEnable, which is exactly what the
                    // incumbent's Anisotropic arm sets. The enable itself is VulkanSamplerPolicy's, because it is
                    // the one field a device feature can take away.
                    min = Filter.Linear;
                    mag = Filter.Linear;
                    mip = SamplerMipmapMode.Linear;
                    return;
                default:
                    throw Unmapped(filter);
            }
        }

        /// <summary>The border colour. The seam exposes none, so this is the one value the engine's Veldrid path
        /// hardcodes (<c>SamplerBorderColor.TransparentBlack</c>) run through
        /// <c>VkFormats.VdToVkSamplerBorderColor</c>.</summary>
        internal static BorderColor TransparentBlackBorder => BorderColor.FloatTransparentBlack;

        static ArgumentOutOfRangeException Unmapped<T>(T value) where T : struct
            => new(nameof(value), value,
                $"Unmapped {typeof(T).Name} on the native Vulkan backend. The seam gained a member that this "
                + "mapping has not been taught, and guessing would render the wrong thing rather than fail.");
    }
}
