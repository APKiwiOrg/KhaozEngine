namespace KhaozEngine.Gpu
{
    /// <summary>
    /// THE SOAK COUNTERS A DEVICE KEEPS ABOUT ITSELF, cumulative since it was created and read LIVE, so a
    /// telemetry session can carry the numbers a field soak is judged on. Surfaced on
    /// <see cref="IGpuDevice.Counters"/>, <see cref="GpuDeviceContext.Counters"/> and <c>AppWindow.Counters</c>,
    /// and projected into a session's sample rows by <see cref="GpuTelemetryChannels"/>.
    /// <para>
    /// WHY CUMULATIVE AND NOT PER FRAME, which is the one design question here. A telemetry session has two
    /// channels: a header written once at the start, and a sample row written whenever the consumer decides. A
    /// per-frame value belongs to neither. The header is a snapshot of creation-time facts, and a sample row
    /// carrying "the frame that just ended" reports only the frames the sampler happened to land on, which for a
    /// counter whose pass condition is ZERO ACROSS A WHOLE WINDOW is the wrong question answered precisely. A
    /// cumulative counter sampled at any cadence settles it by subtraction: the window's stalls are the last row
    /// minus the first, exactly, and the per-frame figure is that difference over
    /// <see cref="FramesBegun"/>'s difference. The backend keeps its per-frame rolls for the debug overlay and for
    /// its own tests. What crosses the seam is the shape that survives sampling.
    /// </para>
    /// <para>
    /// ABSENT IS NOT ZERO, and <see cref="HasValue"/> is the whole reason this is not a bag of nullable longs.
    /// Zero stalls IS the passing result, so a backend that keeps no counters must not report the same numbers as
    /// a backend that kept them and never stalled. The default value answers false and is what every incumbent
    /// Veldrid path gives, Veldrid Metal included. The three native backends give a true value instead: D3D11
    /// every field except the acquire pair, which it passes as zero because a Direct3D 11 present has no acquire
    /// to wait on, and Vulkan and Metal every field including that pair, both of which really do wait on an
    /// acquire.
    /// </para>
    /// <para>
    /// THE TWO BACKPRESSURE READINGS ARE SEPARATE MEMBERS AND MUST STAY SEPARATE
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/499). <see cref="BackpressureStallCount"/> is the wait
    /// that BLOCKED: the CPU reached a ring segment the GPU was still reading, or on the native Vulkan backend a
    /// command list's own oldest buffer slot still executing, which is one statement about pipeline depth whose
    /// lever either way is the frames-in-flight count.
    /// <see cref="OffTimelineDeferred"/> is a device-level buffer write that met a segment an earlier frame was
    /// still reading and queued its bytes for that segment's next acquire, which blocks nobody and usually happens
    /// at load time before any frame exists. A non-zero off-timeline count beside a zero stall count is a specific
    /// diagnosis, namely that the segment count is fine and a caller is writing off-timeline against in-flight
    /// work. Folding them would destroy that reading and make the zero-stall criterion unreachable for a reason
    /// that has nothing to do with pipeline depth.
    /// </para>
    /// </summary>
    public readonly struct GpuDeviceCounters
    {
        /// <summary>
        /// Build a populated counter set. Every backend that HAS these numbers calls this, and every backend that
        /// does not leaves the default, so calling it with all zeros is a deliberate statement that the device
        /// counted and found nothing.
        /// </summary>
        /// <param name="framesBegun">Frames the device has opened, the denominator for any per-frame figure.</param>
        /// <param name="drainCount">Waits for the GPU to go idle that actually blocked.</param>
        /// <param name="drainMs">Milliseconds those drains spent blocked, summed.</param>
        /// <param name="backpressureStallCount">Frame boundaries that blocked on a busy ring segment.</param>
        /// <param name="backpressureStallMs">Milliseconds those stalls spent blocked, summed.</param>
        /// <param name="offTimelineDeferred">Off-timeline buffer writes queued against an in-flight segment.</param>
        /// <param name="offTimelineOutstanding">Those queued writes still waiting for a segment to be reopened.</param>
        /// <param name="acquireWaitCount">Present boundaries that blocked waiting for the next swapchain image.
        /// Zero is the honest reading on a backend whose acquire never blocks the CPU, and on a headless device
        /// with no swapchain at all.</param>
        /// <param name="acquireWaitMs">Milliseconds those acquire waits spent blocked, summed.</param>
        public GpuDeviceCounters(
            long framesBegun,
            long drainCount,
            double drainMs,
            long backpressureStallCount,
            double backpressureStallMs,
            long offTimelineDeferred,
            long offTimelineOutstanding,
            long acquireWaitCount,
            double acquireWaitMs)
        {
            HasValue = true;
            FramesBegun = framesBegun;
            DrainCount = drainCount;
            DrainMs = drainMs;
            BackpressureStallCount = backpressureStallCount;
            BackpressureStallMs = backpressureStallMs;
            OffTimelineDeferred = offTimelineDeferred;
            OffTimelineOutstanding = offTimelineOutstanding;
            AcquireWaitCount = acquireWaitCount;
            AcquireWaitMs = acquireWaitMs;
        }

        /// <summary>
        /// True when this device counted, false on the default value every backend without counters answers with.
        /// Test it before reading anything else: a false here and an all-zero counter set are opposite facts, and
        /// the whole gate turns on telling them apart.
        /// </summary>
        public bool HasValue { get; }

        /// <summary>
        /// Frames this device has OPENED since it was created. It is the denominator: a per-frame drain cost is
        /// the difference in <see cref="DrainMs"/> between two sampled rows divided by the difference here, which
        /// is what the M2 criterion (under 0.2 ms of drain per frame) is stated against.
        /// </summary>
        public long FramesBegun { get; }

        /// <summary>
        /// Waits for the GPU to go idle that ACTUALLY BLOCKED, since device creation. A wait that a kill switch
        /// or a dead device turned into an immediate return is never counted, because counting those would report
        /// a run that never drained as having drained constantly for no time at all.
        /// <para>
        /// THE ALREADY-CAUGHT-UP EXEMPTION IS SCOPED TO THE BACKENDS THAT CAN HONOUR IT, and this member is not
        /// comparable across the two native backends without knowing which shape a backend has
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/545). A backend that can determine caught-up WITHOUT
        /// flushing does not count that call: the native Vulkan drain compares the timeline counter to the last
        /// SUBMITTED value, and on Vulkan that comparison genuinely means idle, because every GPU operation rides
        /// a <c>vkQueueSubmit</c> that took a timeline value. A backend whose drain must SIGNAL A FRESH POINT AND
        /// FLUSH counts every call instead, and the native Direct3D 11 one must: its immediate context can hold
        /// unflushed device-level work, from an <c>UpdateBuffer</c> or a map and unmap, that no fence value
        /// describes, so an early return there would report idle with work still buffered. Both are locally
        /// correct, and the consequence is that the same consumer call pattern reads higher on Direct3D 11 for a
        /// reason that is not a stall. <see cref="DrainMs"/> is barely affected, since a skipped drain costs about
        /// nothing, which is why the ms figure is the one to compare across backends and this count is not.
        /// </para>
        /// </summary>
        public long DrainCount { get; }

        /// <summary>Milliseconds those drains spent blocked, summed. The M2 number.</summary>
        public double DrainMs { get; }

        /// <summary>
        /// Waits that BLOCKED because the CPU ran further ahead of the GPU than this device's pipeline depth
        /// allows, since device creation. The M3 and MV3 number, and its exit criterion on both is that this does
        /// not move across a whole capture window. A non-zero reading says the pipeline is deeper than the
        /// frames-in-flight count allows on that machine, which is a tuning fact rather than a fault.
        /// <para>
        /// TWO KINDS OF WAIT LAND ON THIS ONE ACCUMULATOR, and the fold is deliberate rather than a widening
        /// nobody wrote down. The first is the FRAME BOUNDARY meeting a uniform ring segment the GPU had not
        /// finished reading, which is every backend that keeps a ring. The second is a COMMAND LIST'S <c>Begin</c>
        /// meeting its own oldest command-buffer slot still executing, which is the native Vulkan backend, where
        /// one number sizes both the ring segments and each list's pool ring. They are the same statement about
        /// the same lever, so a consumer reading a non-zero count reaches for the same knob either way and does
        /// not have to know which half moved. What the fold costs is the ability to tell them apart from here, and
        /// the backend keeps that breakdown internally for its own tests.
        /// </para>
        /// <para>
        /// THE SECOND MEANING IS VULKAN'S, NOT UNIVERSAL, AND THAT MAKES THE NUMBER SHARPER ELSEWHERE. The native
        /// Metal backend has no command-buffer pool to wait on at all: an <c>MTLCommandQueue</c> owns that
        /// allocation and hands out a fresh buffer per recording, so there is no pool to reset and nothing for a
        /// <c>Begin</c> to meet. Every unit on this counter there is the ring's segment acquire alone, which makes
        /// a non-zero reading on Metal unambiguously a statement about <c>KE_METAL_FRAMES_IN_FLIGHT</c>, where the
        /// same number on Vulkan folds two sources and has to be split by that backend's own breakdown before it
        /// can be read as one.
        /// </para>
        /// </summary>
        public long BackpressureStallCount { get; }

        /// <summary>Milliseconds those stalls spent blocked, summed. Carried beside the count because a count with
        /// no cost attached cannot be weighed against raising the segment count, which costs memory in every
        /// uniform buffer at once.</summary>
        public double BackpressureStallMs { get; }

        /// <summary>
        /// Device-level buffer writes that met a ring segment an earlier frame was still reading and were QUEUED
        /// for that segment's next reopen, since device creation. NOT a stall and not part of
        /// <see cref="BackpressureStallCount"/>: nothing blocks on this path, and the writes it counts are usually
        /// load-time, before any frame has begun. It carries no duration for the same reason, since a millisecond
        /// field here would read as "the waits are cheap" rather than as "there are none".
        /// </summary>
        public long OffTimelineDeferred { get; }

        /// <summary>
        /// How many of those queued writes are STILL waiting for their segment to be reopened. A number that keeps
        /// climbing rather than settling means writes are being queued for a segment nothing ever acquires, which
        /// on a running device means frames stopped. The backend's own breakdown of how each queued write left the
        /// queue stays internal to it, because this pair is what a soak acts on.
        /// </summary>
        public long OffTimelineOutstanding { get; }

        /// <summary>
        /// Present boundaries that BLOCKED waiting for the presentation engine to hand back the next swapchain
        /// image, since device creation. A backend that acquires with a semaphore and lets the GPU do the waiting
        /// reports zero here and a backend that blocks the CPU on the acquire reports one per frame, which is the
        /// entire difference the two positions of that choice produce and the reason this pair exists at all.
        /// <para>
        /// ZERO IS A READING RATHER THAN A GAP on a backend with no acquire to wait on, and on a headless device
        /// with no swapchain. Nothing else on this struct can distinguish "the CPU never waited" from "nobody
        /// looked", which is what <see cref="HasValue"/> answers for the whole set.
        /// </para>
        /// </summary>
        public long AcquireWaitCount { get; }

        /// <summary>
        /// Milliseconds those acquire waits spent blocked, summed. Carried beside the count for the reason
        /// <see cref="BackpressureStallMs"/> is: a count with no cost attached cannot be weighed, and a duration
        /// with no count cannot tell one long wait from many short ones. On a machine running at a pinned refresh
        /// rate this is the only number that separates a CPU-blocking acquire from a semaphore one, because both
        /// produce the same mean frame time by construction.
        /// </summary>
        public double AcquireWaitMs { get; }
    }
}
