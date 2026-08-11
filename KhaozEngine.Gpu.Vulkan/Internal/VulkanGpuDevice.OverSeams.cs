using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DEVICE-FREE CONSTRUCTION HOOK, and the one caller that passes no instance lease. It holds the factory
    /// and NOTHING the shipped paths depend on: <see cref="Instance"/>, which every device with no lease is read
    /// through, lives on the main partial beside the field it guards, because teardown and command-list creation
    /// are shipped members and a shipped member's dependency does not belong in a file named for a test hook. It
    /// exists because <see cref="VulkanGpuDevice"/>'s constructor is private and reachable only through
    /// <c>CreateHeadless</c> and <c>CreateForWindow</c>, both of which need a Vulkan loader, so until this landed
    /// NO TEST ANYWHERE CONSTRUCTED THIS TYPE and every claim about the device's own WIRING was carried by
    /// inspection (https://github.com/APKiwiOrg/KhaozEngine/issues/550).
    ///
    /// <para><b>WHAT IT IS FOR: the wiring, and nothing below it.</b> The subsystems this device assembles all
    /// have their own device-free suites already, driven through <c>VulkanResourceFixture</c>, which builds the
    /// same graph out of the same fakes and never builds the device. What that rig cannot see is the half-dozen
    /// lines INSIDE the device that decide the ORDER those subsystems are called in: that every device-level read
    /// flushes the setup command buffer before it reads (V-M10), and that a READ map then drains the timeline
    /// (V-C8). Both are one call each, both are invisible to every test that drives the subsystems directly, and
    /// both fail silently in the field as an intermittently wrong golden.</para>
    ///
    /// <para><b>WHY A FACTORY RATHER THAN A WIDER CONSTRUCTOR.</b> Nothing here weakens the shipped surface: the
    /// constructor stays private, every seam stays internal, and the only difference between the device this
    /// builds and the device <c>CreateHeadless</c> builds is that this one holds NO INSTANCE LEASE and NO DISK
    /// PIPELINE CACHE. Both absences are honest rather than convenient. There is no loader under a test device,
    /// so there are no entry points to hand a command list (see <see cref="Instance"/>), and the disk cache's
    /// open also prunes, which no test may do to a developer's cache folder.</para>
    ///
    /// <para><b>IT IS THE FIRST OF ITS KIND IN THE THREE NATIVE BACKENDS.</b> Neither <c>MetalGpuDevice</c> nor
    /// <c>D3D11GpuDevice</c> has a device-over-fakes hook: both are covered by real-device <c>[GpuFact]</c>s on
    /// their own leg plus device-free suites over their subsystems, which is exactly the split that left this gap
    /// here. A backend adding one should copy this shape rather than invent a second: one internal factory, on the
    /// device's own partial, taking the seams the constructor already takes.</para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice
    {
        /// <summary>
        /// Build a device over the seams a caller supplies, with NO Vulkan loader, no instance lease, no
        /// swapchain and no disk pipeline cache. Everything the device itself assembles (the block allocator, the
        /// submit queue, the uniform ring, the resource owner, the staging source, the setup command buffer, the
        /// descriptors, the module cache, the pipelines, the resource factory and the shared sampler pair) is the
        /// REAL type wired exactly as the shipped constructor wires it, because the wiring is the whole point.
        /// <para>
        /// THE CALLER OWNS THE TIMELINE AND THE LIVENESS TOKEN, and they must be the same pair: the timeline is
        /// built over the caller's fake semaphore so a drain is observable, and the liveness token the device
        /// takes has to be the one that timeline reads or a dead-device test would flip only half the device.
        /// </para>
        /// <para>
        /// A DEVICE FROM HERE DISPOSES THROUGH THE TEARDOWN THAT TOUCHES NOTHING NATIVE, and routes itself there:
        /// <see cref="Dispose"/> branches on the missing lease, not on the liveness token, because its native path
        /// calls <c>vkDeviceWaitIdle</c> and <c>vkDestroyDevice</c> through <see cref="Instance"/>. A caller marks
        /// nothing dead first. Leaving it undisposed is equally fine: every handle under it is a fake's integer.
        /// </para>
        /// </summary>
        /// <param name="liveness">The device's liveness token, and the one the timeline was built on.</param>
        /// <param name="timeline">The device's completion timeline, over whatever semaphore the caller wants to
        /// observe.</param>
        /// <param name="memoryApi">The <c>vkAllocateMemory</c> seam.</param>
        /// <param name="memoryFacts">The memory types the allocator chooses from.</param>
        /// <param name="commands">The command-pool and <c>vkQueueSubmit</c> seam, which is where a flush becomes
        /// observable as a submission.</param>
        /// <param name="resourceApi">The buffer, image, view and sampler seam.</param>
        /// <param name="setupSink">The setup command buffer's <c>vkCmd*</c> seam.</param>
        /// <param name="descriptorApi">The descriptor pool, layout and set seam.</param>
        /// <param name="shaderApi">The <c>vkCreateShaderModule</c> seam.</param>
        /// <param name="pipelineApi">The pipeline and pipeline-cache seam.</param>
        /// <param name="capabilities">What the device reports about itself.</param>
        /// <param name="framesInFlight">The device depth every pool ring and uniform ring is cut to.</param>
        /// <param name="maxDynamicUniformBuffers">The device's <c>maxDescriptorSetUniformBuffersDynamic</c>, or 0
        /// to degrade to Vulkan's required minimum exactly as a device whose limit was never read does.</param>
        internal static VulkanGpuDevice CreateOverSeams(
            DeviceLiveness liveness,
            VulkanTimeline timeline,
            IVulkanDeviceMemoryApi memoryApi,
            VulkanMemoryFacts memoryFacts,
            IVulkanCommandApi commands,
            IVulkanResourceApi resourceApi,
            IVulkanSetupSink setupSink,
            IVulkanDescriptorApi descriptorApi,
            IVulkanShaderApi shaderApi,
            IVulkanPipelineApi pipelineApi,
            GpuCapabilities capabilities,
            int framesInFlight = 3,
            uint maxDynamicUniformBuffers = 0)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(timeline);

            return new VulkanGpuDevice(
                instance: null,
                device: default,
                graphicsQueue: default,
                graphicsQueueFamily: 0,
                capabilities,
                softwareAdapter: false,
                liveness,
                new VulkanDeviceLossLatch(liveness),
                timeline,
                memoryApi,
                memoryFacts,
                commands,
                resourceApi,
                setupSink,
                descriptorApi,
                shaderApi,
                pipelineApi,
                pipelineCache: null,
                maxDynamicUniformBuffers,
                framesInFlight,
                windowed: null);
        }
    }
}
