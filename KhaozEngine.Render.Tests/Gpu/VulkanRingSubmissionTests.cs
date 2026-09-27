using System;
using System.Threading;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A SUBMITTED RECORDING KEEPS ITS UNIFORMS on the native Vulkan backend, the same statement as
    /// <see cref="D3D11RingSubmissionTests"/> over this backend's mechanism. A record-time write is a memcpy into the
    /// current ring segment, and a submission reads that segment when the GPU gets to it. So a record-time write
    /// that follows a submission which carried record-time writes opens the next segment first, with the same close,
    /// gate and publish the present applies, and never lands in memory a queued submission still has to read.
    /// <para>
    /// The present was the only rotation, and a headless loop never reaches it, so every frame of a
    /// <see cref="KhaozEngine.Render3D.Render3DSnapshot"/> capture wrote into the one segment its queued frames were
    /// reading. A single present-bounded submission per frame is unchanged. Device-free on every operating system: the
    /// harness submits by registering timeline values exactly as the submit queue does.
    /// </para>
    /// </summary>
    public sealed class VulkanRingSubmissionTests
    {
        static readonly byte[] First = { 11, 12, 13, 14 };
        static readonly byte[] Second = { 21, 22, 23, 24 };
        static readonly byte[] Third = { 31, 32, 33, 34 };

        [Fact]
        public void A_write_after_a_submission_that_wrote_lands_in_the_next_segment_and_leaves_the_submitted_one_alone()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, First);
            harness.Submit(1);   // the GPU has not reached it
            harness.Ring.Write(0, Second);

            Assert.Equal(1, harness.Allocator.CurrentSegment);
            Assert.Equal(First, Segment(harness, 0));
            Assert.Equal(Second, Segment(harness, 1));
            Assert.Equal(1UL, harness.Allocator.SegmentOwner(0));
            Assert.Equal(0, harness.Semaphore.WaitCount);
        }

        [Fact]
        public void Every_submission_of_a_headless_loop_keeps_its_own_segment_until_the_ring_wraps()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);
            harness.Complete(10);   // everything already finished, so the wrap is free

            byte[][] values = { First, Second, Third };
            for (int i = 0; i < values.Length; i++)
            {
                harness.Ring.Write(0, values[i]);
                Assert.Equal(i, harness.Allocator.CurrentSegment);
                harness.Submit((ulong)i + 1);
            }

            Assert.Equal(First, Segment(harness, 0));
            Assert.Equal(Second, Segment(harness, 1));
            Assert.Equal(Third, Segment(harness, 2));
            VulkanRingAllocator allocator = harness.Allocator;
            Assert.Equal(new[] { 1UL, 2UL, 0UL },
                new[] { allocator.SegmentOwner(0), allocator.SegmentOwner(1), allocator.SegmentOwner(2) });
        }

        [Fact]
        public void A_wrap_onto_a_segment_a_queued_submission_reads_waits_for_it_with_the_submit_lock_free()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 2);
            bool? heldDuringTheWait = null;
            harness.Semaphore.OnWait = () => heldDuringTheWait = Monitor.IsEntered(harness.SubmitLock);

            harness.Ring.Write(0, First);
            harness.Submit(1);                  // segment 0
            harness.Ring.Write(0, Second);      // segment 1, never used, no wait
            harness.Submit(2);
            harness.Ring.Write(0, Third);       // back to segment 0, which waits for value 1

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(1, harness.Semaphore.WaitCount);
            Assert.Equal(1UL, harness.Semaphore.LastWaitValue);
            Assert.False(heldDuringTheWait);
            Assert.Equal(1, harness.Allocator.StallCount);
            Assert.Equal(1, harness.Backpressure.Totals.Count);
            Assert.Equal(Third, Segment(harness, 0));
            Assert.Equal(Second, Segment(harness, 1));
        }

        [Fact]
        public void A_setup_flush_during_an_open_writing_recording_neither_rotates_nor_lets_the_segment_go_early()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 2);

            harness.Ring.Write(0, First);       // the frame's list writes into segment 0
            harness.SubmitSetup(1);             // a WaitForIdle or a Map flushes the setup buffer mid-recording
            harness.Ring.Write(0, Second);      // the same recording writes again

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(Second, Segment(harness, 0));

            harness.Submit(2);                  // the list itself
            harness.Ring.Write(0, Third);       // next recording: segment 1, and segment 0 closes at the list's value
            Assert.Equal(1, harness.Allocator.CurrentSegment);
            Assert.Equal(2UL, harness.Allocator.SegmentOwner(0));

            harness.Complete(1);                // the setup flush is done, the list is not
            harness.Submit(3);
            harness.Ring.Write(0, First);       // back to segment 0, which must wait for the list, not the flush

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(1, harness.Semaphore.WaitCount);
            Assert.Equal(2UL, harness.Semaphore.LastWaitValue);
        }

        [Fact]
        public void A_submission_that_wrote_no_uniforms_moves_nothing()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(1);   // a copy or an empty fenced submission
            harness.Ring.Write(0, First);

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(First, Segment(harness, 0));
        }

        [Fact]
        public void A_present_after_the_submission_is_the_only_rotation_of_a_windowed_frame()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, First);
            harness.Submit(1);
            harness.Allocator.BeginFrame();     // the present boundary
            harness.Ring.Write(0, Second);

            Assert.Equal(1, harness.Allocator.CurrentSegment);
            Assert.Equal(1UL, harness.Allocator.FrameIndex);
            Assert.Equal(Second, Segment(harness, 1));
            Assert.Equal(new byte[First.Length], Segment(harness, 2));
        }

        [Fact]
        public void Two_writing_submissions_then_a_present_leave_the_next_frames_first_write_where_the_present_put_it()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);
            harness.Complete(10);

            harness.Ring.Write(0, First);
            harness.Submit(1);                  // the frame's first writing submission, segment 0
            harness.Ring.Write(0, Second);      // a second recording in the same frame: segment 1
            harness.Submit(2);
            harness.Allocator.BeginFrame();     // the present: segment 2
            harness.Ring.Write(0, Third);       // the next frame's first write does not rotate again

            Assert.Equal(2, harness.Allocator.CurrentSegment);
            Assert.Equal(First, Segment(harness, 0));
            Assert.Equal(Second, Segment(harness, 1));
            Assert.Equal(Third, Segment(harness, 2));
            Assert.Equal(2UL, harness.Allocator.SegmentOwner(1));
        }

        [Fact]
        public void Writes_inside_one_recording_still_share_its_segment()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, First);
            harness.Ring.Write(0, Second);

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(Second, Segment(harness, 0));
        }

        [Fact]
        public void An_owed_rotation_under_the_submit_lock_is_refused_by_name()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, First);
            harness.Submit(1);

            InvalidOperationException refused;
            lock (harness.SubmitLock)
                refused = Assert.Throws<InvalidOperationException>(() => harness.Ring.Write(0, Second));

            Assert.Contains("submit lock", refused.Message);
            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(First, Segment(harness, 0));
        }

        static byte[] Segment(VulkanRingHarness harness, int segment)
        {
            int at = (int)harness.Ring.FrameBaseBytes(segment);
            return harness.Bytes.AsSpan(at, First.Length).ToArray();
        }
    }
}
