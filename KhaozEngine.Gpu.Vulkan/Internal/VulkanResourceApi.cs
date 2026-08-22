using System;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE TWELVE REAL DRIVER CALLS BEHIND <see cref="IVulkanResourceApi"/>, and nothing else. Everything that
    /// decides anything is above this line, in <see cref="VulkanViewPolicy"/>,
    /// <see cref="VulkanStagingLayout"/>, <see cref="VulkanSamplerPolicy"/> and
    /// <see cref="VulkanBufferRingPolicy"/>, which is what makes the usage derivation, the eager view set, the
    /// resting layout, the staging arithmetic and the sampler mapping testable with no loader.
    ///
    /// <para><b>EVERY RESULT-RETURNING CALL GOES THROUGH THE LOSS LATCH FIRST AND THEN THROUGH
    /// <see cref="VulkanResultCodes.Require"/>, in every configuration.</b> The spec names every creation call
    /// among those that can return <c>VK_ERROR_DEVICE_LOST</c>, and the incumbent's <c>VulkanUtil.CheckResult</c>
    /// is <c>[Conditional("DEBUG")]</c>, so a Release build of it takes a lost device back from a
    /// <c>vkCreateImage</c> and carries on with a handle that is not one.</para>
    ///
    /// <para><b>EVERY DESTROY IS SKIPPED ON A DEAD DEVICE</b>, through the same liveness token every other destroy
    /// in this package is gated on, and none of them returns a result to check. <c>vkDestroyDevice</c> (or the loss
    /// that killed it) already destroyed every object made from the device, so a destroy afterwards is a call
    /// against freed memory, which aborts the process through the Vulkan loader rather than failing quietly.</para>
    ///
    /// <para><b>TWO DEPARTURES FROM THE INCUMBENT, BOTH OF THEM ITS BUGS.</b> An image is created with
    /// <c>VK_IMAGE_LAYOUT_UNDEFINED</c> rather than <c>PREINITIALIZED</c>: preinitialized means the HOST has
    /// already written the memory, which is a statement about a linear-tiled image a host can write and is
    /// meaningless for the optimal-tiled images this backend creates, and it is the layout every one of them is
    /// then transitioned out of anyway (V-F8). And the memory-requirements call is the <c>2</c> form
    /// unconditionally, with no run-time probe and no 1.0 fallback, because this backend requires Vulkan 1.3 where
    /// both it and <c>VkMemoryDedicatedRequirements</c> are core.</para>
    /// </summary>
    internal sealed unsafe class VulkanResourceApi : IVulkanResourceApi
    {
        readonly Vk _vk;
        readonly Device _device;
        readonly VulkanDeviceLossLatch _loss;
        readonly IDeviceLiveness _liveness;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="device">The device that owns every resource made here and outlives them all.</param>
        /// <param name="loss">The device's loss latch, which every result here is checked against.</param>
        /// <param name="liveness">The device's liveness token, which gates every destroy.</param>
        internal VulkanResourceApi(Vk vk, Device device, VulkanDeviceLossLatch loss,
            IDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(loss);
            ArgumentNullException.ThrowIfNull(liveness);

            _vk = vk;
            _device = device;
            _loss = loss;
            _liveness = liveness;
        }

        /// <inheritdoc/>
        public ulong CreateBuffer(ulong sizeBytes, VulkanBufferBinding binding)
        {
            var createInfo = new BufferCreateInfo(
                sType: StructureType.BufferCreateInfo,
                size: sizeBytes,
                usage: VulkanFormats.ToBufferUsage(binding),
                // EXCLUSIVE, because this backend creates exactly one queue on one family (V-N5), so there is no
                // second family for a concurrent sharing mode to name and concurrent sharing costs bandwidth on
                // some drivers for nothing.
                sharingMode: SharingMode.Exclusive);

            Result created = _vk.CreateBuffer(_device, in createInfo, null, out Buffer buffer);
            Check(created, "vkCreateBuffer", "create a buffer");
            return buffer.Handle;
        }

        /// <inheritdoc/>
        public VulkanResourceRequirements BufferRequirements(ulong buffer)
        {
            var info = new BufferMemoryRequirementsInfo2(
                sType: StructureType.BufferMemoryRequirementsInfo2,
                buffer: new Buffer(buffer));

            var dedicated = new MemoryDedicatedRequirements(sType: StructureType.MemoryDedicatedRequirements);
            var requirements = new MemoryRequirements2(
                sType: StructureType.MemoryRequirements2, pNext: &dedicated);

            _vk.GetBufferMemoryRequirements2(_device, in info, &requirements);
            return Translate(requirements.MemoryRequirements, dedicated);
        }

        /// <inheritdoc/>
        public void BindBufferMemory(ulong buffer, ulong memory, ulong offset)
            => Check(_vk.BindBufferMemory(_device, new Buffer(buffer), new DeviceMemory(memory), offset),
                "vkBindBufferMemory", "bind a buffer to its memory");

        /// <inheritdoc/>
        public void DestroyBuffer(ulong buffer)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyBuffer(_device, new Buffer(buffer), null);
        }

        /// <inheritdoc/>
        public ulong CreateImage(in VulkanImageSpec spec)
        {
            // THE CUBEMAP EXPANSION, reproduced from the incumbent's _actualImageArrayLayers: a cubemap's logical
            // layer count is multiplied by six, because a cube face IS an array layer to Vulkan.
            uint layers = spec.Cubemap ? spec.ArrayLayers * 6 : spec.ArrayLayers;

            ImageCreateFlags flags = ImageCreateFlags.CreateMutableFormatBit;
            if (spec.Cubemap) flags |= ImageCreateFlags.CreateCubeCompatibleBit;

            var createInfo = new ImageCreateInfo(
                sType: StructureType.ImageCreateInfo,
                flags: flags,
                // The seam expresses 2D textures, 2D arrays and cubemaps and nothing else, so there is no image
                // type to derive: a cubemap is a 2D image with six layers and the cube-compatible flag.
                imageType: ImageType.Type2D,
                format: VulkanFormats.ToVkFormat(spec.Format, spec.DepthStencil),
                extent: new Extent3D(spec.Width, spec.Height, 1),
                mipLevels: spec.MipLevels,
                arrayLayers: layers,
                samples: VulkanFormats.ToSampleCount(spec.SampleCount),
                tiling: ImageTiling.Optimal,
                usage: VulkanFormats.ToImageUsage(spec.Usage),
                sharingMode: SharingMode.Exclusive,
                // UNDEFINED, not PREINITIALIZED. See the class note: the incumbent's choice describes a
                // host-written linear image, and every image here is transitioned out of this layout by the setup
                // buffer before anything reads it (V-F8).
                initialLayout: ImageLayout.Undefined);

            Result created = _vk.CreateImage(_device, in createInfo, null, out Image image);
            Check(created, "vkCreateImage", "create an image");
            return image.Handle;
        }

        /// <inheritdoc/>
        public VulkanResourceRequirements ImageRequirements(ulong image)
        {
            var info = new ImageMemoryRequirementsInfo2(
                sType: StructureType.ImageMemoryRequirementsInfo2,
                image: new Image(image));

            var dedicated = new MemoryDedicatedRequirements(sType: StructureType.MemoryDedicatedRequirements);
            var requirements = new MemoryRequirements2(
                sType: StructureType.MemoryRequirements2, pNext: &dedicated);

            _vk.GetImageMemoryRequirements2(_device, in info, &requirements);
            return Translate(requirements.MemoryRequirements, dedicated);
        }

        /// <inheritdoc/>
        public void BindImageMemory(ulong image, ulong memory, ulong offset)
            => Check(_vk.BindImageMemory(_device, new Image(image), new DeviceMemory(memory), offset),
                "vkBindImageMemory", "bind an image to its memory");

        /// <inheritdoc/>
        public void DestroyImage(ulong image)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyImage(_device, new Image(image), null);
        }

        /// <inheritdoc/>
        public ulong CreateImageView(in VulkanImageViewSpec spec)
        {
            // The cubemap expansion again, on the RANGE this time, reproduced from VkTextureView's
            // "subresourceRange.layerCount *= 6".
            uint layers = spec.Cubemap ? spec.ArrayLayers * 6 : spec.ArrayLayers;
            uint baseLayer = spec.Cubemap ? spec.BaseArrayLayer * 6 : spec.BaseArrayLayer;

            var createInfo = new ImageViewCreateInfo(
                sType: StructureType.ImageViewCreateInfo,
                image: new Image(spec.Image),
                viewType: VulkanFormats.ToViewType(spec.Cubemap, spec.ArrayLayers, spec.ArrayView),
                format: VulkanFormats.ToVkFormat(spec.Format, spec.DepthStencil),
                // IDENTITY SWIZZLE, which is what the incumbent leaves the components at by never touching them.
                // A swizzle here would silently reorder every channel of every sampled read.
                components: new ComponentMapping(
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                subresourceRange: new ImageSubresourceRange(
                    VulkanFormats.ToAspect(spec.DepthStencil),
                    spec.BaseMipLevel, spec.MipLevels, baseLayer, layers));

            Result created = _vk.CreateImageView(_device, in createInfo, null, out ImageView view);
            Check(created, "vkCreateImageView", "create an image view");
            return view.Handle;
        }

        /// <inheritdoc/>
        public void DestroyImageView(ulong view)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyImageView(_device, new ImageView(view), null);
        }

        /// <inheritdoc/>
        public ulong CreateSampler(in VulkanSamplerSpec spec)
        {
            VulkanFormats.GetFilterParams(spec.Filter, out Filter min, out Filter mag, out SamplerMipmapMode mip);

            var createInfo = new SamplerCreateInfo(
                sType: StructureType.SamplerCreateInfo,
                magFilter: mag,
                minFilter: min,
                mipmapMode: mip,
                addressModeU: VulkanFormats.ToAddressMode(spec.AddressU),
                addressModeV: VulkanFormats.ToAddressMode(spec.AddressV),
                addressModeW: VulkanFormats.ToAddressMode(spec.AddressW),
                mipLodBias: spec.MipLodBias,
                anisotropyEnable: spec.AnisotropyEnable,
                maxAnisotropy: spec.MaxAnisotropy,
                // NO COMPARISON SAMPLER ANYWHERE. The seam cannot ask for one and the engine's shadow path does
                // manual PCF, which is why the incumbent's compareEnable is driven by a nullable that is always
                // null on this engine's call sites.
                compareEnable: false,
                compareOp: CompareOp.Never,
                minLod: spec.MinLod,
                maxLod: spec.MaxLod,
                borderColor: VulkanFormats.TransparentBlackBorder,
                unnormalizedCoordinates: false);

            Result created = _vk.CreateSampler(_device, in createInfo, null, out Sampler sampler);
            Check(created, "vkCreateSampler", "create a sampler");
            return sampler.Handle;
        }

        /// <inheritdoc/>
        public void DestroySampler(ulong sampler)
        {
            if (_liveness.IsDead) return;

            _vk.DestroySampler(_device, new Sampler(sampler), null);
        }

        static VulkanResourceRequirements Translate(in MemoryRequirements requirements,
            in MemoryDedicatedRequirements dedicated)
            => new(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits,
                dedicated.PrefersDedicatedAllocation, dedicated.RequiresDedicatedAllocation);

        // THE LATCH FIRST, so the site's own name is what the telemetry header carries, and then the plain result
        // check. One body rather than six copies, because six copies is how one of them ends up unchecked.
        void Check(Result result, string call, string what)
        {
            if (_loss.Check(result, call))
            {
                throw new InvalidOperationException(
                    $"The native Vulkan backend could not {what}, because the device was LOST. The loss itself is "
                    + "in the session log and in the telemetry session header, with the call that first noticed "
                    + "it.");
            }

            VulkanResultCodes.Require(result, call);
        }
    }
}
