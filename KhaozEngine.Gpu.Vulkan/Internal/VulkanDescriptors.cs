using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DEVICE'S WHOLE DESCRIPTOR SUBSYSTEM in one object: the native seam, the two content-dedup caches
    /// (V-D5) and the pools (V-D3). Work-breakdown row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/520).
    ///
    /// <para><b>IT IS ONE OBJECT FOR THE REASON <see cref="VulkanResourceOwner"/> IS.</b> A device that owned two
    /// set-layout caches would hand out two handles for one content and silently break the pointer compare row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) rests on, and a device with two pool managers would
    /// have two budgets over one driver. Bundling them removes the shape of that mistake from every signature
    /// that would otherwise carry them apart.</para>
    ///
    /// <para><b>AND IT IS UNREACHABLE FROM THE RECORDING TYPE, WHICH IS DECISION V-D2 (6.3).</b> Neither
    /// <c>vkAllocateDescriptorSets</c> nor <c>vkUpdateDescriptorSets</c> is a bind, a draw or a barrier, so the
    /// native-call budget sink cannot see either and no counting seam ever will. The enforcement is that
    /// <see cref="VulkanCommandList"/>'s field graph cannot reach this type, asserted by
    /// <c>VulkanRecordingUnreachabilityTests</c> alongside V-M11's image-view claim. The device holds this and
    /// hands it to <see cref="VulkanResourceFactory"/>, which is already on that test's forbidden list, and to
    /// nothing else.</para>
    ///
    /// <para><b>TEARDOWN IS ONE CALL IN ONE ORDER.</b> Pipeline layouts, then set layouts, then pools. A pipeline
    /// layout names set layouts, so it goes first. A pool's sets name a set layout, and destroying a pool
    /// implicitly frees them, so the pool goes last. It runs in the device's teardown window, after the wait that
    /// made the GPU idle and after the retire drain that ran every deferred set free, and before the liveness
    /// flip that turns every native destroy into a no-op.</para>
    /// </summary>
    internal sealed class VulkanDescriptors
    {
        readonly VulkanDescriptorOwner _owner;

        /// <param name="owner">The device's descriptor seam, timeline and retire list.</param>
        /// <param name="maxDynamicUniformBuffers">The device's <c>maxDescriptorSetUniformBuffersDynamic</c>, or 0
        /// when it was never read. 8.3's third defence measures against it at pipeline-layout creation.</param>
        internal VulkanDescriptors(VulkanDescriptorOwner owner, uint maxDynamicUniformBuffers)
        {
            ArgumentNullException.ThrowIfNull(owner);

            _owner = owner;
            SetLayouts = new VulkanDescriptorSetLayoutCache(owner.Api);
            PipelineLayouts = new VulkanPipelineLayoutCache(owner.Api, maxDynamicUniformBuffers);
            Pools = new VulkanDescriptorPoolManager(owner);
        }

        /// <summary>The content-deduplicated <c>VkDescriptorSetLayout</c>s (V-D5).</summary>
        internal VulkanDescriptorSetLayoutCache SetLayouts { get; }

        /// <summary>The content-deduplicated <c>VkPipelineLayout</c>s, and 8.3's third defence. Row 13
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523) is what calls it.</summary>
        internal VulkanPipelineLayoutCache PipelineLayouts { get; }

        /// <summary>The descriptor pools (V-D3), with per-type accounting restored on every counted type.</summary>
        internal VulkanDescriptorPoolManager Pools { get; }

        /// <summary>Create a layout: the shared set-layout handle plus the binding table and the per-type cost.
        /// </summary>
        internal IGpuResourceLayout CreateLayout(in GpuResourceLayoutDescription description)
            => new VulkanResourceLayout(SetLayouts, description);

        /// <summary>Create a set: ONE <c>VkDescriptorSet</c>, allocated and written once (V-D1).</summary>
        internal IGpuResourceSet CreateSet(in GpuResourceSetDescription description)
            => new VulkanResourceSet(_owner.Api, Pools, description);

        /// <summary>
        /// Destroy everything, in the one legal order. Called ONCE, from the device's teardown window. Returns
        /// how many pipeline layouts, set layouts and pools went, for the teardown line.
        /// </summary>
        internal (int PipelineLayouts, int SetLayouts, int Pools) DestroyAll()
        {
            int pipelineLayouts = PipelineLayouts.DestroyAll();
            int setLayouts = SetLayouts.DestroyAll();
            int pools = Pools.DestroyAll();

            return (pipelineLayouts, setLayouts, pools);
        }

        /// <summary>The line a teardown diagnostic quotes, with the dedup hit rate that makes V-D5 observable
        /// rather than asserted.</summary>
        internal string Describe()
            => Pools.Describe()
                + ". " + SetLayouts.RequestCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " resource layouts shared "
                + SetLayouts.DistinctLayoutCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " descriptor set layouts, and "
                + PipelineLayouts.RequestCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " pipelines shared "
                + PipelineLayouts.DistinctPipelineLayoutCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + " pipeline layouts";
    }
}
