using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE-FREE RIG FOR ROW 9: the fake native seams, the REAL timeline, retire list, block allocator, ring
    /// allocator, submit queue, staging source, setup command buffer and resource factory, wired exactly as
    /// <c>VulkanGpuDevice</c> wires them.
    ///
    /// <para><b>ONLY THE TWO NATIVE SEAMS ARE FAKED.</b> Everything the row actually decides is the real type:
    /// which usage bits a resource gets, which eager views are created and over what range, which memory ladder it
    /// allocates from, what its resting layout is, what the setup buffer records, when a deferred destroy runs, and
    /// what a staging map answers with. A rig that faked any of those would be testing a second implementation.</para>
    ///
    /// <para><b>THE MAPPED POINTERS ARE FAKE ADDRESSES</b>, so a test may read a ring's or a staging texture's
    /// mapped base and check the arithmetic against it, and must never write through one. The one path that really
    /// writes is driven against real pinned memory in <c>VulkanStagingMapTests</c>.</para>
    /// </summary>
    internal sealed class VulkanResourceFixture
    {
        const VulkanMemoryTrait Local = VulkanMemoryTrait.DeviceLocal;
        const VulkanMemoryTrait Visible = VulkanMemoryTrait.HostVisible;
        const VulkanMemoryTrait Coherent = VulkanMemoryTrait.HostCoherent;
        const VulkanMemoryTrait Cached = VulkanMemoryTrait.HostCached;

        internal VulkanResourceFixture(int framesInFlight = 3, bool samplerAnisotropy = true,
            int maxMsaaSampleCount = 1, object? setupLock = null)
        {
            FramesInFlight = framesInFlight;
            SubmitLock = new object();
            SetupLock = setupLock ?? new object();

            Liveness = new FakeVulkanLiveness();
            Semaphore = new FakeVulkanTimelineSemaphore();
            Timeline = new VulkanTimeline(Semaphore, Liveness);
            Retired = new VulkanRetireList();
            MemoryApi = new FakeVulkanDeviceMemoryApi();

            var facts = new VulkanMemoryFacts(
                [new(0, 0, Local), new(1, 1, Visible | Coherent), new(2, 1, Visible | Coherent | Cached)],
                NonCoherentAtomSize: 64,
                MaxAllocationCount: 4096);

            Memory = new VulkanMemoryAllocator(MemoryApi, facts, new VulkanTimelineRetirement(Timeline, Retired),
                chunkSize: 1024 * 1024, dedicatedThreshold: 4 * 1024 * 1024);

            ResourceApi = new FakeVulkanResourceApi();
            SetupSink = new FakeVulkanSetupSink();
            CommandApi = new FakeVulkanCommandApi();
            Backpressure = new VulkanBackpressure();

            Submits = new VulkanSubmitQueue(CommandApi, Timeline, SubmitLock);
            Rings = new VulkanRingAllocator(framesInFlight, Timeline, Backpressure, SubmitLock);

            Owner = new VulkanResourceOwner(ResourceApi, Memory, Timeline, Retired);
            StagingSource = new VulkanStagingSource(Owner, Liveness);

            Setup = new VulkanSetupCommands(
                new VulkanCommandPoolRing(CommandApi, framesInFlight, Timeline, Backpressure),
                SetupSink,
                new VulkanStagingArena(StagingSource, framesInFlight),
                Submits,
                Liveness,
                SetupLock);

            Capabilities = new GpuCapabilities(
                clipSpaceYInverted: false,
                depthRangeZeroToOne: true,
                deviceName: "Fake Vulkan device",
                samplerAnisotropy: samplerAnisotropy,
                samplerLodBias: true,
                maxMsaaSampleCount: maxMsaaSampleCount,
                supportsShadowMaps: true,
                supportsCompute: true,
                supportsCompletionFences: true);

            Factory = new VulkanResourceFactory(Owner, Rings, Setup,
                () => throw new NotSupportedException("This rig has no command list."),
                () => Timeline.CreateFence(),
                Capabilities);
        }

        internal int FramesInFlight { get; }

        internal object SubmitLock { get; }

        internal object SetupLock { get; }

        internal FakeVulkanLiveness Liveness { get; }

        internal FakeVulkanTimelineSemaphore Semaphore { get; }

        internal VulkanTimeline Timeline { get; }

        internal VulkanRetireList Retired { get; }

        internal FakeVulkanDeviceMemoryApi MemoryApi { get; }

        internal VulkanMemoryAllocator Memory { get; }

        internal FakeVulkanResourceApi ResourceApi { get; }

        internal FakeVulkanSetupSink SetupSink { get; }

        internal FakeVulkanCommandApi CommandApi { get; }

        internal VulkanBackpressure Backpressure { get; }

        internal VulkanSubmitQueue Submits { get; }

        internal VulkanRingAllocator Rings { get; }

        internal VulkanResourceOwner Owner { get; }

        internal VulkanStagingSource StagingSource { get; }

        internal VulkanSetupCommands Setup { get; }

        internal GpuCapabilities Capabilities { get; }

        internal VulkanResourceFactory Factory { get; }

        /// <summary>Run every deferred destroy the timeline has passed. Nothing has usually been submitted, so
        /// <see cref="VulkanTimeline.CompletedValue"/> releases everything, which is the retire list's own
        /// documented behaviour rather than a shortcut.</summary>
        internal int Drain() => Retired.Drain(Timeline.CompletedValue);

        /// <summary>A texture description with the defaults every shipped call site uses.</summary>
        internal static GpuTextureDescription Texture(uint width, uint height, GpuTextureUsage usage,
            GpuPixelFormat format = GpuPixelFormat.R8G8B8A8UNorm, uint mipLevels = 1, uint arrayLayers = 1,
            uint sampleCount = 1)
            => new(width, height, format, usage, mipLevels, arrayLayers, sampleCount);

        /// <summary>Every image view created so far, which is decision V-M11's whole observable surface.</summary>
        internal IReadOnlyList<VulkanImageViewSpec> Views => ResourceApi.Views;
    }
}
