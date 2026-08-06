using System;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE TEST-ONLY INTERFACE THE UNIFORM RING'S SEMANTIC TESTS RUN THROUGH, on BOTH backends (decisions V-P5
    /// and V-T6 of <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>).
    ///
    /// <para><b>WHY THE TESTS ARE SHARED AND THE CODE IS NOT.</b> Section 2.2 weighed extracting the ring into a
    /// shared production home and declined it on the rule of three: the ring's POLICY is genuinely identical
    /// across the two backends and its MECHANISM is not. Direct3D 11 has no persistent mapping, so its ring
    /// carries a whole map lifecycle, a mapped-ring registry and a per-submit unmap. Vulkan maps a host-visible
    /// chunk once and never unmaps, so its ring is a pointer plus arithmetic. Shared code would prove one
    /// implementation exists. A shared TEST proves both implementations behave.</para>
    ///
    /// <para><b>THIS INTERFACE IS ITSELF AN ABSTRACTION DERIVED FROM ONE IMPLEMENTATION, and 2.2 names that cost
    /// rather than presenting it as free.</b> It is a much smaller one than a shared ring would be, and it is
    /// deliberately shaped from what BOTH rings can answer honestly rather than from what either one exposes.
    /// Every member below is either in the design's own list (acquire a segment, write off-timeline, read a segment
    /// base, read the stall count) or is a plain consequence of it: a policy about WHERE bytes land is not
    /// observable without reading bytes back, and a gate is not observable without driving the two values it
    /// compares.</para>
    ///
    /// <para><b>WHAT IT DELIBERATELY DOES NOT CARRY.</b> Nothing about mapping, because one backend has no
    /// mapping to speak of. Nothing about locks, because 9.4 assigns Lock legality to each backend's own tests:
    /// each has its own lock and its own deadlock to not have, and a shared test would assert against a lock this
    /// interface cannot see. Nothing about the stride arithmetic, for the same reason: 9.4 assigns Stride to each
    /// backend too, since the arithmetic differs (a 16-constant count there, a
    /// <c>minUniformBufferOffsetAlignment</c> floor and a descriptor range here) where the invariant does not, and
    /// the Vulkan half additionally answers to a VUID. Nothing about ORDERING either, which 9.4 records as a
    /// BUILD-ORDER fact rather than a runtime semantic: what enforces it is the Vulkan ring row depending on the
    /// completion-primitive row, and there is nothing a test can observe.</para>
    ///
    /// <para><b>SEVEN OF SECTION 9.4'S TEN ROWS RUN THROUGH HERE:</b> segment selection, fence gating,
    /// backpressure counting, the off-timeline every-segment reach, its gating, its never-blocking pending-patch
    /// queue, and record-time writes staying current-segment. The other three are the ones named above.</para>
    /// </summary>
    internal interface IGpuUniformRingUnderTest : IDisposable
    {
        /// <summary>Which backend this adapter drives, for the failure message of a shared row: an assertion that
        /// fires on one of two adapters has to say which.</summary>
        string BackendName { get; }

        /// <summary>How many segments the ring is cut into, which is the device's frames-in-flight.</summary>
        int FramesInFlight { get; }

        /// <summary>The buffer's LOGICAL size, which is the only size the seam ever sees and the bound both write
        /// shapes are validated against.</summary>
        uint LogicalSizeBytes { get; }

        /// <summary>The segment the next submit will bind, which is the one a record-time write lands in. Frame N
        /// is <c>N % FramesInFlight</c>, and asserting that is section 9.4's Segment selection row.</summary>
        int CurrentSegment { get; }

        /// <summary>Segment acquisitions that ACTUALLY BLOCKED. Section 9.4's Backpressure row: a poll that found
        /// the GPU already caught up is not a stall, so this staying at zero through an unstalled run is as much
        /// the assertion as it moving through a stalled one.</summary>
        int StallCount { get; }

        /// <summary>How many off-timeline writes are queued as pending patches right now, across every segment.
        /// Section 9.4's never-blocks row: a write that met an in-flight segment returns having queued rather than
        /// having waited.</summary>
        int PendingPatchCount { get; }

        /// <summary>The byte offset of one segment inside the whole allocation. The "read a segment base" member of
        /// the design's own list.</summary>
        ulong SegmentBaseBytes(int segment);

        /// <summary>
        /// The current frame submitted work that will signal <paramref name="completionValue"/>. Each backend
        /// expresses this differently (a callback from the submit path there, the timeline's own registered
        /// high-water here), and what both agree on is the OBSERVABLE: after this, the current segment may not be
        /// handed out again until the GPU has reached that value.
        /// </summary>
        void SubmitWork(ulong completionValue);

        /// <summary>The GPU reached <paramref name="completionValue"/>. The other half of the gate's input.
        /// </summary>
        void CompleteWork(ulong completionValue);

        /// <summary>
        /// The frame boundary: close the segment just written, advance, WAIT there if the GPU has not finished with
        /// it, replay anything queued for it, and publish it. The "acquire a segment" member of the design's own
        /// list, and the only member here that can block.
        /// </summary>
        void BeginFrame();

        /// <summary>A record-time <c>UpdateBuffer</c>: the CURRENT segment alone (section 9.4's Record-time writes
        /// row).</summary>
        void WriteAtRecordTime(uint offsetBytes, ReadOnlySpan<byte> data);

        /// <summary>A device-level <c>UpdateBuffer</c>: EVERY segment, gated, deferred rather than waited for. The
        /// "write off-timeline" member of the design's own list, and the resolution of
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/484 that section 9.4 gives three rows to.</summary>
        void WriteOffTimeline(uint offsetBytes, ReadOnlySpan<byte> data);

        /// <summary>What one segment currently holds. A policy about WHERE bytes land is not observable without
        /// this, which is why it is here beside the four members the design names.</summary>
        byte[] ReadSegment(int segment, uint offsetBytes, int length);
    }
}
