using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SECTION 9.4'S SEVEN SHARED ROWS, RUN AGAINST ALL THREE BACKENDS' UNIFORM RINGS through the one test-only
    /// interface of decisions V-P5 and V-T6
    /// (<c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>), joined by the native Metal ring at M-P5
    /// and M-T5 (<c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>).
    ///
    /// <para><b>WHY THIS FILE EXISTS AT ALL.</b> Section 2.2 declined to extract the ring into a shared production
    /// home, on the rule of three and on the observation that the POLICY is identical while the MECHANISM is not.
    /// The Metal phase reached the rule of three and declined it AGAIN for the same reason, in its own 2.8, which
    /// makes this file the thing carrying the policy rather than a stopgap until an extraction. A decision not to
    /// share code is not a decision to re-derive the policy from memory, so 9.4 writes the policy out as a
    /// ten-row inventory with an OWNER per row, and these are the seven whose owner is "shared". Shared code
    /// would prove one implementation exists. A shared test proves all three behave.</para>
    ///
    /// <para><b>THE OTHER THREE ROWS ARE EACH BACKEND'S OWN AND ARE NOT HERE.</b> Ordering is a BUILD-ORDER fact
    /// with nothing a test can observe, enforced by each ring row depending on its backend's completion-primitive
    /// row. Lock legality is per-backend because each has its own lock and its own deadlock to not have, and a
    /// shared test would assert against a lock the interface cannot see. Stride is per-backend because the
    /// arithmetic differs where the invariant does not: the Vulkan half additionally answers to a VUID and the
    /// Metal half floors flat at 256 against a device limit it could have read. See
    /// <see cref="VulkanUniformRingTests"/>, <see cref="D3D11UniformRingTests"/> and
    /// <see cref="MetalUniformRingTests"/>.</para>
    ///
    /// <para><b>EVERY ROW HERE RUNS DEVICE-FREE ON EVERY ADAPTER AND NONE SKIPS.</b> A shared row that could skip
    /// on one side is a shared row that quietly became one backend's, which is the outcome V-P5 exists to
    /// prevent, so these are plain <c>[Theory]</c> cases rather than <c>[GpuTheory]</c> ones.</para>
    /// </summary>
    public sealed class GpuUniformRingSharedTests
    {
        const uint Size = 256;
        const int Frames = 3;

        /// <summary>Every adapter is reachable and none is silently absent, which is the assertion the whole file
        /// rests on: a shared row list that lost one adapter would still pass every row below it, and the row
        /// below it would then be one backend's test wearing a shared name.</summary>
        [Fact]
        public void EveryBackend_IsUnderTest()
        {
            string[] names = GpuUniformRingAdapters.All().Select(row => (string)row[0]).ToArray();

            Assert.Equal(3, names.Length);
            Assert.Contains(GpuUniformRingAdapters.Direct3D11, names);
            Assert.Contains(GpuUniformRingAdapters.Vulkan, names);
            Assert.Contains(GpuUniformRingAdapters.Metal, names);

            foreach (string name in names)
            {
                using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(name, Size, Frames);
                Assert.Equal(name, ring.BackendName);
                Assert.Equal(Frames, ring.FramesInFlight);
                Assert.Equal(Size, ring.LogicalSizeBytes);
            }
        }

        // ---- 9.4 row: Segment selection -------------------------------------------------------------------

        /// <summary>
        /// FRAME N USES SEGMENT <c>N % FramesInFlight</c>, and the base is applied at BIND rather than baked into a
        /// descriptor or a resource set. The bases are evenly spaced and every one of them is at least the logical
        /// size apart, which is the mechanism-free half of the statement: the exact stride arithmetic is each
        /// backend's own row.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void SegmentSelection_WalksTheSegmentsAndWraps(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            Assert.Equal(0, ring.CurrentSegment);
            Assert.Equal(0ul, ring.SegmentBaseBytes(0));

            ulong stride = ring.SegmentBaseBytes(1) - ring.SegmentBaseBytes(0);
            Assert.True(stride >= ring.LogicalSizeBytes,
                $"{ring.BackendName}: a segment stride of {stride} is under the buffer's own "
                + $"{ring.LogicalSizeBytes} bytes, so two segments overlap.");

            for (int segment = 1; segment < ring.FramesInFlight; segment++)
            {
                Assert.Equal(stride * (ulong)segment, ring.SegmentBaseBytes(segment));
            }

            for (int frame = 1; frame <= ring.FramesInFlight * 2; frame++)
            {
                ring.BeginFrame();
                Assert.Equal(frame % ring.FramesInFlight, ring.CurrentSegment);
            }
        }

        // ---- 9.4 row: Fence gating --------------------------------------------------------------------------

        /// <summary>
        /// A SEGMENT IS ACQUIRED ONLY AFTER THE COMPLETION VALUE ITS FRAME RECORDED HAS BEEN REACHED, and that
        /// value is a COMPLETION read rather than a submit receipt. A ring gated on a receipt would hand back a
        /// segment the moment the CPU finished ASKING for the work, and would overwrite uniforms a draw in flight
        /// is still reading, with nothing thrown and nothing logged.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void FenceGating_ASegmentWhoseWorkFinished_CostsNoStall(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            ring.SubmitWork(5);
            ring.CompleteWork(9);

            // All the way round, back onto the segment whose frame was closed at 5, with the GPU already past it.
            for (int frame = 0; frame < ring.FramesInFlight; frame++) ring.BeginFrame();

            Assert.Equal(0, ring.StallCount);
        }

        // ---- 9.4 row: Backpressure --------------------------------------------------------------------------

        /// <summary>
        /// A BLOCKED ACQUIRE IS COUNTED AND AN UNBLOCKED ONE IS NOT, which is what makes zero a meaningful reading
        /// rather than an artefact. The bet both backends carry is that three segments mean this never happens at
        /// all, so a non-zero count says the pipeline is deeper than the segment count allows on that machine and
        /// the frames-in-flight knob is the lever, not that the design is wrong.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void Backpressure_ASegmentStillInFlight_BlocksAndIsCounted(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            ring.SubmitWork(7);
            for (int frame = 0; frame < ring.FramesInFlight - 1; frame++) ring.BeginFrame();

            Assert.Equal(0, ring.StallCount);

            // The GPU has reached nothing, and the next boundary comes back to the segment closed at 7.
            ring.BeginFrame();

            Assert.Equal(1, ring.StallCount);
        }

        // ---- 9.4 row: Off-timeline reach (#484) --------------------------------------------------------------

        /// <summary>
        /// A DEVICE-LEVEL WRITE REACHES EVERY SEGMENT. A value written ONCE persists for the buffer's life exactly
        /// as it does on a backend where the buffer has one copy, and reaching the current segment alone was a
        /// shipped DEFECT for one release: a load-time write held only until the frame index wrapped, so two frames
        /// in three bound memory nothing had ever written, intermittently, with nothing thrown and nothing logged.
        /// This is the resolution of https://github.com/APKiwiOrg/KhaozEngine/issues/484 and the row that must
        /// never regress on either backend.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void OffTimelineReach_AtLoadTime_LandsInEverySegmentByteForByte(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            byte[] payload = Payload(16, seed: 1);
            ring.WriteOffTimeline(32, payload);

            for (int segment = 0; segment < ring.FramesInFlight; segment++)
            {
                Assert.Equal(payload, ring.ReadSegment(segment, 32, payload.Length));
            }

            // Nothing was submitted, so no segment was in flight and nothing had to be deferred at all.
            Assert.Equal(0, ring.PendingPatchCount);
        }

        // ---- 9.4 row: Off-timeline gating --------------------------------------------------------------------

        /// <summary>
        /// THE NON-CURRENT SEGMENTS ARE GATED ON THE SAME COMPLETION READ, AND THE CURRENT ONE IS UNGATED. Writing
        /// into a segment the GPU is still reading is the data race the gate exists to prevent, so a segment whose
        /// work has not completed takes a pending patch instead. The current segment is always copied, because
        /// gating it would change the documented semantic that the write lands when it is called and the next list
        /// submitted reads it.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void OffTimelineGating_InFlightSegmentsDefer_TheCurrentOneDoesNot(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            // Close two segments against submitted work the GPU has not reached, leaving the third current.
            ring.SubmitWork(5);
            ring.BeginFrame();
            ring.SubmitWork(6);
            ring.BeginFrame();

            int current = ring.CurrentSegment;
            byte[] payload = Payload(8, seed: 2);
            ring.WriteOffTimeline(0, payload);

            Assert.Equal(payload, ring.ReadSegment(current, 0, payload.Length));
            Assert.Equal(ring.FramesInFlight - 1, ring.PendingPatchCount);

            for (int segment = 0; segment < ring.FramesInFlight; segment++)
            {
                if (segment == current) continue;

                Assert.Equal(new byte[payload.Length], ring.ReadSegment(segment, 0, payload.Length));
            }
        }

        // ---- 9.4 row: Off-timeline never blocks --------------------------------------------------------------

        /// <summary>
        /// A SEGMENT FAILING THE GATE IS QUEUED AND NEVER WAITED FOR, and the queued bytes land at that segment's
        /// NEXT ACQUIRE, right after the gate has proved the GPU is done with it. That is what makes the write
        /// legal from a caller already holding the submit lock, and it is what a retry loop cannot be: waiting for
        /// every non-current segment AT ONCE never terminates in the GPU-bound steady state, because the frame
        /// thread submits again for every frame the GPU retires.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void OffTimelineNeverBlocks_TheQueuedBytesLandAtThatSegmentsNextAcquire(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            ring.SubmitWork(5);
            ring.BeginFrame();
            ring.SubmitWork(6);
            ring.BeginFrame();

            byte[] payload = Payload(8, seed: 3);
            ring.WriteOffTimeline(64, payload);

            // The call returned rather than blocking, which is the whole claim, and the evidence it left is a
            // queue rather than a wait.
            Assert.Equal(ring.FramesInFlight - 1, ring.PendingPatchCount);

            // Walk all the way round. Every segment is acquired once, and every queued patch drains.
            for (int frame = 0; frame < ring.FramesInFlight; frame++) ring.BeginFrame();

            Assert.Equal(0, ring.PendingPatchCount);

            for (int segment = 0; segment < ring.FramesInFlight; segment++)
            {
                Assert.Equal(payload, ring.ReadSegment(segment, 64, payload.Length));
            }
        }

        /// <summary>
        /// TWO OFF-TIMELINE WRITES TO ONE SEGMENT REPLAY IN ARRIVAL ORDER, so the LAST one wins exactly as two
        /// direct copies into mapped memory would. Sorting or de-duplicating the queue by range would quietly
        /// reorder them, and a uniform buffer whose two halves came from different calls is precisely the class of
        /// bug a ring makes invisible.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void OffTimelineNeverBlocks_TwoQueuedWrites_ReplayInArrivalOrder(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            ring.SubmitWork(5);
            ring.BeginFrame();
            ring.SubmitWork(6);
            ring.BeginFrame();

            byte[] older = Payload(8, seed: 4);
            byte[] newer = Payload(8, seed: 5);
            ring.WriteOffTimeline(0, older);
            ring.WriteOffTimeline(0, newer);

            for (int frame = 0; frame < ring.FramesInFlight; frame++) ring.BeginFrame();

            for (int segment = 0; segment < ring.FramesInFlight; segment++)
            {
                Assert.Equal(newer, ring.ReadSegment(segment, 0, newer.Length));
            }
        }

        // ---- 9.4 row: Record-time writes ---------------------------------------------------------------------

        /// <summary>
        /// A RECORD-TIME WRITE STAYS IN THE CURRENT SEGMENT ALONE. The split is the CALL rather than a usage hint
        /// on the buffer, because the call is what knows whether it happens once: every shipped record-time uniform
        /// write is unconditional per frame, so replicating those would be <c>FramesInFlight</c> memcpys for a
        /// value the next frame overwrites, on the one path the whole design exists to make cheap.
        /// </summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void RecordTimeWrites_ReachTheCurrentSegmentAlone(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            ring.BeginFrame();

            int current = ring.CurrentSegment;
            byte[] payload = Payload(12, seed: 6);
            ring.WriteAtRecordTime(16, payload);

            Assert.Equal(payload, ring.ReadSegment(current, 16, payload.Length));

            for (int segment = 0; segment < ring.FramesInFlight; segment++)
            {
                if (segment == current) continue;

                Assert.Equal(new byte[payload.Length], ring.ReadSegment(segment, 16, payload.Length));
            }
        }

        /// <summary>Two record-time writes in one frame both land in that frame's segment, at their own offsets,
        /// which is what makes a frame's uniforms consistent: a bind computes its base from the same segment every
        /// write in that frame went to.</summary>
        [Theory]
        [MemberData(nameof(Backends))]
        public void RecordTimeWrites_InOneFrame_ShareThatFramesSegment(string backend)
        {
            using IGpuUniformRingUnderTest ring = GpuUniformRingAdapters.Create(backend, Size, Frames);

            ring.BeginFrame();
            ring.BeginFrame();

            int current = ring.CurrentSegment;
            byte[] first = Payload(4, seed: 7);
            byte[] second = Payload(4, seed: 8);

            ring.WriteAtRecordTime(0, first);
            ring.WriteAtRecordTime(128, second);

            Assert.Equal(first, ring.ReadSegment(current, 0, first.Length));
            Assert.Equal(second, ring.ReadSegment(current, 128, second.Length));
        }

        public static TheoryData<string> Backends
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (object[] row in GpuUniformRingAdapters.All()) data.Add((string)row[0]);
                return data;
            }
        }

        static byte[] Payload(int length, byte seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(seed * 17 + i + 1);
            return bytes;
        }
    }
}
