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

        /// <param name="framesInFlight">The device depth every ring and pool ring is cut to.</param>
        /// <param name="samplerAnisotropy">Whether the device enabled the feature.</param>
        /// <param name="maxMsaaSampleCount">The MSAA ceiling the texture refusal measures against.</param>
        /// <param name="setupLock">A shared lock, for the nesting assertions.</param>
        /// <param name="maxDynamicUniformBuffers">The device's
        /// <c>maxDescriptorSetUniformBuffersDynamic</c>, which 8.3's third defence measures against at
        /// pipeline-layout creation. 0 degrades to Vulkan's required minimum of 8, exactly as a device whose
        /// limit was never read does.</param>
        internal VulkanResourceFixture(int framesInFlight = 3, bool samplerAnisotropy = true,
            int maxMsaaSampleCount = 1, object? setupLock = null, uint maxDynamicUniformBuffers = 0)
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
            RenderApi = new FakeVulkanRenderApi();
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

            DescriptorApi = new FakeVulkanDescriptorApi();
            DescriptorOwner = new VulkanDescriptorOwner(DescriptorApi, Timeline, Retired);
            Descriptors = new VulkanDescriptors(DescriptorOwner, maxDynamicUniformBuffers);

            ShaderApi = new FakeVulkanShaderApi();
            Modules = new VulkanShaderModuleCache(ShaderApi);

            Factory = new VulkanResourceFactory(Owner, Rings, Setup, Descriptors, Modules,
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

        /// <summary>Row 12's six rendering calls, recorded rather than made. Every list <see cref="CreateList"/>
        /// hands out records into this one, so a test reads the begins, the load ops, the viewport and the scissor
        /// off it (https://github.com/APKiwiOrg/KhaozEngine/issues/522).</summary>
        internal FakeVulkanRenderApi RenderApi { get; }

        internal VulkanBackpressure Backpressure { get; }

        internal VulkanSubmitQueue Submits { get; }

        internal VulkanRingAllocator Rings { get; }

        internal VulkanResourceOwner Owner { get; }

        internal VulkanStagingSource StagingSource { get; }

        internal VulkanSetupCommands Setup { get; }

        internal GpuCapabilities Capabilities { get; }

        internal FakeVulkanDescriptorApi DescriptorApi { get; }

        internal VulkanDescriptorOwner DescriptorOwner { get; }

        /// <summary>The device's ONE descriptor subsystem (row 10): both content-dedup caches and the pools. Wired
        /// on the device's OWN timeline and retire list, so a set's deferred free lands in the same
        /// <see cref="Drain"/> every other deferred destroy does.</summary>
        internal VulkanDescriptors Descriptors { get; }

        internal FakeVulkanShaderApi ShaderApi { get; }

        /// <summary>The device's ONE <c>VkShaderModule</c> cache (row 16), which dedups by SPIR-V hash. Wired on
        /// the fake seam, so a whole shader set can be built and the dedup asserted with no loader.</summary>
        internal VulkanShaderModuleCache Modules { get; }

        internal VulkanResourceFactory Factory { get; }

        /// <summary>A layout description with the shape most shipped renderers declare: one uniform buffer,
        /// optionally declared dynamic.</summary>
        internal static GpuResourceLayoutDescription UniformLayout(bool dynamic = false, string name = "U")
            => new(new GpuResourceLayoutElement(name, GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex,
                dynamic));

        /// <summary>A buffer description with the defaults the uniform call sites use.</summary>
        internal static GpuBufferDescription Buffer(uint sizeInBytes, GpuBufferUsage usage)
            => new(sizeInBytes, usage);

        /// <summary>
        /// A real <see cref="VulkanCommandList"/> on this rig's own fakes, so a recording can be driven against
        /// the SAME descriptor seam the sets were built through. That is what makes decision V-D2's zero-count
        /// assertion meaningful: two rigs would prove nothing about the one that recorded.
        /// <para>
        /// NO UPLOADER BY DEFAULT: the record-time write into a ring-backed uniform buffer needs none, and the
        /// real <see cref="VulkanListUploads"/> needs a real <c>Vk</c> that no device-free rig has. A test of the
        /// BULK leg passes its own <paramref name="uploads"/>, which is also how the rendering scope the list
        /// hands its uploader is observed.
        /// </para>
        /// <para>
        /// IT DOES GET THE RENDERING SEAM, because that one is device-free by construction: every argument it
        /// takes is a handle or one of this backend's own records, so <see cref="RenderApi"/> records what a
        /// recording asked for and the whole deferred begin is drivable here.
        /// </para>
        /// </summary>
        /// <param name="uploads">The list's staging uploader, or null for a list that records no bulk write.</param>
        internal VulkanCommandList CreateList(IVulkanRecordUploads? uploads = null)
            => new(new VulkanCommandPoolRing(CommandApi, FramesInFlight, Timeline, Backpressure), Retired, uploads,
                render: RenderApi);

        /// <summary>
        /// A resource set over <paramref name="description"/> with a freshly created resource of the right kind
        /// at every element, so a whole shipped layout shape can be built without naming its resources one by
        /// one. Everything created is appended to <paramref name="owned"/> for the caller to dispose.
        /// </summary>
        internal IGpuResourceSet CreateSetFor(in GpuResourceLayoutDescription description,
            List<IDisposable> owned)
        {
            ArgumentNullException.ThrowIfNull(owned);

            IGpuResourceLayout layout = Factory.CreateResourceLayout(description);
            owned.Add(layout);

            GpuResourceLayoutElement[] elements = description.Elements ?? [];
            var resources = new IGpuBindableResource[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                IGpuBindableResource resource = elements[i].Kind switch
                {
                    GpuResourceKind.UniformBuffer => Factory.CreateBuffer(
                        Buffer(256, GpuBufferUsage.UniformBuffer)),
                    GpuResourceKind.StructuredBufferReadOnly => Factory.CreateBuffer(
                        Buffer(256, GpuBufferUsage.StructuredBufferReadOnly)),
                    GpuResourceKind.StructuredBufferReadWrite => Factory.CreateBuffer(
                        Buffer(256, GpuBufferUsage.StructuredBufferReadWrite)),
                    GpuResourceKind.TextureReadOnly => Factory.CreateTexture(
                        Texture(8, 8, GpuTextureUsage.Sampled)),
                    GpuResourceKind.TextureReadWrite => Factory.CreateTexture(
                        Texture(8, 8, GpuTextureUsage.Storage)),
                    GpuResourceKind.Sampler => Factory.CreateSampler(GpuSamplerDescription.Linear),
                    _ => throw new NotSupportedException("A resource kind this rig cannot build: "
                        + elements[i].Kind),
                };

                owned.Add((IDisposable)resource);
                resources[i] = resource;
            }

            IGpuResourceSet set = Factory.CreateResourceSet(new GpuResourceSetDescription(layout, resources));
            owned.Add(set);
            return set;
        }

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
