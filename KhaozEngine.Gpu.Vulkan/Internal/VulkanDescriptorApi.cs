using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE NINE REAL DRIVER CALLS BEHIND <see cref="IVulkanDescriptorApi"/>, and nothing else. Everything that
    /// decides anything is above this line, in <see cref="VulkanDescriptorPolicy"/>,
    /// <see cref="VulkanDescriptorPoolBudget"/>, <see cref="VulkanDescriptorSetLayoutCache"/>,
    /// <see cref="VulkanPipelineLayoutCache"/> and <see cref="VulkanResourceSet"/>, which is what makes the type
    /// mapping, the dedup, the pool sizing, the per-type accounting and the bind window all testable with no
    /// loader.
    ///
    /// <para><b>EVERY RESULT-RETURNING CALL GOES THROUGH THE LOSS LATCH FIRST AND THEN THROUGH
    /// <see cref="VulkanResultCodes.Require"/>, in every configuration</b>, exactly as
    /// <see cref="VulkanResourceApi"/>'s do. <c>vkAllocateDescriptorSets</c> is among the calls that can return
    /// <c>VK_ERROR_DEVICE_LOST</c>, and it is also the one call here whose FAILURE is routine rather than fatal
    /// on a wrongly sized pool, so a Release build that skipped the check would carry on with a handle that is
    /// not one.</para>
    ///
    /// <para><b>EVERY DESTROY IS SKIPPED ON A DEAD DEVICE</b>, through the same liveness token every other
    /// destroy in this package is gated on. That covers <c>vkFreeDescriptorSets</c> too, which is not a destroy
    /// in the spec's vocabulary but is one in this backend's: it names a pool that went with the device.</para>
    ///
    /// <para><b>NO PUSH CONSTANT RANGES AND NO DESCRIPTOR SET LAYOUT FLAGS (V-D8).</b> No descriptor indexing, no
    /// bindless, no <c>UPDATE_AFTER_BIND</c>, no descriptor buffers. Section 8.4 argues the decline against the
    /// idiomatic grain and names the trigger that reopens it, and the absence of push constants is what keeps row
    /// 11's compatibility computation a pure set-layout prefix compare.</para>
    /// </summary>
    internal sealed unsafe class VulkanDescriptorApi : IVulkanDescriptorApi
    {
        // ABOVE THIS MANY ELEMENTS THE NATIVE ARRAYS BELOW MOVE TO THE HEAP. Every one of them is sized from a
        // consumer-supplied count (a layout's binding count, a pipeline layout's set-layout count, a set's write
        // count), and a stackalloc has no bound the CLR can catch: past the thread's stack it corrupts memory
        // instead of throwing, where the heap array below fails safely with an OutOfMemoryException. No shipped
        // call site is anywhere near this: the largest is 7 elements.
        const int StackallocThreshold = 32;

        readonly Vk _vk;
        readonly Device _device;
        readonly VulkanDeviceLossLatch _loss;
        readonly IVulkanDeviceLiveness _liveness;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="device">The device that owns every object made here and outlives them all.</param>
        /// <param name="loss">The device's loss latch, which every result here is checked against.</param>
        /// <param name="liveness">The device's liveness token, which gates every destroy and every free.</param>
        internal VulkanDescriptorApi(Vk vk, Device device, VulkanDeviceLossLatch loss,
            IVulkanDeviceLiveness liveness)
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
        public ulong CreateSetLayout(ReadOnlySpan<VulkanDescriptorBinding> bindings)
        {
            // One extra slot so a zero-binding layout still has a valid pointer to hand over. An empty layout is
            // real: the seam permits new GpuResourceLayoutDescription() and two shipped tests use one.
            int count = bindings.Length + 1;
            Span<DescriptorSetLayoutBinding> nativeSpan = count <= StackallocThreshold
                ? stackalloc DescriptorSetLayoutBinding[count]
                : new DescriptorSetLayoutBinding[count];

            fixed (DescriptorSetLayoutBinding* native = nativeSpan)
            {
                for (int i = 0; i < bindings.Length; i++)
                {
                    native[i] = new DescriptorSetLayoutBinding(
                        binding: bindings[i].Binding,
                        descriptorType: VulkanFormats.ToDescriptorType(bindings[i].Type),
                        descriptorCount: bindings[i].DescriptorCount,
                        stageFlags: VulkanFormats.ToShaderStages(bindings[i].Stages),
                        // NULL. Immutable samplers would bake a VkSampler into the layout, which would make two
                        // layouts with the same shape and different samplers different objects and break the
                        // content dedup that V-D5 rests on. The engine binds its samplers as descriptors like
                        // everything else.
                        pImmutableSamplers: null);
                }

                var createInfo = new DescriptorSetLayoutCreateInfo(
                    sType: StructureType.DescriptorSetLayoutCreateInfo,
                    // NO FLAGS (V-D8). UPDATE_AFTER_BIND_POOL and PUSH_DESCRIPTOR are both
                    // descriptor-indexing-shaped features this backend declines.
                    flags: DescriptorSetLayoutCreateFlags.None,
                    bindingCount: (uint)bindings.Length,
                    pBindings: native);

                Result created = _vk.CreateDescriptorSetLayout(
                    _device, in createInfo, null, out DescriptorSetLayout layout);
                Check(created, "vkCreateDescriptorSetLayout", "create a descriptor set layout");
                return layout.Handle;
            }
        }

        /// <inheritdoc/>
        public void DestroySetLayout(ulong setLayout)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyDescriptorSetLayout(_device, new DescriptorSetLayout(setLayout), null);
        }

        /// <inheritdoc/>
        public ulong CreatePipelineLayout(ReadOnlySpan<ulong> setLayouts)
        {
            int count = setLayouts.Length + 1;
            Span<DescriptorSetLayout> nativeSpan = count <= StackallocThreshold
                ? stackalloc DescriptorSetLayout[count]
                : new DescriptorSetLayout[count];

            fixed (DescriptorSetLayout* native = nativeSpan)
            {
                for (int i = 0; i < setLayouts.Length; i++) native[i] = new DescriptorSetLayout(setLayouts[i]);

                var createInfo = new PipelineLayoutCreateInfo(
                    sType: StructureType.PipelineLayoutCreateInfo,
                    setLayoutCount: (uint)setLayouts.Length,
                    pSetLayouts: native,
                    // ZERO PUSH CONSTANT RANGES, which is decision V-D8 and not an omission. See the class note.
                    pushConstantRangeCount: 0,
                    pPushConstantRanges: null);

                Result created = _vk.CreatePipelineLayout(_device, in createInfo, null, out PipelineLayout layout);
                Check(created, "vkCreatePipelineLayout", "create a pipeline layout");
                return layout.Handle;
            }
        }

        /// <inheritdoc/>
        public void DestroyPipelineLayout(ulong pipelineLayout)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyPipelineLayout(_device, new PipelineLayout(pipelineLayout), null);
        }

        /// <inheritdoc/>
        public ulong CreatePool(in VulkanDescriptorPoolSize size)
        {
            DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[VulkanDescriptorCounts.CountedTypes.Length];

            uint used = 0;
            foreach (VulkanDescriptorType type in VulkanDescriptorCounts.CountedTypes)
            {
                uint count = size.Counts.CountOf(type);

                // A ZERO-COUNT ENTRY IS ILLEGAL (VUID-VkDescriptorPoolSize-descriptorCount-00302), so a type this
                // pool holds none of contributes no entry rather than an entry of zero. That is also why the
                // budget is carried as a value beside the pool rather than read back off the create-info.
                if (count == 0) continue;

                sizes[used++] = new DescriptorPoolSize(VulkanFormats.ToDescriptorType(type), count);
            }

            var createInfo = new DescriptorPoolCreateInfo(
                sType: StructureType.DescriptorPoolCreateInfo,
                // FREE_DESCRIPTOR_SET, which is what makes vkFreeDescriptorSets legal at all (V-D3). Without it a
                // pool can only be reset wholesale, and a resource set's own Dispose could not release anything.
                flags: DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                maxSets: size.MaxSets,
                poolSizeCount: used,
                pPoolSizes: sizes);

            Result created = _vk.CreateDescriptorPool(_device, in createInfo, null, out DescriptorPool pool);
            Check(created, "vkCreateDescriptorPool", "create a descriptor pool");
            return pool.Handle;
        }

        /// <inheritdoc/>
        public void DestroyPool(ulong pool)
        {
            if (_liveness.IsDead) return;

            _vk.DestroyDescriptorPool(_device, new DescriptorPool(pool), null);
        }

        /// <inheritdoc/>
        public ulong AllocateSet(ulong pool, ulong setLayout)
        {
            var layout = new DescriptorSetLayout(setLayout);

            var allocateInfo = new DescriptorSetAllocateInfo(
                sType: StructureType.DescriptorSetAllocateInfo,
                descriptorPool: new DescriptorPool(pool),
                // EXACTLY ONE (V-D1). One IGpuResourceSet is one VkDescriptorSet for its whole life.
                descriptorSetCount: 1,
                pSetLayouts: &layout);

            Result allocated = _vk.AllocateDescriptorSets(_device, in allocateInfo, out DescriptorSet set);
            Check(allocated, "vkAllocateDescriptorSets", "allocate a descriptor set");
            return set.Handle;
        }

        /// <inheritdoc/>
        public void FreeSet(ulong pool, ulong set)
        {
            if (_liveness.IsDead) return;

            var handle = new DescriptorSet(set);

            // The result IS checked, unlike the destroys above, because vkFreeDescriptorSets returns one and a
            // failure here means the pool's accounting and the driver's have diverged.
            Check(_vk.FreeDescriptorSets(_device, new DescriptorPool(pool), 1, in handle),
                "vkFreeDescriptorSets", "free a descriptor set");
        }

        /// <inheritdoc/>
        public void UpdateSet(ulong set, ReadOnlySpan<VulkanDescriptorWrite> writes)
        {
            if (writes.Length == 0) return;

            Span<WriteDescriptorSet> nativeSpan = writes.Length <= StackallocThreshold
                ? stackalloc WriteDescriptorSet[writes.Length]
                : new WriteDescriptorSet[writes.Length];
            Span<DescriptorBufferInfo> bufferSpan = writes.Length <= StackallocThreshold
                ? stackalloc DescriptorBufferInfo[writes.Length]
                : new DescriptorBufferInfo[writes.Length];
            Span<DescriptorImageInfo> imageSpan = writes.Length <= StackallocThreshold
                ? stackalloc DescriptorImageInfo[writes.Length]
                : new DescriptorImageInfo[writes.Length];

            fixed (WriteDescriptorSet* native = nativeSpan)
            fixed (DescriptorBufferInfo* buffers = bufferSpan)
            fixed (DescriptorImageInfo* images = imageSpan)
            {
                for (int i = 0; i < writes.Length; i++)
                {
                    VulkanDescriptorWrite write = writes[i];

                    native[i] = new WriteDescriptorSet(
                        sType: StructureType.WriteDescriptorSet,
                        dstSet: new DescriptorSet(set),
                        dstBinding: write.Binding,
                        dstArrayElement: 0,
                        descriptorCount: 1,
                        descriptorType: VulkanFormats.ToDescriptorType(write.Type));

                    if (VulkanDescriptorPolicy.IsBuffer(write.Type))
                    {
                        // THE RANGE IS THE BIND WINDOW (V-M6): never VK_WHOLE_SIZE and never the ring stride. The
                        // decision is VulkanResourceSet's and this line only carries it across.
                        buffers[i] = new DescriptorBufferInfo(
                            new Buffer(write.Buffer), write.BufferOffset, write.BufferRange);
                        native[i].PBufferInfo = &buffers[i];
                        continue;
                    }

                    if (write.Type == VulkanDescriptorType.Sampler)
                    {
                        images[i] = new DescriptorImageInfo(
                            new Sampler(write.Sampler), default, ImageLayout.Undefined);
                        native[i].PImageInfo = &images[i];
                        continue;
                    }

                    images[i] = new DescriptorImageInfo(
                        default, new ImageView(write.ImageView),
                        VulkanFormats.ToDescriptorImageLayout(write.ImageLayout));
                    native[i].PImageInfo = &images[i];
                }

                // ONE CALL COVERING EVERY BINDING (V-D1), made exactly once in a set's life. It returns nothing,
                // so there is no result to latch: a malformed write is a validation-layer error rather than a
                // code.
                _vk.UpdateDescriptorSets(_device, (uint)writes.Length, native, 0, null);
            }
        }

        // The latch first, so the site's own name is what the telemetry header carries, and then the plain result
        // check. One body rather than five copies, because five copies is how one of them ends up unchecked.
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
