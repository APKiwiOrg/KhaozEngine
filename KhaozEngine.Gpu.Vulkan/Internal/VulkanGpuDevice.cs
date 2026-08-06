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
    /// validation pump. Resources and samplers are row 9 and the swapchain is row 17, so the members those rows
    /// own throw a message saying so rather than returning something that fails later somewhere less informative.
    /// The Direct3D 11 package's <c>D3D11ResourceFactory</c> landed the same way and its doc paragraph was
    /// rewritten at every fill-in, which is the discipline this paragraph is under too: it is a ledger, and a
    /// stale one is worse than none.
    /// </para>
    /// <para>
    /// <b>THE MEMBERS THAT ARE LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> (in the part that can
    /// be read honestly, see below), <see cref="Diagnostics"/> with both of its fields, <see cref="Counters"/> (in
    /// the drain and backpressure halves, see below), both <c>Submit</c> overloads, <see cref="WaitForIdle"/>,
    /// and <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// <b>THE COMMAND PATH IS LIVE IN ITS LIFECYCLE AND NOT IN ITS CONTENT</b> (row 7,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/517). <c>CreateCommandList</c> hands out a real
    /// <see cref="VulkanCommandList"/> with its own per-slot <c>VkCommandPool</c>s, <c>Begin</c> and <c>End</c>
    /// work, and <c>Submit</c> is ONE <c>vkQueueSubmit</c> under one short lock that allocates and signals the
    /// timeline value inside it. What no list can do yet is RECORD anything: every drawing, binding, clearing and
    /// copying member names the row that builds it. The list is not reachable through the SEAM either, because
    /// that is <c>IGpuResourceFactory.CreateCommandList</c> and <see cref="Factory"/> is row 9's, so this device's
    /// own internal member is how the rows built on recording get one meanwhile. See
    /// <c>VulkanGpuDevice.Submit.cs</c>.
    /// </para>
    /// <para>
    /// <b>THE COMPLETION TIMELINE IS LIVE AND IS NOT REACHABLE THROUGH THE SEAM YET</b> (row 5,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/515). The device owns one timeline <c>VkSemaphore</c>, a
    /// real <c>IGpuFence</c> over it and the deferred-disposal retire list, and <see cref="WaitForIdle"/> is the
    /// counted <c>vkWaitSemaphores</c> drain on it. What the seam cannot reach is the FENCE FACTORY: the seam
    /// creates a fence through <c>IGpuResourceFactory.CreateFence</c>, and <see cref="Factory"/> is row 9's, so
    /// <see cref="VulkanTimeline.CreateFence"/> is reached through <see cref="Timeline"/> by the rows that build
    /// on it and by nothing else meanwhile. <c>SupportsCompletionFences</c> was already true in this device's
    /// partial capability read, and it is now backed by a real primitive rather than by a promise about one.
    /// </para>
    /// <para>
    /// <b>THE BLOCK SUBALLOCATOR IS LIVE AND NOTHING ALLOCATES OUT OF IT YET</b> (row 6,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/516). The device owns one
    /// <see cref="VulkanMemoryAllocator"/>: chunks pooled by <c>(memoryTypeIndex, linear|optimal)</c>, first-fit
    /// with alignment correction and coalescing free, persistent whole-chunk mapping, dedicated allocations on
    /// driver preference or above a size threshold, and flush and invalidate on the non-coherent path. What it has
    /// no callers for is RESOURCES: buffers and images are row 9, so the allocator is complete internal machinery
    /// driven by tests until that row binds the first <c>VkBuffer</c> to one of its offsets. The retire path is
    /// wired from this row, so a chunk that empties is already returned behind the timeline rather than freed
    /// underneath a submission.
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
            int framesInFlight)
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
        }

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
        /// the counter passes that value. Its first real depositor is row 7's command list, whose per-slot
        /// <c>VkCommandPool</c>s go here when a list is disposed with submissions outstanding, and row 9's
        /// resources are the next.
        /// </summary>
        internal VulkanRetireList Retired => _retired;

        /// <summary>
        /// The device's ONE block suballocator (V-M1, section 9.1). Every <c>vkAllocateMemory</c> this backend
        /// ever makes goes through it, and its chunk destroys are already routed through <see cref="Retired"/>, so
        /// memory returns to the driver only once the timeline has passed the value recorded at free time.
        /// <para>
        /// Nothing allocates out of it yet, because no resource type exists: buffers and images are row 9
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519) and the uniform ring is row 8
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/518). It is created with the device rather than with
        /// the first resource for the reason the timeline was: a device-owned primitive that every later row can
        /// assume exists needs no lazy-creation rule, and its teardown slot in <see cref="Dispose"/> is decided
        /// once here instead of by whichever row first allocated something.
        /// </para>
        /// </summary>
        internal VulkanMemoryAllocator Memory => _memory;

        /// <summary>
        /// The device's ONE uniform ring allocator (V-M5, section 9.2): the frame segment every ring-backed uniform
        /// buffer writes into, the completion gate that recycles it, and the off-timeline write's pending-patch
        /// queue.
        /// <para>
        /// Nothing constructs a ring out of it yet, because no BUFFER exists: row 9
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519) is where <c>CreateBuffer</c> asks
        /// <see cref="VulkanBufferRingPolicy.ForBuffer"/> whether to build one. What is already wired is everything
        /// a ring needs from the device: this object's segment index, the timeline its gate reads, the submit lock
        /// its off-timeline write takes, and the backpressure accumulator its stalls land in.
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
                return new GpuDeviceCounters(
                    framesBegun: 0,
                    drainCount: drain.Count,
                    drainMs: drain.TotalMs,
                    backpressureStallCount: stalls.Count,
                    backpressureStallMs: stalls.TotalMs,
                    offTimelineDeferred: patches.Deferred,
                    offTimelineOutstanding: patches.Outstanding);
            }
        }

        /// <inheritdoc/>
        /// <remarks>Null, and correct rather than unbuilt: this row creates HEADLESS devices only, and a headless
        /// device has no swapchain by definition. The windowed path is row 17
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527), and it refuses at creation rather than handing
        /// back a device that cannot present.</remarks>
        public IGpuFramebuffer? SwapchainFramebuffer => null;

        /// <inheritdoc/>
        public IGpuResourceFactory Factory => throw NotBuiltYet("The resource factory", ResourcesRow);

        /// <inheritdoc/>
        public IGpuSampler PointSampler => throw NotBuiltYet("The shared point sampler", ResourcesRow);

        /// <inheritdoc/>
        public IGpuSampler LinearSampler => throw NotBuiltYet("The shared linear sampler", ResourcesRow);

        /// <inheritdoc/>
        /// <remarks>A backing value on a headless device, which is what the seam asks for. It reconfigures
        /// nothing because there is no swapchain to reconfigure, and row 17 is where it starts meaning
        /// something.</remarks>
        public bool SyncToVerticalBlank
        {
            get => _syncToVerticalBlank;
            set => _syncToVerticalBlank = value;
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
        /// A NON-UNIFORM buffer needs a device-owned staging pool and the setup command buffer of V-M10, which is
        /// row 9's (https://github.com/APKiwiOrg/KhaozEngine/issues/519), and no such buffer can exist before that
        /// row anyway.
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
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => throw NotBuiltYet("Uploading to a texture", ResourcesRow);

        /// <inheritdoc/>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
            => throw NotBuiltYet("Uploading to a texture", ResourcesRow);

        /// <inheritdoc/>
        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
            => throw NotBuiltYet("Mapping a staging texture", ResourcesRow);

        /// <inheritdoc/>
        public void Unmap(IGpuTexture staging) => throw NotBuiltYet("Mapping a staging texture", ResourcesRow);

        /// <inheritdoc/>
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
            => throw NotBuiltYet("Mapping a staging buffer", ResourcesRow);

        /// <inheritdoc/>
        public void Unmap(IGpuBuffer staging) => throw NotBuiltYet("Mapping a staging buffer", ResourcesRow);

        /// <inheritdoc/>
        public void ResizeSwapchain(uint w, uint h) => throw NotBuiltYet("Resizing the swapchain", SwapchainRow);

        /// <inheritdoc/>
        public void Present() => throw NotBuiltYet("Presenting a frame", SwapchainRow);

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
        /// hazard, and row 9 (https://github.com/APKiwiOrg/KhaozEngine/issues/519) is where those objects start
        /// existing, so the order is settled here rather than by the row that first trips it. The chunks are then
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
                        if (lost)
                        {
                            ReportAbandoned(_retired.Abandon(), _memory.Abandon());
                        }
                        else
                        {
                            // Objects first, then the memory they are bound into, then anything the object
                            // destroys retired on their way out. See the doc block above for both orderings.
                            _retired.DrainAll();
                            _memory.Dispose();
                            _retired.DrainAll();
                        }

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
                        ReportAbandoned(_retired.Abandon(), _memory.Abandon());
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

        // THE ROUTING, in ONE place rather than at each of the three overloads, and the mirror of the command
        // list's: a ring-backed buffer takes the every-segment off-timeline write and everything else needs the
        // device-owned staging pool row 9 builds.
        void UpdateOffTimeline(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);

            if (buffer is IVulkanRingBacked { Ring: { } ring })
            {
                _rings.UpdateBuffer(ring, offsetBytes, data);
                return;
            }

            throw NotBuiltYet("Uploading to a NON-UNIFORM buffer", ResourcesRow);
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

        // The row that owns each unbuilt member, as a full URL, because these messages are read by somebody who
        // has just hit one and needs to know whether to wait for a row or file a bug.
        const string ResourcesRow = "the resources row (https://github.com/APKiwiOrg/KhaozEngine/issues/519)";
        const string SwapchainRow = "the swapchain row (https://github.com/APKiwiOrg/KhaozEngine/issues/527)";

        // Named rather than a bare NotImplementedException, and it names WHAT IS LIVE as well as what is not,
        // which is the shape D3D11ResourceFactory's equivalent settled on: a reader who hits this needs to know
        // whether the backend is unfinished or their machine is wrong, and those have different answers.
        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Vulkan backend: it lands in {row}. The instance, the "
                + "device, the queue, the device-loss latch and the validation pump ARE live (work-breakdown row "
                + "4, https://github.com/APKiwiOrg/KhaozEngine/issues/514). This is a statement about the "
                + "package and not about this machine. Select GpuBackendKind.Vulkan, which goes through Veldrid, "
                + "for a fully working Vulkan device.");
    }
}
