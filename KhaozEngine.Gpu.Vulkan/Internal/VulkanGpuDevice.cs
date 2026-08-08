using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The engine's native Vulkan device as the GPU seam sees it: a real <c>VkDevice</c> and a real graphics
    /// queue, on the shared refcounted instance, that creates and destroys cleanly.
    /// <para>
    /// <b>NOT EVERY MEMBER IS BUILT YET, and each one that is not names the row that builds it.</b> This is
    /// work-breakdown row 4 of <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>: the instance, the
    /// device, the queue, the selective feature enable, the device-loss latch, the liveness token and the
    /// validation pump. The swapchain is row 17, so the two members that row owns throw a message saying so rather
    /// than returning something that fails later somewhere less informative.
    /// The Direct3D 11 package's <c>D3D11ResourceFactory</c> landed the same way and its doc paragraph was
    /// rewritten at every fill-in, which is the discipline this paragraph is under too: it is a ledger, and a
    /// stale one is worse than none.
    /// </para>
    /// <para>
    /// <b>THE MEMBERS THAT ARE LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> (in the part that can
    /// be read honestly, see below), <see cref="Diagnostics"/> with both of its fields, <see cref="Counters"/> (in
    /// the drain, backpressure and off-timeline halves, see below), both <c>Submit</c> overloads, all three
    /// <c>UpdateBuffer</c> overloads at BOTH levels, <see cref="Factory"/>, <see cref="PointSampler"/>,
    /// <see cref="LinearSampler"/>, both <c>UpdateTexture</c> overloads, both <c>Map</c> and <c>Unmap</c> pairs,
    /// <see cref="WaitForIdle"/>, and <see cref="Dispose"/>. What remains unbuilt on this type is the swapchain
    /// pair, <see cref="ResizeSwapchain"/> and <see cref="Present"/>.
    /// </para>
    /// <para>
    /// <b>RESOURCES ARE LIVE, AND SO IS THE SETUP COMMAND BUFFER THEY APPEND TO</b> (row 9,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/519). <see cref="Factory"/> hands out real buffers (a
    /// uniform buffer arrives ring-backed, everything else with device-local memory), real textures (a
    /// <c>VkImage</c> with every image view it will ever need already made and its canonical resting layout
    /// assigned, or a <c>VkBuffer</c> with the incumbent's software subresource layout when it is a staging
    /// texture), real samplers, real command lists and real fences. Framebuffers, shaders and
    /// pipelines still refuse by naming their own row. NO CREATION SUBMITS ANYTHING: the clear and the first-ever
    /// transition go into ONE device-owned setup command buffer under its own short lock, flushed at the next
    /// submit or at any device-level read. See <c>VulkanGpuDevice.Resources.cs</c> and
    /// <see cref="VulkanSetupCommands"/>.
    /// </para>
    /// <para>
    /// <b>DESCRIPTORS ARE LIVE TOO</b> (row 10, https://github.com/APKiwiOrg/KhaozEngine/issues/520). The device
    /// owns one <see cref="VulkanDescriptors"/>: content-deduplicated <c>VkDescriptorSetLayout</c>s and
    /// <c>VkPipelineLayout</c>s (V-D5, which is what makes row 11's compatibility test a pointer compare), and
    /// descriptor pools sized from actual demand whose free path restores EVERY counted type including both
    /// dynamic ones (V-D3). <c>CreateResourceLayout</c> and <c>CreateResourceSet</c> hand out real objects: one
    /// <c>VkDescriptorSet</c> allocated and written once at creation with the bind window as its range (V-M6).
    /// The subsystem hangs off THIS object and off the resource factory and off nothing a recorder can reach,
    /// which is decision V-D2's structural enforcement rather than a preference. Row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523) is the first caller of the pipeline-layout cache and
    /// of the dynamic-uniform limit check that lives with it.
    /// </para>
    /// <para>
    /// <b>THE COMMAND PATH IS LIVE IN ITS LIFECYCLE AND IN ONE RECORDING MEMBER</b> (rows 7 and 8,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/517 and
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/518). <c>CreateCommandList</c> hands out a real
    /// <see cref="VulkanCommandList"/> with its own per-slot <c>VkCommandPool</c>s, <c>Begin</c> and <c>End</c>
    /// work, <c>Submit</c> is ONE <c>vkQueueSubmit</c> under one short lock that allocates and signals the
    /// timeline value inside it, and a record-time <c>UpdateBuffer</c> on a ring-backed uniform buffer is a memcpy
    /// into that frame's segment. Everything else a list could record names the row that builds it. A list is
    /// reachable through the SEAM from row 9 onward, as <c>IGpuResourceFactory.CreateCommandList</c>, and it now
    /// arrives with its own staging arena so a record-time write to a NON-uniform buffer stages and copies rather
    /// than refusing. See <c>VulkanGpuDevice.Submit.cs</c>.
    /// </para>
    /// <para>
    /// <b>THE UNIFORM RING IS LIVE AND HAS BUFFERS IN IT</b> (row 8,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/518). The device owns one
    /// <see cref="VulkanRingAllocator"/>: the device-wide frame segment, the completion gate that recycles it into
    /// the same backpressure accumulator the command lists stall into, and the off-timeline write's pending-patch
    /// queue. Row 9's <c>CreateBuffer</c> cuts a ring for every uniform buffer, after
    /// <see cref="VulkanBufferRingPolicy.ForBuffer"/> has answered. What still has no caller is the FRAME BOUNDARY
    /// that rotates the segment, which is row 17's <see cref="Present"/>.
    /// </para>
    /// <para>
    /// <b>THE COMPLETION TIMELINE IS LIVE AND IS NOW REACHABLE THROUGH THE SEAM</b> (row 5,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/515). The device owns one timeline <c>VkSemaphore</c>, a
    /// real <c>IGpuFence</c> over it and the deferred-disposal retire list, and <see cref="WaitForIdle"/> is the
    /// counted <c>vkWaitSemaphores</c> drain on it. Row 9's <c>IGpuResourceFactory.CreateFence</c> hands out
    /// <see cref="VulkanTimeline.CreateFence"/>'s fences, which is the last thing that subsystem was waiting for.
    /// <c>SupportsCompletionFences</c> was already true in this device's partial capability read, and it is now
    /// backed by a real primitive rather than by a promise about one.
    /// </para>
    /// <para>
    /// <b>THE BLOCK SUBALLOCATOR IS LIVE AND EVERY RESOURCE ALLOCATES OUT OF IT</b> (row 6,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/516). The device owns one
    /// <see cref="VulkanMemoryAllocator"/>: chunks pooled by <c>(memoryTypeIndex, linear|optimal)</c>, first-fit
    /// with alignment correction and coalescing free, persistent whole-chunk mapping, dedicated allocations on
    /// driver preference or above a size threshold, and flush and invalidate on the non-coherent path. Row 9 binds
    /// every <c>VkBuffer</c> and every <c>VkImage</c> to one of its offsets, on the ladder
    /// <see cref="VulkanViewPolicy.MemoryFor"/> chooses. The retire path was wired from that row, so a chunk that
    /// empties is returned behind the timeline rather than freed underneath a submission.
    /// </para>
    /// <para>
    /// <b><see cref="Capabilities"/> IS PARTIAL AND SAYS WHICH PART.</b> Row 18
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/528) owns the capability read and the ZERO-permitted-
    /// difference parity test against the incumbent. What this row fills is everything readable off a device with
    /// no renderer on it, and <c>MaxMsaaSampleCount</c> is pinned to 1 rather than guessed, because the incumbent's
    /// own computation is what row 18 reproduces and a number invented here would be a silent lie that
    /// <c>AntiAliasing.ResolveFor</c> would act on. Nothing selects this backend, so a conservative 1 costs
    /// nothing and an invented value would cost the parity test its meaning.
    /// </para>
    /// <para>
    /// <b>TEARDOWN CALLS <c>vkDeviceWaitIdle</c> FIRST</b> (V-F10), unlike the incumbent, which destroys the
    /// memory manager and the pools and only then waits. Everything the device owns is destroyed after that wait
    /// and before the liveness token flips, and only then is the real device destroyed, so no wrapper can observe
    /// "alive" after the object it would destroy has gone. <see cref="Dispose"/> carries the full order.
    /// </para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice : IGpuDevice
    {
        static readonly ILogger log = Log.For<VulkanGpuDevice>();

        readonly VulkanInstanceLease<VulkanInstance> _instance;
        readonly VulkanDeviceLiveness _liveness;
        readonly VulkanDeviceLossLatch _loss;
        readonly VulkanTimeline _timeline;
        readonly VulkanRetireList _retired = new();
        readonly VulkanMemoryAllocator _memory;
        readonly IVulkanCommandApi _commands;
        readonly VulkanSubmitQueue _submits;
        readonly VulkanBackpressure _backpressure = new();
        readonly VulkanRingAllocator _rings;
        readonly VulkanResourceOwner _resources;
        readonly VulkanStagingSource _staging;
        readonly VulkanSetupCommands _setup;
        readonly VulkanDescriptors _descriptors;
        readonly VulkanShaderModuleCache _modules;
        readonly VulkanResourceFactory _factory;
        readonly VulkanStagingMaps _maps = new();
        readonly VulkanSampler _pointSampler;
        readonly VulkanSampler _linearSampler;
        readonly VulkanAcquireWaits _acquireWaits = new();
        readonly VulkanPresentBoundary? _present;
        readonly int _framesInFlight;
        readonly Device _device;
        readonly bool _softwareAdapter;
        readonly object _lifecycle = new();

        // THE ONE SUBMIT LOCK (V-W8), owned here rather than by the submit queue because TWO subsystems need the
        // same one: vkQueueSubmit's ordering and the uniform ring's off-timeline write (9.2). Two locks over one
        // queue is not a serialisation at all, and a ring on its own lock could not make its segment-owner read
        // exact, since the window it depends on is inside this lock.
        readonly object _submitLock = new();

        bool _disposed;
        bool _syncToVerticalBlank;

        VulkanGpuDevice(VulkanInstanceLease<VulkanInstance> instance, Device device, Queue graphicsQueue,
            uint graphicsQueueFamily, GpuCapabilities capabilities, bool softwareAdapter,
            VulkanDeviceLiveness liveness, VulkanDeviceLossLatch loss, VulkanTimeline timeline,
            IVulkanDeviceMemoryApi memoryApi, VulkanMemoryFacts memoryFacts, IVulkanCommandApi commands,
            IVulkanResourceApi resourceApi, IVulkanSetupSink setupSink, IVulkanDescriptorApi descriptorApi,
            IVulkanShaderApi shaderApi, uint maxDynamicUniformBuffers, int framesInFlight,
            VulkanWindowedParts? windowed)
        {
            _instance = instance;
            _device = device;
            GraphicsQueue = graphicsQueue;
            GraphicsQueueFamily = graphicsQueueFamily;
            _softwareAdapter = softwareAdapter;
            _liveness = liveness;
            _loss = loss;
            _timeline = timeline;
            _commands = commands;
            _framesInFlight = framesInFlight;
            Capabilities = capabilities;

            // BUILT HERE for the same reason the allocator below is: the lock that orders vkQueueSubmit and the
            // timeline it allocates values from are both this device's, and a submit queue handed in would either
            // need the timeline before the device exists or bring a second lock, and two locks over one queue is
            // not a serialisation at all.
            _submits = new VulkanSubmitQueue(commands, timeline, _submitLock);

            // BUILT HERE, on the SAME lock and the SAME backpressure accumulator the submit path and the command
            // lists use. The ring's segment gate reads this device's one timeline, its off-timeline write takes
            // this device's one submit lock, and its stalls land in the one accumulator MV3 reads, so all three
            // have to be the device's rather than the ring's own.
            _rings = new VulkanRingAllocator(framesInFlight, timeline, _backpressure, _submitLock);

            // BUILT HERE rather than handed in, because the retire list its chunk destroys go through is this
            // object's own field. Passing the allocator in would mean either handing that list out before the
            // device exists or building a second one, and a second retire list splits the deferred destroys in
            // two so that neither drain sees all of them.
            _memory = new VulkanMemoryAllocator(memoryApi, memoryFacts, new VulkanTimelineRetirement(
                timeline, _retired));

            // THE RESOURCE SUBSYSTEM (row 9), built here for the reason everything else on this line is: it is
            // assembled out of four things only the device has (the native resource seam, the one allocator, the
            // one timeline and the one retire list), and a resource created against a second allocator or a second
            // retire list would free memory into a pool nobody drains.
            _resources = new VulkanResourceOwner(resourceApi, _memory, timeline, _retired);
            _staging = new VulkanStagingSource(_resources, liveness);

            // THE SETUP COMMAND BUFFER (V-M10) AND ITS OWN LOCK (V-W8), on the device's frame depth, its own
            // command pools and its own staging arena. Its flush takes the submit lock UNDER its own lock, which
            // is why it is handed the submit queue rather than the raw lock.
            _setup = new VulkanSetupCommands(
                new VulkanCommandPoolRing(commands, framesInFlight, timeline, _backpressure),
                setupSink,
                new VulkanStagingArena(_staging, framesInFlight),
                _submits,
                liveness);

            // THE DESCRIPTOR SUBSYSTEM (row 10), on its OWN owner rather than on _resources. That separation is
            // decision V-D2's enforcement: the recording type's field graph legitimately reaches
            // VulkanResourceOwner through the staging block lifetime edge, so a descriptor pool hung off that
            // record would sit on the far side of the one allowance the unreachability walk makes.
            _descriptors = new VulkanDescriptors(
                new VulkanDescriptorOwner(descriptorApi, timeline, _retired), maxDynamicUniformBuffers);

            // THE SHADER MODULE CACHE (row 16), on the device rather than on the factory, so the dedup is per
            // VkDevice: a module handle is a child of one device and sharing one across two would be a use of a
            // handle the other device never made. It takes no timeline and no retire list, because nothing here
            // is ever destroyed mid-life. Every handle is shared and they all go at teardown.
            _modules = new VulkanShaderModuleCache(shaderApi);

            _factory = new VulkanResourceFactory(_resources, _rings, _setup, _descriptors, _modules,
                () => CreateCommandList(), () => _timeline.CreateFence(), capabilities,
                memoryFacts.MinUniformBufferOffsetAlignment);

            // THE SHARED PAIR WRAPS ON ALL THREE AXES (section 14), built from VulkanSharedSamplers and NOT from
            // the identically named GpuSamplerDescription statics, which default every axis to CLAMP. Neither
            // wrapper owns its VkSampler, so a consumer that disposes one destroys nothing.
            _pointSampler = new VulkanSampler(
                _resources, VulkanSharedSamplers.Point, capabilities.SamplerAnisotropy, ownsSampler: false);
            _linearSampler = new VulkanSampler(
                _resources, VulkanSharedSamplers.Linear, capabilities.SamplerAnisotropy, ownsSampler: false);

            // THE PRESENT BOUNDARY IS BUILT LAST, and it has to be: its orphan target is created through THIS
            // object's resource factory, and its drain is this object's timeline. It creates the first swapchain
            // and takes the first acquire inside its own constructor, which is what makes SwapchainFramebuffer
            // valid from the moment the device exists rather than from the first present.
            if (windowed is not null)
            {
                _syncToVerticalBlank = windowed.SyncToVerticalBlank;
                _present = new VulkanPresentBoundary(
                    windowed.Surfaces, windowed.Swapchains, windowed.Surface, windowed.Requested,
                    windowed.SyncToVerticalBlank, windowed.Acquire, framesInFlight, _submitLock,
                    // THE TIMELINE DRAIN AND NOTHING ELSE. Not this device's WaitForIdle, which also flushes the
                    // setup command buffer: that flush takes the SETUP lock, the boundary calls this holding the
                    // SUBMIT lock, and setup-under-submit is the one lock order V-W8 forbids.
                    () => _timeline.WaitForIdle(),
                    new VulkanOrphanTarget(CreateOrphanTarget),
                    _acquireWaits);
            }
        }

        // WHAT AN IMAGELESS FRAME RENDERS INTO (V-W4): an ordinary render-target texture at the swapchain's own
        // format, so a pipeline built against the swapchain framebuffer is validated against the same output
        // description whether a real image or this is bound. One mip, one layer, one sample, no depth, matching
        // the swapchain framebuffer's shape exactly.
        IGpuTexture CreateOrphanTarget(VulkanExtent extent, GpuPixelFormat format)
            => _factory.CreateTexture(new GpuTextureDescription(
                extent.Width, extent.Height, format, GpuTextureUsage.RenderTarget));

        /// <inheritdoc/>
        public GpuBackendKind Backend => GpuBackendKind.VulkanNative;

        /// <inheritdoc/>
        public GpuCapabilities Capabilities { get; }

        /// <summary>The ONE queue this device has, graphics and (on a windowed device) presentation both (V-N5).
        /// Held here because it is the device's, and read by the command-list row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517) and the swapchain row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527), which are the two that submit and present
        /// through it. The timeline row deliberately does NOT: a semaphore wait needs no queue, which is half the
        /// argument for it being the drain (V-F4).</summary>
        internal Queue GraphicsQueue { get; }

        /// <summary>The family that queue came from. Command pools are created against it, which is why it
        /// travels with the device rather than being re-derived per pool.</summary>
        internal uint GraphicsQueueFamily { get; }

        /// <summary>
        /// The device's ONE completion timeline (V-F1). Every submission takes its next value, every fence holds
        /// one, and the drain waits on the last one handed out. Exposed because it is the primitive rows 7, 8 and
        /// 9 are all built on: the submit path allocates and signals, the uniform ring gates a segment on it, and
        /// the resource factory hands out fences from it.
        /// </summary>
        internal VulkanTimeline Timeline => _timeline;

        /// <summary>
        /// The deferred-disposal retire list (V-F9). A resource's <c>Dispose</c> records
        /// <see cref="VulkanTimeline.LastAllocated"/> here with its own native destroy, and the destroy runs once
        /// the counter passes that value. Its depositors are row 7's command lists, whose per-slot
        /// <c>VkCommandPool</c>s go here when a list is disposed with submissions outstanding, row 9's buffers,
        /// textures and samplers, row 9's staging blocks, and the allocator's own emptied chunks. Every entry is
        /// TERMINAL: it ends its own children inline rather than retiring them, which is what bounds the
        /// retirement depth at the two generations the teardown drains twice for. See
        /// <see cref="VulkanResourceOwner.RetireTerminal"/>.
        /// </summary>
        internal VulkanRetireList Retired => _retired;

        /// <summary>
        /// The device's ONE block suballocator (V-M1, section 9.1). Every <c>vkAllocateMemory</c> this backend
        /// ever makes goes through it, and its chunk destroys are already routed through <see cref="Retired"/>, so
        /// memory returns to the driver only once the timeline has passed the value recorded at free time.
        /// <para>
        /// Every buffer, every image and every staging block comes out of it. It is created with the device rather
        /// than with the first resource for the reason the timeline was: a device-owned primitive that every later
        /// row can assume exists needs no lazy-creation rule, and its teardown slot in <see cref="Dispose"/> is
        /// decided once here instead of by whichever row first allocated something.
        /// </para>
        /// </summary>
        internal VulkanMemoryAllocator Memory => _memory;

        /// <summary>
        /// The device's ONE uniform ring allocator (V-M5, section 9.2): the frame segment every ring-backed uniform
        /// buffer writes into, the completion gate that recycles it, and the off-timeline write's pending-patch
        /// queue.
        /// <para>
        /// Every uniform buffer cuts a ring out of it, at the point <see cref="VulkanBufferRingPolicy.ForBuffer"/>
        /// answers yes inside <c>CreateBuffer</c>. Everything a ring needs from the device is this object's:
        /// the segment index, the timeline its gate reads, the submit lock its off-timeline write takes, and the
        /// backpressure accumulator its stalls land in.
        /// </para>
        /// <para>
        /// AND ITS FRAME BOUNDARY HAS NO CALLER YET, for the same reason <see cref="DrainRetiredResources"/> has
        /// none: the boundary is <see cref="Present"/> and that is row 17's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527). That row calls
        /// <see cref="VulkanRingAllocator.BeginFrame"/> AFTER the present has released the submit lock, which the
        /// allocator refuses a caller by name for.
        /// </para>
        /// </summary>
        internal VulkanRingAllocator Rings => _rings;

        /// <summary>
        /// THE FRAME-BOUNDARY DRAIN of the retire list: run every held destroy the timeline has passed, and leave
        /// the rest. Returns how many ran.
        /// <para>
        /// Nothing calls it yet, because the frame boundary is <see cref="Present"/> and that is row 17's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527). It exists at this row rather than at that one so
        /// that the retire list has exactly ONE release path from the start: this method and the teardown drain
        /// inside <see cref="Dispose"/> both go through <see cref="VulkanRetireList"/>, and no later row has to
        /// invent a second rule about when a deferred destroy is safe.
        /// </para>
        /// </summary>
        internal int DrainRetiredResources()
        {
            if (_liveness.IsDead) return 0;

            return _retired.Drain(_timeline.CompletedValue);
        }

        /// <inheritdoc/>
        /// <remarks>Decision V-G2's <c>softwareAdapter</c> and V-G4's <c>deviceLossReason</c>, both filled from
        /// this row: the first from the chosen device's type and driver id, the second from the latch, which is
        /// null until something is latched and is the header field #427 asks for.</remarks>
        public GpuDeviceDiagnostics Diagnostics => new(_softwareAdapter, _loss.HeaderValue);

        /// <inheritdoc/>
        /// <remarks>
        /// TWO PAIRS ARE MEASUREMENTS AND THE REST IS ARITHMETIC ABOUT SUBSYSTEMS THAT DO NOT EXIST YET, and the
        /// difference matters enough to say here. <c>DrainCount</c> and <c>DrainMs</c> come off
        /// <see cref="VulkanTimeline.TotalDrain"/> and are the M2 numbers (V-F4). <c>BackpressureStallCount</c>
        /// and <c>BackpressureStallMs</c> come off <see cref="VulkanBackpressure"/> and are MV3's, counting BOTH
        /// of that accumulator's meanings on one number: a command list's <c>Begin</c> blocking on its own oldest
        /// pool slot, and a uniform ring's frame boundary finding its segment still in flight (row 8). The two are
        /// folded deliberately, because both say the pipeline is deeper than
        /// <c>KE_VULKAN_FRAMES_IN_FLIGHT</c> allows and both are fixed by the same lever.
        /// <c>OffTimelineDeferred</c> and <c>OffTimelineOutstanding</c> come off
        /// <see cref="VulkanRingAllocator.OffTimelinePatches"/> and are deliberately NOT folded into that number:
        /// a deferred patch is not a stall at all (see <see cref="VulkanRingPatchStats"/>).
        /// <c>FramesBegun</c> is the one field still 0 because the thing that could move it is not built: no frame
        /// has been OPENED, since <see cref="Present"/> is row 17's. That zero is literally true about this device
        /// rather than a placeholder, which is the bar the struct's own "absent is not zero" rule sets for
        /// reporting <c>HasValue</c> at all.
        /// <para>
        /// WHAT A READER STILL MUST NOT DO is divide by <c>FramesBegun</c> while it is 0, and row 18
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/528) is where every field becomes a reading taken from
        /// the subsystem that owns it.
        /// </para>
        /// </remarks>
        public GpuDeviceCounters Counters
        {
            get
            {
                VulkanWaitTotals drain = _timeline.TotalDrain;
                VulkanWaitTotals stalls = _backpressure.Totals;
                VulkanRingPatchStats patches = _rings.OffTimelinePatches;

                // Named, because the two longs and the two doubles sit next to each other: a transposed pair here
                // compiles, passes every test, and reports a stall count as a drain count in the field.
                VulkanWaitTotals acquires = _acquireWaits.Totals;

                return new GpuDeviceCounters(
                    framesBegun: _present?.FramesBegun ?? 0,
                    drainCount: drain.Count,
                    drainMs: drain.TotalMs,
                    backpressureStallCount: stalls.Count,
                    backpressureStallMs: stalls.TotalMs,
                    offTimelineDeferred: patches.Deferred,
                    offTimelineOutstanding: patches.Outstanding,
                    acquireWaitCount: acquires.Count,
                    acquireWaitMs: acquires.TotalMs);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE SAME OBJECT FOR THE WHOLE LIFE OF A WINDOWED DEVICE (V-W5), so it may be cached by anything: a
        /// recreate and an acquire both change what it points at and never its identity. NULL on a headless
        /// device, which is correct rather than unbuilt: the headless path enables no surface extension at all
        /// (V-N6), which is what lets the golden suite run on a machine with no display server.
        /// </remarks>
        public IGpuFramebuffer? SwapchainFramebuffer => _present?.Framebuffer;

        /// <inheritdoc/>
        /// <remarks>
        /// UNLIKE DIRECT3D 11, THIS RECONFIGURES SOMETHING. There, vsync is an argument of the present call, so a
        /// setter only changes an interval. Vulkan cannot change a swapchain's present mode in place, so a change
        /// here QUEUES A RECREATE that the next present boundary applies, exactly as a resize does (V-W6). The
        /// value is settable from any thread and takes no lock, and the recreate lands on the submit thread where
        /// it provably owns the queue.
        /// <para>
        /// On a HEADLESS device it is a plain backing value, which is what the seam asks for: there is no
        /// swapchain to reconfigure.
        /// </para>
        /// </remarks>
        public bool SyncToVerticalBlank
        {
            get => _present?.SyncToVerticalBlank ?? _syncToVerticalBlank;
            set
            {
                _syncToVerticalBlank = value;
                if (_present is not null) _present.SyncToVerticalBlank = value;
            }
        }

        /// <summary>
        /// Block until the GPU is idle: <c>vkWaitSemaphores</c> on the last value the timeline handed to a
        /// submission, with an infinite timeout, counted into <c>DrainCount</c> and <c>DrainMs</c> (V-F4). A SAFE
        /// NO-OP after the device is dead (V-F10), because a destroyed device has no outstanding work left to
        /// finish, so returning is the honest answer and waiting would wait on a counter nothing can advance.
        /// <para>
        /// NOT <c>vkDeviceWaitIdle</c> AND NOT <c>vkQueueWaitIdle</c>, which is the change this row made to a
        /// method that already worked. A semaphore wait does not need the queue lock, so a drain from one thread
        /// does not block a submit from another until it finishes, and it names a VALUE, which is what turns a
        /// drain into something with a number attached. Teardown still uses <c>vkDeviceWaitIdle</c>, where there
        /// is no submission left to protect and the question being asked is about the whole device.
        /// </para>
        /// <para>
        /// The result of every native call on that path is checked in every configuration and a
        /// <c>VK_ERROR_DEVICE_LOST</c> is latched at the call's own site (V-G4). See
        /// <see cref="VulkanTimeline.WaitForIdle"/> for which cases return without counting and why.
        /// </para>
        /// </summary>
        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;

            // THE EXPLICIT DRAIN IS A DEVICE-LEVEL READ, so it flushes the setup buffer first (V-M10). A caller
            // that created a render target and then drained is entitled to find its creation-time clear executed,
            // and the wait below is what makes that true rather than merely queued.
            FlushSetup();

            _timeline.WaitForIdle();

            // The strict rung's controlled throw. Placed after the wait rather than before it, so a validation
            // error raised by work this wait was flushing is caught by the same call that flushed it.
            _instance.Value.Messenger?.Pump.ThrowIfLatched("WaitForIdle");
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE OFF-TIMELINE WRITE (V-M8, section 9.2), which is the device-level half of the split. On a
        /// RING-BACKED uniform buffer it reaches EVERY segment, so a value written once at load time or when a
        /// setting changes persists for the buffer's life exactly as it does on the Veldrid leg, where the buffer
        /// has one copy. It NEVER BLOCKS: a segment an earlier frame is still reading takes the bytes as a pending
        /// patch that the frame boundary opening that segment replays, so a caller already holding the submit lock
        /// is legal.
        /// <para>
        /// A NON-UNIFORM buffer takes the other path: its bytes go into the DEVICE-OWNED staging arena and a
        /// <c>vkCmdCopyBuffer</c> plus a narrowed barrier are appended to the setup command buffer of V-M10, which
        /// is what off-timeline means for anything that is not persistently mapped. Nothing is submitted here
        /// either: that batch flushes at the next submit or at the next device-level read.
        /// </para>
        /// </remarks>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => UpdateOffTimeline(b, offsetBytes, MemoryMarshal.AsBytes(data));

        /// <inheritdoc/>
        /// <remarks>Same routing as the span overload. A null array is refused rather than treated as empty,
        /// because a caller that meant "write nothing" passes an empty span.</remarks>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(data);
            UpdateOffTimeline(b, offsetBytes, MemoryMarshal.AsBytes<T>(data));
        }

        /// <inheritdoc/>
        /// <remarks>Same routing as the span overload.</remarks>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => UpdateOffTimeline(b, offsetBytes,
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1)));

        /// <inheritdoc/>
        /// <remarks>
        /// QUEUED AND APPLIED AT THE NEXT PRESENT BOUNDARY (V-W6), never here. This stores a number and returns:
        /// no lock, no native call, nothing that can block, so a window callback arriving on any thread while the
        /// submit thread is inside <c>vkQueueSubmit</c> is safe. That makes a foreign-thread resize during
        /// recording STRUCTURALLY impossible rather than contractually forbidden, and it matters more on this
        /// backend than on the other one because recreating a swapchain invalidates every attachment a recording
        /// may already have bound.
        /// <para>
        /// A ZERO IN EITHER DIMENSION IS SAFE HERE AND SAFE AT THE APPLY. A minimised window reports (0, 0)
        /// through its framebuffer-resize event on Windows, and the clamp against the surface's own reported
        /// bounds produces an extent the boundary then reads as not creatable, so no <c>vkCreateSwapchainKHR</c>
        /// is ever made at a size the specification forbids
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/81). The frame goes imageless, binds the orphan
        /// target and skips its present until the window comes back.
        /// </para>
        /// <para>
        /// A no-op on a headless device, which has no swapchain to resize. Silent rather than a throw, matching
        /// the incumbent, because teardown order is a consumer's business and a window can report a size change
        /// while it is being destroyed.
        /// </para>
        /// </remarks>
        public void ResizeSwapchain(uint w, uint h) => _present?.QueueResize(w, h);

        /// <inheritdoc/>
        /// <remarks>
        /// THE FRAME BOUNDARY, IN ONE PLACE (V-W4). It presents the frame just submitted if an image is held,
        /// applies any pending recreate at that same boundary, and acquires for the NEXT frame, then rotates the
        /// uniform ring's segment and drains the deferred-disposal list. It never throws and never reports
        /// failure upward: an <c>OUT_OF_DATE</c> surface, a minimised window and a driver that lost the surface
        /// are all handled inside <see cref="VulkanPresentBoundary"/> and the frame loop above is unchanged.
        /// <para>
        /// THE RING AND THE RETIRE LIST ROTATE AFTER THE BOUNDARY HAS RELEASED THE SUBMIT LOCK, which
        /// <see cref="VulkanRingAllocator.BeginFrame"/> refuses a caller by name for: its segment gate can block,
        /// and blocking inside the submit lock would stop every other thread's submit for the length of a GPU
        /// wait.
        /// </para>
        /// <para>
        /// A HEADLESS DEVICE STILL ROTATES BOTH. It has no swapchain and nothing to present, and it does have a
        /// uniform ring and a retire list, so a headless consumer that runs frames gets the same recycling a
        /// windowed one does rather than an ever-growing retire list.
        /// </para>
        /// </remarks>
        public void Present()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _present?.Present();

            // OUTSIDE EVERY LOCK, and in this order. The ring's frame boundary is what recycles a segment the GPU
            // has finished with, and the retire drain is what actually runs the deferred destroys the frame's
            // disposals recorded.
            _rings.BeginFrame();
            DrainRetiredResources();
        }

        /// <summary>
        /// Destroy the device, in the ONE order V-F10 permits: <c>vkDeviceWaitIdle</c> FIRST, then the retire
        /// list's teardown drain, then the block suballocator's chunks, then a second drain, then the timeline
        /// semaphore, then the liveness flip, then <c>vkDestroyDevice</c>, then the instance lease.
        /// <para>
        /// The incumbent destroys its memory manager and its pools and only THEN waits, which destroys objects the
        /// GPU may still be reading. Waiting first is the whole fix, and it is why the wait was written here at
        /// row 4 rather than left to whichever later row added the first destroyable resource: a teardown order
        /// established once is an order every later row inherits. This row is the first to add to it, and it slots
        /// its two entries into the WINDOW BETWEEN THE WAIT AND THE FLIP, which is the only window in which
        /// destroying a child object of the device is both safe and legal. Before the wait it would be a destroy
        /// of something the GPU may still be reading, and after the flip every native destroy is skipped by
        /// contract, so anything the device itself owns would leak until <c>vkDestroyDevice</c> collected it.
        /// </para>
        /// <para>
        /// THE RETIRE LIST GOES BEFORE THE SEMAPHORE, and the order of those two is the one edit here that could
        /// look arbitrary. The held destroys are gated on timeline VALUES, and the teardown drain runs them all
        /// regardless because the wait above already made every value passed. Draining them first keeps the rule
        /// intact anyway: nothing that reads the timeline runs after the timeline has gone.
        /// </para>
        /// <para>
        /// THE ALLOCATOR GOES AFTER THE RETIRE LIST'S DRAIN, because objects are destroyed before the memory they
        /// are bound into. Freeing a chunk while a <c>VkBuffer</c> or <c>VkImage</c> is still bound into it is the
        /// hazard, and row 9 (https://github.com/APKiwiOrg/KhaozEngine/issues/519) is where those objects started
        /// existing, so the order was settled here rather than by the row that first trips it. The chunks are then
        /// freed IMMEDIATELY rather than retired: the wait above already made the GPU idle, so retiring them would
        /// only mean the same calls one line later.
        /// </para>
        /// <para>
        /// THE SECOND DRAIN IS NOT BELT AND BRACES. A resource destroy running in the first drain may free its own
        /// allocation, and freeing the last allocation in a chunk RETIRES that chunk, appending to a list the
        /// drain has already taken its entries from. Without the second drain that chunk is never freed, and the
        /// leak is one that only appears once resources exist, which is to say in somebody else's row. A drain on
        /// an empty list is one length read.
        /// </para>
        /// <para>
        /// A DEAD DEVICE ABANDONS THE RETIRE LIST INSTEAD OF DRAINING IT, on BOTH of the paths that reach one. The
        /// device can be dead when this method is entered, and it can be lost BY the wait above, which is the
        /// easier one to miss: the loss flips liveness mid-teardown, and from that moment every wrapper-level
        /// destroy is skipped by contract, so a drain that ran anyway would make exactly the calls that contract
        /// exists to stop. Running a destroy against a lost device is a call against freed memory, which aborts
        /// the process through the Vulkan loader rather than failing quietly.
        /// </para>
        /// <para>
        /// The lease goes LAST and always, including when the wait or the destroy failed. A device that leaked its
        /// lease would hold the process instance alive for the rest of the run, and every later device would
        /// share an instance whose first holder is gone.
        /// </para>
        /// <para>
        /// The order below is held by nothing more than the sequence of statements: no test asserts it, because
        /// there is no seam that can observe teardown order device-free. An edit that reorders these lines must
        /// re-read this block rather than trust a green suite to catch the regression.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    if (!_liveness.IsDead)
                    {
                        Vk vk = _instance.Value.Api;

                        // FIRST. A device destroyed with work in flight takes the driver down with it on some
                        // implementations and corrupts silently on others.
                        Result waited = vk.DeviceWaitIdle(_device);
                        bool lost = _loss.Check(waited, "vkDeviceWaitIdle (teardown)");
                        if (!lost && waited != Result.Success)
                        {
                            log.Warn("vkDeviceWaitIdle returned "
                                + $"{VulkanResultCodes.Token(waited)} during native Vulkan device teardown. The "
                                + "device is destroyed anyway, because there is no recovery available at "
                                + "teardown and leaving it alive would leak everything behind it.");
                        }

                        // THE DRAIN IS GATED ON THE WAIT HAVING ACTUALLY HAPPENED. A loss latched by the wait
                        // above flipped liveness, and from that moment every wrapper-level destroy is skipped by
                        // contract, because the objects went with the device. Draining anyway would run exactly
                        // the calls that contract exists to stop. Otherwise the wait is what makes an
                        // unconditional drain correct: the GPU is idle, so every recorded timeline value has been
                        // passed and the values have nothing left to say.
                        // EVERY OPEN MAPPING IS FORGOTTEN rather than closed. After the flip the memory behind one
                        // does not exist, so a later Unmap is a caller error about a resource with nothing under
                        // it, and there is no vkUnmapMemory to make anyway (V-M3).
                        ReportForgottenMaps(_maps.Forget());

                        // THE PRESENT BOUNDARY GOES FIRST AMONG THE DEVICE'S OWN CHILDREN, and its position is
                        // the one edit here that could look arbitrary. It destroys the swapchain, its image
                        // views and its binary semaphores, all of which are children of the device and none of
                        // which the retire list holds, and it must happen while the device is still alive and
                        // before vkDestroyDevice collects them. It runs its own drain first, which the wait
                        // above has already made a formality. On the LOST path it is skipped entirely, for the
                        // same reason every other destroy is: the objects went with the device.
                        if (!lost) _present?.Dispose();

                        if (lost)
                        {
                            // The setup buffer still hands over on this path: its arena's blocks go through the
                            // staging source, which ABANDONS on a dead device rather than freeing, and its pools
                            // land in a list the abandon below empties.
                            _setup.Retire(_retired);
                            ReportAbandoned(_retired.Abandon(), _memory.Abandon());
                        }
                        else
                        {
                            // THE SHARED SAMPLER PAIR IS DESTROYED DIRECTLY rather than retired. The wait above
                            // already made the GPU idle, so retiring them would mean the same two calls one line
                            // later, and their wrappers deliberately refuse to destroy themselves so a consumer
                            // cannot.
                            _pointSampler.DestroyShared();
                            _linearSampler.DestroyShared();

                            // THE SETUP BUFFER'S POOLS AND ITS ARENA, before the first drain, so both generations
                            // of what they retire are covered by the two drains below.
                            _setup.Retire(_retired);

                            // Objects first, then the memory they are bound into, then anything the object
                            // destroys retired on their way out. See the doc block above for both orderings.
                            _retired.DrainAll();
                            _memory.Dispose();
                            _retired.DrainAll();
                        }

                        // THE DESCRIPTOR SUBSYSTEM GOES AFTER BOTH DRAINS, and the order is the one thing here
                        // that could look arbitrary. A resource set's disposal is a DEFERRED free, so the
                        // vkFreeDescriptorSets calls are entries in the retire list: destroying the pools before
                        // the drains would leave those calls naming a pool that no longer exists. After them,
                        // every free has run against a live pool and destroying a pool only collects whatever a
                        // consumer never disposed. Skipped natively on a dead device by the same liveness token
                        // as every other destroy.
                        ReportDescriptorTeardown(_descriptors.DestroyAll());

                        // THE SHADER MODULES GO HERE TOO, and their position carries no constraint of its own: a
                        // VkShaderModule may legally be destroyed while pipelines built from it are still alive,
                        // because the pipeline consumed the code at creation. They are grouped with the descriptor
                        // teardown because both are caches of SHARED handles that no wrapper's Dispose can end,
                        // and both are skipped natively on a dead device by the same liveness token.
                        ReportShaderTeardown(_modules.DestroyAll());

                        // Skips its own native destroy on the lost path, for the same reason, through the same
                        // liveness token.
                        _timeline.Dispose();

                        // Flipped BEFORE the destroy and after the wait, so no wrapper can observe "alive" after
                        // the object it would destroy has gone, and so a wrapper disposed on another thread mid
                        // teardown becomes a no-op rather than a call against freed memory.
                        _liveness.MarkDead();
                        vk.DestroyDevice(_device, null);
                    }
                    else
                    {
                        // A device that was ALREADY dead when Dispose arrived. Its children went with it, so the
                        // held destroys are dropped rather than run, every memory chunk is forgotten rather than
                        // freed, and the timeline's own Dispose skips the native destroy for the same reason.
                        ReportForgottenMaps(_maps.Forget());
                        _setup.Retire(_retired);
                        ReportAbandoned(_retired.Abandon(), _memory.Abandon());
                        ReportDescriptorTeardown(_descriptors.DestroyAll());
                        ReportShaderTeardown(_modules.DestroyAll());
                        _timeline.Dispose();
                    }
                }
                finally
                {
                    // ALWAYS, including the device-lost path where the destroy above was skipped. The instance
                    // survives a lost device, and the next device created on it is the recovery such as it is.
                    _instance.Dispose();
                }
            }
        }

        // Says how many mappings were still open when the device was torn down. A correct consumer closes every
        // one, and a non-zero count is a readback that held a pointer across teardown.
        static void ReportForgottenMaps(int open)
        {
            if (open == 0) return;

            log.Warn($"{open} native Vulkan staging mappings were still open when the device was disposed. The "
                + "memory behind them went with the device, so they are forgotten rather than closed, and an "
                + "Unmap after this point is a call about a resource with nothing under it.");
        }

        // Says what the descriptor subsystem released. Logged at debug rather than warn because none of these
        // numbers is a defect: a non-zero pool count at teardown is a program that built resource sets, and the
        // gap between the layouts asked for and the set layouts created is decision V-D5's dedup working.
        static void ReportDescriptorTeardown((int PipelineLayouts, int SetLayouts, int Pools) destroyed)
        {
            if (destroyed.PipelineLayouts == 0 && destroyed.SetLayouts == 0 && destroyed.Pools == 0) return;

            log.Debug($"The native Vulkan device destroyed {destroyed.PipelineLayouts} shared pipeline layouts, "
                + $"{destroyed.SetLayouts} shared descriptor set layouts and {destroyed.Pools} descriptor pools "
                + "at teardown. Both layout kinds are CONTENT-SHARED, so these counts are distinct shapes rather "
                + "than distinct IGpuResourceLayout objects.");
        }

        // Says how many VkShaderModules the device destroyed, which is a count of DISTINCT SPIR-V rather than of
        // IGpuShaderSet objects: the shipped programs share stages heavily, so the two numbers differ on purpose
        // and the gap is decision V-S7's dedup working. Debug rather than warn, since none of it is a defect.
        void ReportShaderTeardown(int destroyed)
        {
            if (destroyed == 0) return;

            log.Debug($"The native Vulkan device destroyed {destroyed} shared VkShaderModules at teardown: "
                + _modules.Describe() + ". A module is shared by every program compiled from the same SPIR-V, so "
                + "this is a count of distinct modules rather than of shader sets.");
        }

        // Says how many deferred destroys and how many memory chunks went unfreed on a dead device. A report
        // rather than a leak, since both went with the device, and worth a line because a large number of either
        // says the consumer was still creating and disposing resources after the device had gone.
        static void ReportAbandoned(int dropped, int chunks)
        {
            if (dropped == 0 && chunks == 0) return;

            log.Warn($"{dropped} deferred native Vulkan destroys and {chunks} device memory chunks were dropped "
                + "without running, because the device they belonged to was dead by the time it was disposed. "
                + "Both went with the device, so this is a report rather than a leak.");
        }

        // NOTHING ON THIS TYPE REFUSES BY NAMING A ROW ANY MORE. The swapchain pair was the last pair that did,
        // and the helper that built those messages went with it rather than being left standing for a future
        // caller: an unbuilt-member helper on a type with no unbuilt members is an invitation to add one.
        // VulkanCommandList and VulkanResourceFactory still carry their own, for the rows that really are open.
    }
}
