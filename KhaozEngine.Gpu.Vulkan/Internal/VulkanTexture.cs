using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuTexture"/> on the native Vulkan backend, in its two entirely different shapes.
    ///
    /// <para><b>A NORMAL TEXTURE IS A <c>VkImage</c> WITH EVERY VIEW IT WILL EVER NEED ALREADY MADE (V-M11).</b>
    /// A full-chain sampled view if it is sampled or generates mips, an attachment view at mip 0 layer 0 if it is a
    /// render target or a depth target, and a storage view at mip 0 if it is a storage image.
    /// <see cref="VulkanViewPolicy"/> decides which, and the bound is real rather than optimistic because the seam
    /// cannot express anything else. Nothing here is deferred to a bind or a draw, and no view factory is reachable
    /// from the recording type at all: all 25 <c>DEVICE_REMOVED</c> stacks in
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/423 surfaced inside a lazy view constructor on the draw
    /// path.</para>
    ///
    /// <para><b>IT IS ALSO ASSIGNED ITS CANONICAL RESTING LAYOUT AT CREATION (V-F7)</b>, and the device's setup
    /// command buffer records the first-ever transition into it, plus the creation-time clear a render target
    /// gets. NO QUEUE SUBMIT HAPPENS HERE (V-M10): the incumbent issued a whole <c>vkQueueSubmit</c> per render
    /// target and per sampled texture created, which is two hundred submissions to load a scene with two hundred
    /// textures.</para>
    ///
    /// <para><b>A STAGING TEXTURE IS A <c>VkBuffer</c> AND HAS NO IMAGE, NO VIEW AND NO LAYOUT AT ALL (V-C7).</b>
    /// That is the incumbent's shape reproduced exactly, and it is the highest-risk parity surface in the backend:
    /// its subresource layout is computed IN SOFTWARE by <see cref="VulkanStagingLayout"/> rather than read back
    /// from the driver, every golden in the suite reads through it, and a different arithmetic garbles all of them
    /// at once.</para>
    ///
    /// <para><b>DISPOSAL IS ONE TERMINAL RETIRE (V-F9), AND THE WORD TERMINAL IS THE DESIGN DECISION.</b> The
    /// single held entry destroys this texture's image views INLINE, then its image (or its staging buffer), then
    /// frees its suballocation. It does not re-retire the views as entries of their own. A compound resource whose
    /// destroy retired another destroy that then freed an allocation would append a third generation of retirement
    /// after the teardown drain had taken its snapshot, and the chunk that generation freed would never be
    /// returned. Destroying children inline keeps the depth at the one generation the device's two teardown drains
    /// already cover, which is the choice row 6's review asked this row to make and to write down. See
    /// <see cref="VulkanResourceOwner.RetireTerminal"/>.</para>
    /// </summary>
    internal sealed class VulkanTexture : IGpuTexture
    {
        readonly VulkanResourceOwner _owner;
        readonly VulkanMemoryAllocation _allocation;

        bool _disposed;

        /// <param name="owner">The device's resource seam, allocator, timeline and retire list.</param>
        /// <param name="setup">The device's setup command buffer, which every non-staging texture appends its
        /// first-ever transition and its creation-time clear to. Null only for a staging texture, which records
        /// nothing at all.</param>
        /// <param name="description">The seam's description.</param>
        internal VulkanTexture(VulkanResourceOwner owner, VulkanSetupCommands? setup,
            in GpuTextureDescription description)
        {
            ArgumentNullException.ThrowIfNull(owner);
            RequireShape(description);

            _owner = owner;

            Width = description.Width;
            Height = description.Height;
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            IsArray = description.IsArray;
            SampleCount = description.SampleCount;
            Format = description.Format;
            Usage = description.Usage;

            // THROWS on a usage that combines Staging with anything else, before anything native is made.
            Plan = VulkanViewPolicy.ForTexture(description.Usage);

            StagingShape = new VulkanStagingShape(
                description.Width, description.Height, description.MipLevels, description.ArrayLayers,
                description.Format);

            if (Plan.Staging)
            {
                _allocation = CreateStagingBuffer(owner);
                return;
            }

            if (setup is null)
            {
                throw new ArgumentNullException(nameof(setup),
                    "A native Vulkan texture that is not a staging texture needs the device's setup command "
                    + "buffer: its first-ever layout transition out of UNDEFINED and its creation-time clear are "
                    + "appended there (V-M10). A texture built without one would be left in UNDEFINED, which every "
                    + "command list assumes it is not.");
            }

            _allocation = CreateImage(owner, setup);
        }

        /// <inheritdoc/>
        public uint Width { get; }

        /// <inheritdoc/>
        public uint Height { get; }

        /// <inheritdoc/>
        public uint MipLevels { get; }

        /// <inheritdoc/>
        public uint SampleCount { get; }

        /// <inheritdoc/>
        public GpuPixelFormat Format { get; }

        /// <summary>The LOGICAL array layer count, before the cubemap expansion.</summary>
        internal uint ArrayLayers { get; }

        /// <summary>Whether the seam asked for an ARRAY, which the layer count alone cannot say at one layer
        /// (#666). Carried onto the sampled and storage views so a one-layer array reaches an array-declaring
        /// shader as <c>VK_IMAGE_VIEW_TYPE_2D_ARRAY</c>.</summary>
        internal bool IsArray { get; }

        /// <summary>The usage the seam asked for.</summary>
        internal GpuTextureUsage Usage { get; }

        /// <summary>The eager view set, the image usage bits and the resting layout, all decided at creation.
        /// </summary>
        internal VulkanTextureViewPlan Plan { get; }

        /// <summary>The <c>VkImage</c>, or 0 on a staging texture, which has none.</summary>
        internal ulong Image { get; private set; }

        /// <summary>The staging <c>VkBuffer</c>, or 0 on a normal texture, which has none.</summary>
        internal ulong StagingBuffer { get; private set; }

        /// <summary>The full-chain sampled view, or 0 when the texture is neither sampled nor mip-generating.
        /// </summary>
        internal ulong SampledView { get; private set; }

        /// <summary>The mip 0, layer 0 attachment view, or 0 when the texture is not an attachment.</summary>
        internal ulong AttachmentView { get; private set; }

        /// <summary>The mip 0 storage view, or 0 when the texture is not a storage image.</summary>
        internal ulong StorageView { get; private set; }

        /// <summary>The canonical resting layout this texture is created in and every list restores it to (V-F7).
        /// </summary>
        internal VulkanRestingLayout Resting => Plan.Resting;

        /// <summary>Whether this is a staging texture, which is a <c>VkBuffer</c> rather than an image.</summary>
        internal bool IsStaging => Plan.Staging;

        /// <summary>The shape its software subresource layout is computed from (V-C7). Meaningful for a staging
        /// texture and carried for every texture, because a copy INTO a staging texture needs the destination's
        /// shape and a copy out of a normal one needs its own dimensions.</summary>
        internal VulkanStagingShape StagingShape { get; }

        /// <summary>The suballocation, which the readback path invalidates before handing a pointer out on a
        /// non-coherent memory type.</summary>
        internal VulkanMemoryAllocation Allocation => _allocation;

        /// <summary>The mapped address of a staging texture's first byte, or zero for a texture whose memory is
        /// not host-visible. Stable for the texture's life (V-M3).</summary>
        internal nint MappedPointer => _allocation.MappedPointer;

        /// <summary>True once disposed, whether or not the deferred destroy has run yet.</summary>
        internal bool IsDisposed => _disposed;

        /// <summary>The REAL array layer count, six per logical layer on a cubemap.</summary>
        internal uint ActualArrayLayers => Plan.ActualArrayLayers(ArrayLayers);

        /// <summary>
        /// Retire the texture behind the timeline (V-F9), as ONE terminal entry: every image view inline, then the
        /// image or the staging buffer, then the suballocation. See the class note for why nothing here re-retires
        /// a child.
        /// <para>
        /// IDEMPOTENT, because a consumer disposing a texture twice is a teardown-order accident rather than a
        /// defect.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            VulkanResourceOwner owner = _owner;
            VulkanMemoryAllocation allocation = _allocation;
            ulong image = Image;
            ulong staging = StagingBuffer;
            ulong sampled = SampledView;
            ulong attachment = AttachmentView;
            ulong storage = StorageView;

            owner.RetireTerminal(() =>
            {
                // VIEWS FIRST AND INLINE. A view outliving its image is undefined behaviour, and a view retired as
                // its own entry would be exactly that for one drain's worth of time.
                if (sampled != 0) owner.Api.DestroyImageView(sampled);
                if (attachment != 0) owner.Api.DestroyImageView(attachment);
                if (storage != 0) owner.Api.DestroyImageView(storage);

                if (image != 0) owner.Api.DestroyImage(image);
                if (staging != 0) owner.Api.DestroyBuffer(staging);

                if (allocation.IsValid) owner.Memory.Free(allocation);
            });
        }

        /// <summary>The texture as this backend's own, or a named refusal for one another backend made.</summary>
        internal static VulkanTexture Require(IGpuTexture? texture, string what)
            => texture as VulkanTexture
                ?? throw new ArgumentException(
                    $"The texture handed to {what} was not created by the native Vulkan backend, so it carries no "
                    + "VkImage and no VkImageView. Create it through the same IGpuDevice.Factory.",
                    nameof(texture));

        /// <summary>The diagnostic line a refusal quotes.</summary>
        internal string Describe()
            => Width.ToString(CultureInfo.InvariantCulture)
                + "x" + Height.ToString(CultureInfo.InvariantCulture)
                + " " + Format + " " + Usage + " texture";

        // The staging shape: one VkBuffer sized by the incumbent's own arithmetic, out of the readback ladder, in
        // a LINEAR chunk. No image, no view, no layout and nothing appended to the setup buffer.
        VulkanMemoryAllocation CreateStagingBuffer(VulkanResourceOwner owner)
        {
            StagingBuffer = owner.Api.CreateBuffer(
                VulkanStagingLayout.TotalBytes(StagingShape),
                VulkanBufferBinding.TransferSrc | VulkanBufferBinding.TransferDst);

            // OUTSIDE THE TRY, for the reason the image path's is: a catch that cannot see the allocation cannot
            // free it.
            VulkanMemoryAllocation allocation = default;

            try
            {
                VulkanResourceRequirements requirements = owner.Api.BufferRequirements(StagingBuffer);

                allocation = owner.Memory.Allocate(new VulkanMemoryRequest(
                    requirements.Size,
                    requirements.Alignment,
                    requirements.MemoryTypeBits,
                    // READBACK, which is the one ladder that prefers a CACHED type and therefore the one place
                    // row 6's invalidate is real code rather than a defensive branch. It is also the incumbent's
                    // own preference for a staging texture, host-cached first and coherent alone as the fallback.
                    VulkanMemoryUsage.Readback,
                    VulkanMemoryTiling.Linear,
                    requirements.PrefersDedicated,
                    requirements.RequiresDedicated,
                    new VulkanDedicatedTarget(Buffer: StagingBuffer, Image: 0)));

                owner.Api.BindBufferMemory(StagingBuffer, allocation.Memory, allocation.Offset);
                return allocation;
            }
            catch
            {
                owner.Api.DestroyBuffer(StagingBuffer);
                if (allocation.IsValid) owner.Memory.Free(allocation);
                StagingBuffer = 0;
                throw;
            }
        }

        // The image shape: one VkImage out of the device-local ladder in an OPTIMAL chunk, every eager view, and
        // the setup buffer append. The order matters on the failure path, which is why every native object is
        // assigned to a field as it is made.
        VulkanMemoryAllocation CreateImage(VulkanResourceOwner owner, VulkanSetupCommands setup)
        {
            Image = owner.Api.CreateImage(new VulkanImageSpec(
                Width, Height, MipLevels, ArrayLayers, Format, Plan.DepthStencil, Plan.Usage, SampleCount,
                Plan.Cubemap));

            // OUTSIDE THE TRY, so the catch below can free it. See that catch for what a leak here would cost.
            VulkanMemoryAllocation allocation = default;

            try
            {
                VulkanResourceRequirements requirements = owner.Api.ImageRequirements(Image);

                allocation = owner.Memory.Allocate(new VulkanMemoryRequest(
                    requirements.Size,
                    requirements.Alignment,
                    requirements.MemoryTypeBits,
                    VulkanMemoryUsage.DeviceLocal,
                    // OPTIMAL, which is the pool key's second half: an optimal-tiled image never shares a chunk
                    // with a linear resource, so bufferImageGranularity is satisfied with no arithmetic (V-M2).
                    VulkanMemoryTiling.Optimal,
                    requirements.PrefersDedicated,
                    requirements.RequiresDedicated,
                    new VulkanDedicatedTarget(Buffer: 0, Image: Image)));

                owner.Api.BindImageMemory(Image, allocation.Memory, allocation.Offset);

                CreateViews(owner);

                // THE ONLY THING TEXTURE CREATION RECORDS, and it records rather than submits (V-M10). The FORMAT
                // travels with it because the barrier and the clear name every aspect the format has, which on a
                // combined depth-stencil format is both planes and not just depth.
                setup.Prepare(new VulkanImageSetup(
                    Image, Plan.DepthStencil, Format, MipLevels, ActualArrayLayers,
                    Plan.ClearColorAtCreation, Plan.ClearDepthAtCreation, Plan.Resting));

                return allocation;
            }
            catch
            {
                if (SampledView != 0) owner.Api.DestroyImageView(SampledView);
                if (AttachmentView != 0) owner.Api.DestroyImageView(AttachmentView);
                if (StorageView != 0) owner.Api.DestroyImageView(StorageView);
                owner.Api.DestroyImage(Image);

                // THE ALLOCATION TOO, which is why it is declared outside the try. A catch that destroyed the
                // image and left its suballocation behind would leak that memory for the process's life, and the
                // block allocator would never hand it out again. Freed LAST, in the order this type's own
                // terminal destroy uses, and the same hoist VulkanBuffer's and VulkanStagingSource's failure
                // paths already take.
                if (allocation.IsValid) owner.Memory.Free(allocation);

                SampledView = AttachmentView = StorageView = 0;
                Image = 0;
                throw;
            }
        }

        // The eager view set (V-M11), whose RANGES are the whole of the decision: the full chain and every layer
        // for sampling, mip 0 layer 0 for an attachment, mip 0 and every layer for a storage image.
        void CreateViews(VulkanResourceOwner owner)
        {
            if (Plan.SampledView)
            {
                SampledView = owner.Api.CreateImageView(new VulkanImageViewSpec(
                    Image, Format, Plan.DepthStencil, Plan.Cubemap, 0, MipLevels, 0, ArrayLayers, IsArray));
            }

            if (Plan.AttachmentView)
            {
                // MIP 0, LAYER 0, and NOT a cube view: an attachment is one 2D image plane, and a cube view cannot
                // be bound as a render target. CreateFramebuffer carries no mip or layer parameter, so there is
                // nothing else this view could be asked to be.
                AttachmentView = owner.Api.CreateImageView(new VulkanImageViewSpec(
                    Image, Format, Plan.DepthStencil, Cubemap: false, 0, 1, 0, 1));
            }

            if (Plan.StorageView)
            {
                // ONE MIP LEVEL, which is what a storage-image binding must cover, and every layer so an
                // image2DArray binding works. Not a cube view either, for the same reason as the attachment.
                StorageView = owner.Api.CreateImageView(new VulkanImageViewSpec(
                    Image, Format, Plan.DepthStencil, Cubemap: false, 0, 1, 0, ActualArrayLayers, IsArray));
            }
        }

        static void RequireShape(in GpuTextureDescription description)
        {
            if (description.Width != 0 && description.Height != 0 && description.MipLevels != 0
                && description.ArrayLayers != 0)
            {
                return;
            }

            throw new ArgumentOutOfRangeException(nameof(description), description.Width,
                "A native Vulkan texture needs a non-zero width, height, mip level count and array layer count. "
                + "vkCreateImage rejects a zero in any of them, and a staging texture with one would be a "
                + "zero-byte buffer.");
        }
    }
}
