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
    /// decision.</b> The incumbent mapped <c>PixelFormat.R16_G16_Float</c> to
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
        /// which is what the incumbent passed when the texture carries
        /// <see cref="GpuTextureUsage.DepthStencil"/>: it turns a single-channel float into a real depth format
        /// and leaves everything else alone. Reproduced from <c>VkFormats.VdToVkPixelFormat</c>.
        /// </summary>
        internal static Format ToVkFormat(GpuPixelFormat format, bool depthStencil) => format switch
        {
            GpuPixelFormat.R8UNorm => Format.R8Unorm,
            GpuPixelFormat.R32Float => depthStencil ? Format.D32Sfloat : Format.R32Sfloat,
            // See the class note: the incumbent answered R16G16B16A16Sfloat here and that is its bug, not its
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

        /// <summary>
        /// The real <c>VkShaderStageFlags</c> for the seam's stage set. Read by every descriptor set layout
        /// binding, and by row 13's pipeline stages.
        /// <para>
        /// <see cref="GpuShaderStages.None"/> maps to no bits, which <c>vkCreateDescriptorSetLayout</c> accepts
        /// and which means the binding is visible to no stage. It is not refused here, because the honest place
        /// to notice a binding no shader can see is the shader-validation pass rather than a flag translation.
        /// </para>
        /// </summary>
        internal static ShaderStageFlags ToShaderStages(GpuShaderStages stages)
        {
            ShaderStageFlags flags = ShaderStageFlags.None;
            if ((stages & GpuShaderStages.Vertex) != 0) flags |= ShaderStageFlags.VertexBit;
            if ((stages & GpuShaderStages.Geometry) != 0) flags |= ShaderStageFlags.GeometryBit;
            if ((stages & GpuShaderStages.TessellationControl) != 0)
                flags |= ShaderStageFlags.TessellationControlBit;
            if ((stages & GpuShaderStages.TessellationEvaluation) != 0)
                flags |= ShaderStageFlags.TessellationEvaluationBit;
            if ((stages & GpuShaderStages.Fragment) != 0) flags |= ShaderStageFlags.FragmentBit;
            if ((stages & GpuShaderStages.Compute) != 0) flags |= ShaderStageFlags.ComputeBit;
            return flags;
        }

        /// <summary>The real <c>VkDescriptorType</c> for one of the seven this backend counts (8.1).</summary>
        internal static DescriptorType ToDescriptorType(VulkanDescriptorType type) => type switch
        {
            VulkanDescriptorType.UniformBuffer => DescriptorType.UniformBuffer,
            VulkanDescriptorType.UniformBufferDynamic => DescriptorType.UniformBufferDynamic,
            VulkanDescriptorType.StorageBuffer => DescriptorType.StorageBuffer,
            VulkanDescriptorType.StorageBufferDynamic => DescriptorType.StorageBufferDynamic,
            // SEPARATE from the sampler, never COMBINED_IMAGE_SAMPLER, which the shared GLSL sources already
            // assume by declaring texture2D and sampler separately.
            VulkanDescriptorType.SampledImage => DescriptorType.SampledImage,
            VulkanDescriptorType.StorageImage => DescriptorType.StorageImage,
            VulkanDescriptorType.Sampler => DescriptorType.Sampler,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type,
                "A native Vulkan descriptor type outside the seven this backend counts."),
        };

        /// <summary>The real <c>VkImageLayout</c> a sampled or storage image descriptor is written with
        /// (8.1).</summary>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="VulkanDescriptorImageLayout.None"/>, which
        /// means the write is not an image write at all and its layout should never have been read.</exception>
        internal static ImageLayout ToDescriptorImageLayout(VulkanDescriptorImageLayout layout) => layout switch
        {
            VulkanDescriptorImageLayout.ShaderReadOnlyOptimal => ImageLayout.ShaderReadOnlyOptimal,
            VulkanDescriptorImageLayout.General => ImageLayout.General,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout,
                "A native Vulkan descriptor write that is not an image write has no image layout. Reading one "
                + "means the write's descriptor type and its payload disagree."),
        };

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
            VulkanRestingLayout.PresentSrcKhr => ImageLayout.PresentSrcKhr,
            _ => throw new ArgumentOutOfRangeException(nameof(resting), resting,
                "A native Vulkan staging texture has no image and therefore no layout: it is a VkBuffer with a "
                + "software subresource layout (V-C7). Asking for one means a caller lost track of which kind of "
                + "resource it is holding."),
        };

        /// <summary>
        /// The aspect mask a VIEW and a COPY REGION name. DEPTH ALONE for a depth-stencil texture, reproduced from
        /// <c>VkTextureView</c>, which does the same and does NOT add the stencil aspect: nothing in this engine
        /// samples a stencil plane, a view carrying both aspects cannot be bound as a sampled image at all, and a
        /// <c>vkCmdCopyBufferToImage</c> region must name exactly one aspect bit.
        /// <para>
        /// A BARRIER AND A CREATION-TIME CLEAR TAKE THE OTHER ANSWER. See <see cref="ToBarrierAspect"/>: the two
        /// rules genuinely differ on a combined depth-stencil format, and one helper answering both is what made
        /// this backend emit a spec-invalid barrier and leave the stencil plane uncleared.
        /// </para>
        /// </summary>
        internal static ImageAspectFlags ToAspect(bool depthStencil)
            => depthStencil ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;

        /// <summary>
        /// Whether a pixel format carries a STENCIL plane as well as a depth one. Both depth formats the seam has
        /// are combined, so this is true for every depth format except the single-channel float a depth-stencil
        /// texture turns into <c>VK_FORMAT_D32_SFLOAT</c>.
        /// </summary>
        internal static bool IsStencilFormat(GpuPixelFormat format)
            => format is GpuPixelFormat.D32FloatS8UInt or GpuPixelFormat.D24UNormS8UInt;

        /// <summary>
        /// The aspect mask a BARRIER and a CREATION-TIME CLEAR name: EVERY aspect the format has, which on a
        /// combined format is depth AND stencil. Reproduced from the incumbent's <c>VkTexture</c>, whose barrier
        /// helper adds the stencil bit for a format that has one and whose creation-time clear range does the same.
        ///
        /// <para><b>IT IS NOT A REFINEMENT OF THE VIEW ANSWER, IT IS A DIFFERENT RULE.</b> A layout transition
        /// applies to the whole image, and without <c>separateDepthStencilLayouts</c> a barrier over a combined
        /// format MUST name both planes: naming depth alone is
        /// <c>VUID-VkImageMemoryBarrier2-image-03319</c> rather than a narrower transition. And the creation-time
        /// clear inherits it for a reason V-M10 is entirely about: a clear range over depth alone leaves the
        /// stencil plane in whatever <c>UNDEFINED</c> gave it, which is not stable between two runs of the same
        /// golden. The preserved clear exists to remove exactly that.</para>
        /// </summary>
        internal static ImageAspectFlags ToBarrierAspect(bool depthStencil, GpuPixelFormat format)
        {
            if (!depthStencil) return ImageAspectFlags.ColorBit;

            return IsStencilFormat(format)
                ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : ImageAspectFlags.DepthBit;
        }

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
        /// those two arms of the incumbent's switch had no reachable caller here.
        /// <para>
        /// WITH ONE DEPARTURE FROM THE INCUMBENT, and it is the point of #666. <paramref name="arrayView"/> is
        /// <see cref="GpuTextureDescription.IsArray"/>, so a texture the seam asked for as an ARRAY takes
        /// <c>VK_IMAGE_VIEW_TYPE_2D_ARRAY</c> even at one layer, which is what a fragment declaring
        /// <c>texture2DArray</c> needs. A 2D-array view over an image with <c>arrayLayers == 1</c> is legal, the
        /// range simply covers one layer. It is ORed with the count test rather than replacing it, so a caller
        /// that passes only the count keeps the incumbent's answer, and the CUBE arm is untouched because a cube
        /// is already six faces and the seam has no one-cube cube-array caller.
        /// </para>
        /// </summary>
        internal static ImageViewType ToViewType(bool cubemap, uint arrayLayers, bool arrayView = false)
            => cubemap
                ? (arrayLayers == 1 ? ImageViewType.TypeCube : ImageViewType.TypeCubeArray)
                : (arrayLayers == 1 && !arrayView ? ImageViewType.Type2D : ImageViewType.Type2DArray);

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
        /// hardcoded (<c>SamplerBorderColor.TransparentBlack</c>) run through
        /// <c>VkFormats.VdToVkSamplerBorderColor</c>.</summary>
        internal static BorderColor TransparentBlackBorder => BorderColor.FloatTransparentBlack;

        // ---- Pipeline state (row 13, https://github.com/APKiwiOrg/KhaozEngine/issues/523) ----

        /// <summary>The vertex attribute format. Every seam format is a float vector, so this is the one mapping
        /// here with no incumbent quirk in it at all.</summary>
        internal static Format ToVertexFormat(GpuVertexElementFormat format) => format switch
        {
            GpuVertexElementFormat.Float1 => Format.R32Sfloat,
            GpuVertexElementFormat.Float2 => Format.R32G32Sfloat,
            GpuVertexElementFormat.Float3 => Format.R32G32B32Sfloat,
            GpuVertexElementFormat.Float4 => Format.R32G32B32A32Sfloat,
            _ => throw Unmapped(format),
        };

        /// <summary>Primitive topology. Reproduced from <c>VkFormats.VdToVkPrimitiveTopology</c>.</summary>
        internal static PrimitiveTopology ToTopology(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.TriangleList => PrimitiveTopology.TriangleList,
            GpuPrimitiveTopology.TriangleStrip => PrimitiveTopology.TriangleStrip,
            GpuPrimitiveTopology.LineList => PrimitiveTopology.LineList,
            GpuPrimitiveTopology.LineStrip => PrimitiveTopology.LineStrip,
            GpuPrimitiveTopology.PointList => PrimitiveTopology.PointList,
            _ => throw Unmapped(topology),
        };

        /// <summary>The depth comparison.</summary>
        internal static CompareOp ToCompareOp(GpuComparison comparison) => comparison switch
        {
            GpuComparison.Never => CompareOp.Never,
            GpuComparison.Less => CompareOp.Less,
            GpuComparison.Equal => CompareOp.Equal,
            GpuComparison.LessEqual => CompareOp.LessOrEqual,
            GpuComparison.Greater => CompareOp.Greater,
            GpuComparison.NotEqual => CompareOp.NotEqual,
            GpuComparison.GreaterEqual => CompareOp.GreaterOrEqual,
            GpuComparison.Always => CompareOp.Always,
            _ => throw Unmapped(comparison),
        };

        /// <summary>The cull mode.</summary>
        internal static CullModeFlags ToCullMode(GpuFaceCull cull) => cull switch
        {
            GpuFaceCull.Back => CullModeFlags.BackBit,
            GpuFaceCull.Front => CullModeFlags.FrontBit,
            GpuFaceCull.None => CullModeFlags.None,
            _ => throw Unmapped(cull),
        };

        /// <summary>The fill mode. <c>Wireframe</c> needs the <c>fillModeNonSolid</c> feature, which
        /// <see cref="VulkanFeatureChain"/> enables by name where the device has it.</summary>
        internal static PolygonMode ToPolygonMode(GpuPolygonFill fill) => fill switch
        {
            GpuPolygonFill.Solid => PolygonMode.Fill,
            GpuPolygonFill.Wireframe => PolygonMode.Line,
            _ => throw Unmapped(fill),
        };

        /// <summary>
        /// The front-facing winding, MAPPED STRAIGHT ACROSS, and the reason is the single most consequential line
        /// in this design rather than a coincidence. The viewport carries a NEGATIVE height (V-A5,
        /// <see cref="VulkanViewportRect"/>), which makes Vulkan's clip space match Direct3D's, so a triangle that
        /// is clockwise in the engine's clip space is clockwise in framebuffer space here exactly as it is there.
        /// Flipping the winding to "compensate" for Vulkan's y-down NDC would double-correct and cull every front
        /// face in the engine, and it would do it silently.
        /// </summary>
        internal static FrontFace ToFrontFace(GpuFrontFace face) => face switch
        {
            GpuFrontFace.Clockwise => FrontFace.Clockwise,
            GpuFrontFace.CounterClockwise => FrontFace.CounterClockwise,
            _ => throw Unmapped(face),
        };

        /// <summary>A blend factor. The two constant arms read <c>blendConstants</c>, which this backend bakes
        /// into the pipeline from <see cref="GpuPipelineDescription.BlendFactor"/> rather than leaving
        /// dynamic.</summary>
        internal static BlendFactor ToBlendFactor(GpuBlendFactor factor) => factor switch
        {
            GpuBlendFactor.Zero => BlendFactor.Zero,
            GpuBlendFactor.One => BlendFactor.One,
            GpuBlendFactor.SourceColor => BlendFactor.SrcColor,
            GpuBlendFactor.InverseSourceColor => BlendFactor.OneMinusSrcColor,
            GpuBlendFactor.SourceAlpha => BlendFactor.SrcAlpha,
            GpuBlendFactor.InverseSourceAlpha => BlendFactor.OneMinusSrcAlpha,
            GpuBlendFactor.DestinationColor => BlendFactor.DstColor,
            GpuBlendFactor.InverseDestinationColor => BlendFactor.OneMinusDstColor,
            GpuBlendFactor.DestinationAlpha => BlendFactor.DstAlpha,
            GpuBlendFactor.InverseDestinationAlpha => BlendFactor.OneMinusDstAlpha,
            GpuBlendFactor.BlendFactor => BlendFactor.ConstantColor,
            GpuBlendFactor.InverseBlendFactor => BlendFactor.OneMinusConstantColor,
            _ => throw Unmapped(factor),
        };

        /// <summary>A blend equation.</summary>
        internal static BlendOp ToBlendOp(GpuBlendFunction function) => function switch
        {
            GpuBlendFunction.Add => BlendOp.Add,
            GpuBlendFunction.Subtract => BlendOp.Subtract,
            GpuBlendFunction.ReverseSubtract => BlendOp.ReverseSubtract,
            GpuBlendFunction.Minimum => BlendOp.Min,
            GpuBlendFunction.Maximum => BlendOp.Max,
            _ => throw Unmapped(function),
        };

        /// <summary>The two dynamic states this backend leaves dynamic, and there is deliberately no third. See
        /// <see cref="VulkanPipelineDynamicState"/> for why the list is a value rather than a line inside the
        /// pipeline seam.</summary>
        internal static DynamicState ToDynamicState(VulkanDynamicState state) => state switch
        {
            VulkanDynamicState.Viewport => DynamicState.Viewport,
            VulkanDynamicState.Scissor => DynamicState.Scissor,
            _ => throw Unmapped(state),
        };

        static ArgumentOutOfRangeException Unmapped<T>(T value) where T : struct
            => new(nameof(value), value,
                $"Unmapped {typeof(T).Name} on the native Vulkan backend. The seam gained a member that this "
                + "mapping has not been taught, and guessing would render the wrong thing rather than fail.");
    }
}
