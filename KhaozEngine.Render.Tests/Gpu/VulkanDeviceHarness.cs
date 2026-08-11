using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A REAL <see cref="VulkanGpuDevice"/> ON THE SAME FAKES EVERY OTHER DEVICE-FREE VULKAN SUITE USES, built
    /// through <c>VulkanGpuDevice.CreateOverSeams</c>. It is the rig for the claims that live INSIDE the device
    /// rather than inside a subsystem: that every device-level read flushes the setup command buffer before it
    /// reads (V-M10), and that a READ map then drains the timeline (V-C8).
    ///
    /// <para><b>THE DIFFERENCE FROM <see cref="VulkanResourceFixture"/> IS THE DEVICE, AND THAT IS THE WHOLE
    /// POINT.</b> That rig assembles the same subsystems out of the same fakes and drives them directly, which is
    /// right for everything the subsystems decide and blind to the order the DEVICE calls them in. Here the device
    /// owns its own allocator, submit queue, ring, staging source, setup buffer, descriptors, pipelines and
    /// factory, exactly as the shipped constructor builds them, and the only objects a test holds are the fakes at
    /// the bottom. Nothing here duplicates a fake: every one of them is the file beside this one.</para>
    ///
    /// <para><b>THE LIST IS BUILT HERE RATHER THAN THROUGH THE FACTORY.</b> A device with no loader cannot create
    /// a command list (the five recording seams are <c>Vk</c> entry points), so <see cref="RecordedList"/> makes
    /// the same shape the device would, on the device's OWN timeline, retire list, backpressure accumulator and
    /// pool depth. A list that shared none of those would submit against a second timeline and prove nothing
    /// about this device's ordering.</para>
    /// </summary>
    internal sealed class VulkanDeviceFixture : IDisposable
    {
        const VulkanMemoryTrait Local = VulkanMemoryTrait.DeviceLocal;
        const VulkanMemoryTrait Visible = VulkanMemoryTrait.HostVisible;
        const VulkanMemoryTrait Coherent = VulkanMemoryTrait.HostCoherent;
        const VulkanMemoryTrait Cached = VulkanMemoryTrait.HostCached;

        /// <param name="framesInFlight">The device depth every pool ring and uniform ring is cut to.</param>
        internal VulkanDeviceFixture(int framesInFlight = 3)
        {
            FramesInFlight = framesInFlight;

            Liveness = new DeviceLiveness();
            Semaphore = new FakeVulkanTimelineSemaphore();
            Timeline = new VulkanTimeline(Semaphore, Liveness);
            MemoryApi = new FakeVulkanDeviceMemoryApi();
            CommandApi = new FakeVulkanCommandApi();
            ResourceApi = new FakeVulkanResourceApi();
            SetupSink = new FakeVulkanSetupSink();
            DescriptorApi = new FakeVulkanDescriptorApi();
            ShaderApi = new FakeVulkanShaderApi();
            PipelineApi = new FakeVulkanPipelineApi();

            // The same three-type ladder VulkanResourceFixture declares: one device-local, one coherent
            // host-visible, one cached host-visible, which is what the readback rung prefers.
            var facts = new VulkanMemoryFacts(
                [new(0, 0, Local), new(1, 1, Visible | Coherent), new(2, 1, Visible | Coherent | Cached)],
                NonCoherentAtomSize: 64,
                MaxAllocationCount: 4096);

            Device = VulkanGpuDevice.CreateOverSeams(
                Liveness, Timeline, MemoryApi, facts, CommandApi, ResourceApi, SetupSink, DescriptorApi,
                ShaderApi, PipelineApi, Capabilities, framesInFlight);
        }

        /// <summary>The device under test: a real one, wired exactly as the shipped constructor wires it.</summary>
        internal VulkanGpuDevice Device { get; }

        internal int FramesInFlight { get; }

        /// <summary>The device's liveness token. Flipped by <see cref="Dispose"/>, because the live teardown path
        /// calls <c>vkDeviceWaitIdle</c> through entry points this device does not have.</summary>
        internal DeviceLiveness Liveness { get; }

        /// <summary>The timeline semaphore, which is where a DRAIN becomes observable: <c>WaitCount</c> and
        /// <c>LastWaitValue</c> are what V-C8's wait looks like from below.</summary>
        internal FakeVulkanTimelineSemaphore Semaphore { get; }

        internal VulkanTimeline Timeline { get; }

        internal FakeVulkanDeviceMemoryApi MemoryApi { get; }

        /// <summary>The command seam, which is where a FLUSH becomes observable: the setup buffer's batch reaches
        /// the queue as a submission on this fake, tagged with the pool it came out of.</summary>
        internal FakeVulkanCommandApi CommandApi { get; }

        internal FakeVulkanResourceApi ResourceApi { get; }

        /// <summary>The setup buffer's <c>vkCmd*</c> seam, which is where the clear a flush carries is read.
        /// </summary>
        internal FakeVulkanSetupSink SetupSink { get; }

        internal FakeVulkanDescriptorApi DescriptorApi { get; }

        internal FakeVulkanShaderApi ShaderApi { get; }

        internal FakeVulkanPipelineApi PipelineApi { get; }

        /// <summary>What the device reports about itself. The members that matter here are the two the resource
        /// factory gates on: anisotropy for the shared samplers and the MSAA ceiling for a texture refusal.
        /// </summary>
        internal static GpuCapabilities Capabilities { get; } = new(
            clipSpaceYInverted: false,
            depthRangeZeroToOne: true,
            deviceName: "Fake Vulkan device",
            samplerAnisotropy: true,
            samplerLodBias: true,
            maxMsaaSampleCount: 1,
            supportsShadowMaps: true,
            supportsCompute: true,
            supportsCompletionFences: true);

        /// <summary>
        /// Leave the setup command buffer with something OPEN, by creating a render target: its creation-time
        /// clear and its first-ever layout transition are appended and nothing is submitted (V-M10). This is the
        /// state every flush claim is about, and the returned texture is the caller's to dispose.
        /// </summary>
        internal IGpuTexture OpenSetupWork()
            => Device.Factory.CreateTexture(new GpuTextureDescription(
                4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));

        /// <summary>A staging texture, which is a host-visible <c>VkBuffer</c> with no image, so creating one
        /// records nothing into the setup buffer.</summary>
        internal IGpuTexture StagingTexture()
            => Device.Factory.CreateTexture(new GpuTextureDescription(
                4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));

        /// <summary>A staging buffer, for the buffer half of the map pair.</summary>
        internal IGpuBuffer StagingBuffer()
            => Device.Factory.CreateBuffer(new GpuBufferDescription(256, GpuBufferUsage.Staging));

        /// <summary>
        /// A sealed command list on the DEVICE's own timeline, retire list, backpressure accumulator and depth,
        /// ready for <c>Submit</c>. Built here rather than through the factory, for the reason in the type's
        /// summary.
        /// </summary>
        internal VulkanCommandList RecordedList()
        {
            var list = new VulkanCommandList(
                new VulkanCommandPoolRing(CommandApi, FramesInFlight, Timeline, Device.Backpressure),
                Device.Retired);

            list.Begin();
            list.End();
            return list;
        }

        /// <summary>
        /// Kill the device before disposing it. <see cref="VulkanGpuDevice.Dispose"/>'s live path calls
        /// <c>vkDeviceWaitIdle</c> and <c>vkDestroyDevice</c> through entry points a device built over fake seams
        /// does not have, so the dead path is the only one this rig can take. Everything it skips is a native
        /// destroy of a fake's integer.
        /// </summary>
        public void Dispose()
        {
            Liveness.MarkDead();
            Device.Dispose();
        }
    }
}
