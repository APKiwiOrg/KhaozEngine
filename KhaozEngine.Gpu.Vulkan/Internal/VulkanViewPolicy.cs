using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The image usage bits a texture is created with, as the backend's OWN flags rather than
    /// <c>VkImageUsageFlags</c>, so the DERIVATION from the seam's usage bits is a pure function tested with no
    /// loader. The translation to Silk.NET lives in <see cref="VulkanFormats"/>, which is the same split
    /// <c>D3D11ViewPolicy</c> and <c>D3D11Formats</c> take on the other backend.
    /// </summary>
    [Flags]
    internal enum VulkanImageUsage
    {
        /// <summary>Nothing, which no image this backend creates ever is.</summary>
        None = 0,

        /// <summary><c>VK_IMAGE_USAGE_TRANSFER_SRC_BIT</c>. On EVERY image, because a readback copies out of any
        /// texture and mip generation blits out of one.</summary>
        TransferSrc = 1 << 0,

        /// <summary><c>VK_IMAGE_USAGE_TRANSFER_DST_BIT</c>. On EVERY image, because the creation-time clear, the
        /// upload and the mip blit all write into one.</summary>
        TransferDst = 1 << 1,

        /// <summary><c>VK_IMAGE_USAGE_SAMPLED_BIT</c>.</summary>
        Sampled = 1 << 2,

        /// <summary><c>VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT</c>.</summary>
        ColorAttachment = 1 << 3,

        /// <summary><c>VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT</c>.</summary>
        DepthStencilAttachment = 1 << 4,

        /// <summary><c>VK_IMAGE_USAGE_STORAGE_BIT</c>.</summary>
        Storage = 1 << 5,
    }

    /// <summary>
    /// The buffer usage bits, same shape and same reason as <see cref="VulkanImageUsage"/>.
    /// </summary>
    [Flags]
    internal enum VulkanBufferBinding
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary><c>VK_BUFFER_USAGE_TRANSFER_SRC_BIT</c>. On every buffer, matching the incumbent.</summary>
        TransferSrc = 1 << 0,

        /// <summary><c>VK_BUFFER_USAGE_TRANSFER_DST_BIT</c>. On every buffer, matching the incumbent.</summary>
        TransferDst = 1 << 1,

        /// <summary><c>VK_BUFFER_USAGE_VERTEX_BUFFER_BIT</c>.</summary>
        Vertex = 1 << 2,

        /// <summary><c>VK_BUFFER_USAGE_INDEX_BUFFER_BIT</c>.</summary>
        Index = 1 << 3,

        /// <summary><c>VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT</c>.</summary>
        Uniform = 1 << 4,

        /// <summary><c>VK_BUFFER_USAGE_STORAGE_BUFFER_BIT</c>, which BOTH structured kinds take (V-C4). There is
        /// no read-only storage-buffer bit in Vulkan, and no RAW byte-address forcing either: that is an HLSL
        /// artefact of what SPIRV-Cross emits and has no analogue here.</summary>
        Storage = 1 << 5,

        /// <summary><c>VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT</c>.</summary>
        Indirect = 1 << 6,
    }

    /// <summary>
    /// THE CANONICAL RESTING LAYOUT of decision V-F7, as the backend's own enum. Every texture is assigned one at
    /// CREATION from its usage bits, a command list assumes every texture is at rest when it starts, and
    /// <c>End</c> restores it. That is what makes lists composable in any submit order, which record-time layout
    /// tracking on the texture object cannot deliver (section 2.5).
    /// </summary>
    internal enum VulkanRestingLayout
    {
        /// <summary>A staging texture, which is a <c>VkBuffer</c> here and has no image and therefore no layout at
        /// all (V-C7). Present so the plan for a staging texture is a value rather than a null.</summary>
        None,

        /// <summary><c>VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL</c>, for anything <see cref="GpuTextureUsage.Sampled"/>.
        /// It wins over every other reading, including a render target that is also sampled, which is the common
        /// shape in the post chain.</summary>
        ShaderReadOnlyOptimal,

        /// <summary><c>VK_IMAGE_LAYOUT_GENERAL</c>, for a storage image that is not also sampled, and for the
        /// leftover case below.</summary>
        General,

        /// <summary><c>VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL</c>.</summary>
        ColorAttachmentOptimal,

        /// <summary><c>VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL</c>.</summary>
        DepthStencilAttachmentOptimal,

        /// <summary>
        /// <c>VK_IMAGE_LAYOUT_PRESENT_SRC_KHR</c>, which is NOT reachable from any usage combination and belongs
        /// to exactly one kind of image: a SWAPCHAIN image (https://github.com/APKiwiOrg/KhaozEngine/issues/557).
        /// Its resting layout is where the PRESENTATION ENGINE expects to find it, so a list that rendered into it
        /// leaves it presentable at <c>End</c> and the present needs no submit of its own.
        /// <para>
        /// A TRANSITION OUT OF IT DISCARDS, which is V-F8's second permitted <c>UNDEFINED</c> site and the reason
        /// this is a resting layout at all rather than a barrier the boundary records: an image handed to
        /// <c>vkQueuePresentKHR</c> is next seen through an acquire, whose contents are undefined by
        /// specification. See <see cref="VulkanLayoutTracker"/> for the rule and its one limitation.
        /// </para>
        /// </summary>
        PresentSrcKhr,
    }

    /// <summary>
    /// THE EAGER VIEW SET of decision V-M11, plus the image usage bits and the resting layout, for one texture.
    /// Every field is decided at CREATION and nothing here is deferred to a bind or a draw.
    /// </summary>
    /// <param name="Staging">A <see cref="GpuTextureUsage.Staging"/> texture: a <c>VkBuffer</c> with the
    /// software subresource layout, no image, no view and no layout (V-C7).</param>
    /// <param name="SampledView">A view over the FULL mip chain and every array layer, created when the texture is
    /// <see cref="GpuTextureUsage.Sampled"/> or <see cref="GpuTextureUsage.GenerateMipmaps"/>. Full-chain because
    /// the seam has no texture-view type at all, so nothing can ask for a sub-range.</param>
    /// <param name="AttachmentView">A view at mip 0, layer 0, created when the texture is a
    /// <see cref="GpuTextureUsage.RenderTarget"/> or a <see cref="GpuTextureUsage.DepthStencil"/>. Mip 0 layer 0
    /// is enough because <c>CreateFramebuffer</c> carries no mip or layer parameter and per-face cubemap rendering
    /// is not expressible.</param>
    /// <param name="StorageView">A view at mip 0 over every array layer, created when the texture is
    /// <see cref="GpuTextureUsage.Storage"/>. One mip level because a storage-image binding must cover exactly one,
    /// which the seam's own compute note already says.</param>
    /// <param name="Usage">The image usage bits the <c>VkImage</c> is created with.</param>
    /// <param name="Resting">The canonical resting layout (V-F7).</param>
    /// <param name="Cubemap">Whether the image is cube-compatible, which multiplies the real array layer count by
    /// six and changes the view type.</param>
    internal readonly record struct VulkanTextureViewPlan(
        bool Staging,
        bool SampledView,
        bool AttachmentView,
        bool StorageView,
        VulkanImageUsage Usage,
        VulkanRestingLayout Resting,
        bool Cubemap)
    {
        /// <summary>Whether the image carries a DEPTH aspect rather than a colour one, which decides both the view
        /// aspect mask and the depth reading of the format map. Taken off the usage bits exactly as the incumbent
        /// takes it.</summary>
        internal bool DepthStencil => (Usage & VulkanImageUsage.DepthStencilAttachment) != 0;

        /// <summary>How many <c>vkCreateImageView</c> calls this plan makes at creation. The number V-M11's
        /// bound is stated in, and what a test pins per usage shape.</summary>
        internal int ViewCount
            => (SampledView ? 1 : 0) + (AttachmentView ? 1 : 0) + (StorageView ? 1 : 0);

        /// <summary>
        /// Whether the setup buffer CLEARS this texture to transparent black at creation (V-M10). True for a
        /// colour render target, reproducing <c>VkTexture.ClearIfRenderTarget</c>'s first arm.
        /// <para>
        /// THE CLEAR IS PRESERVED DELIBERATELY, and only the queue submit that carried it is removed. Dropping it
        /// would change what a render target reads before anything writes it, and undefined contents are not
        /// stable across runs while the goldens require stability on the same rasterizer.
        /// </para>
        /// </summary>
        internal bool ClearColorAtCreation => (Usage & VulkanImageUsage.ColorAttachment) != 0;

        /// <summary>
        /// Whether the setup buffer clears this texture to depth 0, stencil 0 at creation. True for a depth target
        /// that is NOT also a colour one, which is the incumbent's <c>else if</c> reproduced: its two arms are
        /// exclusive, so a texture declaring both usages gets the colour clear alone.
        /// </summary>
        internal bool ClearDepthAtCreation
            => (Usage & VulkanImageUsage.ColorAttachment) == 0
                && (Usage & VulkanImageUsage.DepthStencilAttachment) != 0;

        /// <summary>Whether creation records a clear at all, which decides whether the first-ever transition goes
        /// to <c>TRANSFER_DST_OPTIMAL</c> or straight to the resting layout.</summary>
        internal bool ClearsAtCreation => ClearColorAtCreation || ClearDepthAtCreation;

        /// <summary>The REAL array layer count for <paramref name="logicalArrayLayers"/>: six per logical layer on
        /// a cubemap, because a cube face is an array layer to Vulkan. The incumbent's
        /// <c>_actualImageArrayLayers</c>.</summary>
        internal uint ActualArrayLayers(uint logicalArrayLayers)
            => Cubemap ? logicalArrayLayers * 6 : logicalArrayLayers;
    }

    /// <summary>
    /// THE DERIVATION, and nothing else: seam usage bits in, image or buffer usage bits plus the eager view set
    /// plus the resting layout out. Decisions V-M11 and V-F7, section 9.3.
    ///
    /// <para><b>EVERY VIEW IS CREATED AT RESOURCE CREATION, NONE AT A BIND AND NONE AT A DRAW (V-M11).</b> The
    /// bound is real rather than optimistic because THE SEAM CANNOT EXPRESS ANYTHING ELSE: <c>CreateFramebuffer</c>
    /// carries no mip or layer parameter, <c>ResolveTexture</c> is subresource 0 only, and per-face cubemap
    /// rendering is not expressible. Widening any of those is a seam change, and a seam change is where the extra
    /// view would be added.</para>
    ///
    /// <para><b>AND IT IS WORTH RESTATING IN A VULKAN SEAT, where <c>vkCreateImageView</c> looks cheap enough to do
    /// at a bind.</b> All 25 <c>DEVICE_REMOVED</c> stacks in
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/423 surfaced inside the lazy view constructor on the DRAW
    /// PATH, so lazy creation put an allocation on the hot path and put it on the exact path a broken device makes
    /// fail. The enforcement is V-D2's shape rather than a counter: no view factory is reachable from the recording
    /// type, asserted by the architecture test over the type graph, so a draw-time view is a compile error.</para>
    ///
    /// <para><b>THE RESTING LAYOUT IS A PROPERTY OF THE RESOURCE (V-F7), which is why it is decided here.</b>
    /// Sampled wins outright, then storage, then the attachment reading. A texture that is BOTH a render target and
    /// sampled rests in <c>SHADER_READ_ONLY_OPTIMAL</c>, which is the whole post chain, and a list that renders
    /// into it transitions and restores. <see cref="VulkanLayoutTracker"/> consumes this, and
    /// <see cref="VulkanTrackedImage"/> carries it.</para>
    ///
    /// <para><b>TRANSFER SOURCE AND DESTINATION ARE ON EVERY IMAGE AND EVERY BUFFER</b>, reproduced from the
    /// incumbent (<c>VkFormats.VdToVkTextureUsage</c> opens with both, and <c>VkBuffer</c>'s constructor opens with
    /// both). It is what makes a readback, an upload, a creation-time clear and a mip blit legal on any resource
    /// without the seam having to declare an intention it does not have a word for.</para>
    /// </summary>
    internal static class VulkanViewPolicy
    {
        /// <summary>
        /// The eager view set, image usage and resting layout for a texture with <paramref name="usage"/>.
        /// </summary>
        /// <exception cref="ArgumentException"><see cref="GpuTextureUsage.Staging"/> combined with anything else. A
        /// staging texture here is a <c>VkBuffer</c> with no image behind it, so there is no bindable resource for
        /// the other bits to describe, and every staging texture the engine creates passes the bit alone.</exception>
        internal static VulkanTextureViewPlan ForTexture(GpuTextureUsage usage)
        {
            bool staging = (usage & GpuTextureUsage.Staging) != 0;
            if (staging && usage != GpuTextureUsage.Staging)
            {
                throw new ArgumentException(
                    "A staging texture is CPU-mapped and cannot be bound, so GpuTextureUsage.Staging cannot be "
                    + "combined with any other usage. On the native Vulkan backend a staging texture is a VkBuffer "
                    + "with a software subresource layout rather than a linear-tiled image (V-C7), so there is no "
                    + "image for the other bits to describe at all. Read back by copying into a staging texture of "
                    + "its own.", nameof(usage));
            }

            if (staging)
            {
                return new VulkanTextureViewPlan(true, false, false, false, VulkanImageUsage.None,
                    VulkanRestingLayout.None, Cubemap: false);
            }

            bool sampled = (usage & GpuTextureUsage.Sampled) != 0;
            bool mips = (usage & GpuTextureUsage.GenerateMipmaps) != 0;
            bool renderTarget = (usage & GpuTextureUsage.RenderTarget) != 0;
            bool depthStencil = (usage & GpuTextureUsage.DepthStencil) != 0;
            bool storage = (usage & GpuTextureUsage.Storage) != 0;

            // TransferSrc | TransferDst on EVERY image, then one bit per declared usage. Reproduced from
            // VkFormats.VdToVkTextureUsage, which opens with exactly those two and adds the same four.
            VulkanImageUsage image = VulkanImageUsage.TransferSrc | VulkanImageUsage.TransferDst;

            // SAMPLED FOR EITHER REASON, and the second one is not a nicety. A mip-generating texture earns the
            // full-chain sampled view below, and vkCreateImageView REFUSES a view over an image whose usage bits
            // name no view-compatible use at all (VUID-VkImageViewCreateInfo-image-04441): TransferSrc and
            // TransferDst are not among them. Deriving the bit from `sampled` alone while deriving the VIEW from
            // `sampled || mips` made GenerateMipmaps on its own an image whose one view cannot be created.
            if (sampled || mips) image |= VulkanImageUsage.Sampled;
            if (depthStencil) image |= VulkanImageUsage.DepthStencilAttachment;
            if (renderTarget) image |= VulkanImageUsage.ColorAttachment;
            if (storage) image |= VulkanImageUsage.Storage;

            // IT STILL NEEDS NO ATTACHMENT BIT, which is where this backend and Direct3D 11 differ: there
            // GenerateMips is defined through a shader resource view and forces the render-target bind flag onto
            // the resource. Mip generation here is a BLIT CHAIN (row 15), which needs TransferSrc and TransferDst,
            // and both are on every image above.

            return new VulkanTextureViewPlan(
                Staging: false,
                SampledView: sampled || mips,
                AttachmentView: renderTarget || depthStencil,
                StorageView: storage,
                image,
                RestingLayoutFor(sampled, storage, renderTarget, depthStencil),
                Cubemap: (usage & GpuTextureUsage.Cubemap) != 0);
        }

        /// <summary>
        /// The buffer usage bits for <paramref name="usage"/>, reproduced from the incumbent's <c>VkBuffer</c>
        /// constructor.
        /// <para>
        /// THE RING COMBINATION IS NOT REFUSED HERE. <see cref="VulkanBufferRingPolicy.ForBuffer"/> owns that
        /// refusal and is the factory's FIRST statement, so this function is reached only for a usage that has
        /// already been accepted. Two throws for one rule is how the two drift apart.
        /// </para>
        /// </summary>
        internal static VulkanBufferBinding ForBuffer(GpuBufferUsage usage)
        {
            // TransferSrc | TransferDst on EVERY buffer, matching the incumbent, and load-bearing on both ends: a
            // staged upload copies INTO one and a readback copies OUT of one, and the seam declares neither.
            VulkanBufferBinding binding = VulkanBufferBinding.TransferSrc | VulkanBufferBinding.TransferDst;

            // A STAGING BUFFER TAKES THE TRANSFER BITS AND NOTHING ELSE. It is CPU-mapped and never bound, so a
            // binding bit on it would describe a use it cannot have. The incumbent reached the same set by the
            // longer road of testing each bit against a usage that carries none of them.
            if ((usage & GpuBufferUsage.Staging) != 0) return binding;

            if ((usage & GpuBufferUsage.VertexBuffer) != 0) binding |= VulkanBufferBinding.Vertex;
            if ((usage & GpuBufferUsage.IndexBuffer) != 0) binding |= VulkanBufferBinding.Index;
            if ((usage & GpuBufferUsage.UniformBuffer) != 0) binding |= VulkanBufferBinding.Uniform;
            if ((usage & (GpuBufferUsage.StructuredBufferReadOnly | GpuBufferUsage.StructuredBufferReadWrite)) != 0)
                binding |= VulkanBufferBinding.Storage;
            if ((usage & GpuBufferUsage.IndirectBuffer) != 0) binding |= VulkanBufferBinding.Indirect;

            return binding;
        }

        /// <summary>
        /// Which memory ladder a buffer of <paramref name="usage"/> allocates from (section 9.1). A staging buffer
        /// is READBACK, because the seam's only documented use for one is
        /// <see cref="IGpuDevice.Map(IGpuBuffer,GpuMapMode)"/> after a copy, and readback is the one rung that
        /// prefers a CACHED type and makes row 6's invalidate real code. A ring-backed uniform buffer is RING,
        /// which has no fallback at all (V-M4). Everything else is device-local.
        /// <para>
        /// <see cref="GpuBufferUsage.Dynamic"/> deliberately does NOT make a buffer host-visible here, which is one
        /// place this backend differs from the incumbent. On the incumbent a dynamic buffer is host-visible so its
        /// <c>Map</c> can write it, and the only dynamic buffers the engine creates are uniform buffers, which are
        /// RING-BACKED here and host-visible for a better reason. A dynamic vertex buffer would be written through
        /// the staging arena like any other, which costs a copy and buys the device-local read.
        /// </para>
        /// </summary>
        internal static VulkanMemoryUsage MemoryFor(GpuBufferUsage usage)
        {
            if ((usage & GpuBufferUsage.Staging) != 0) return VulkanMemoryUsage.Readback;
            if (VulkanBufferRingPolicy.IsRingBacked(usage)) return VulkanMemoryUsage.Ring;
            return VulkanMemoryUsage.DeviceLocal;
        }

        // V-F7's ladder, in the one order the design states it: sampled, then storage, then the attachment
        // reading. The leftover case is a texture with no binding usage at all, which the seam can express (a
        // transfer-only scratch surface) and which nothing in the engine creates: GENERAL is the layout every
        // access type is legal in, so it is the answer that cannot be wrong for a shape nobody has.
        static VulkanRestingLayout RestingLayoutFor(bool sampled, bool storage, bool renderTarget,
            bool depthStencil)
        {
            if (sampled) return VulkanRestingLayout.ShaderReadOnlyOptimal;
            if (storage) return VulkanRestingLayout.General;
            if (depthStencil) return VulkanRestingLayout.DepthStencilAttachmentOptimal;
            if (renderTarget) return VulkanRestingLayout.ColorAttachmentOptimal;
            return VulkanRestingLayout.General;
        }
    }
}
