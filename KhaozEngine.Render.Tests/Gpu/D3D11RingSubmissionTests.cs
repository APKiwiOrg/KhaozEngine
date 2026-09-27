using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A SUBMITTED RECORDING KEEPS ITS UNIFORMS. A record-time write is a memcpy into the current ring segment, and a
    /// submission binds that segment when the GPU gets to it, which can be well after the submit returns. So a record-time
    /// write that follows a submission which wrote uniforms starts the next segment, gated on completion exactly as the
    /// present boundary gates it, and never lands in memory a queued submission still has to read.
    /// <para>
    /// WHY THE PRESENT BOUNDARY ALONE WAS NOT ENOUGH. It is the only other place the segment moves, and a headless loop
    /// never reaches it: every <see cref="KhaozEngine.Render3D.Render3DSnapshot"/> capture and every GPU fact submits frame
    /// after frame with no present. Each frame's writes then landed in the one segment the queued frames were still
    /// reading, so every queued frame drew the newest frame's uniforms. A temporal frame rasterised and resolved with the
    /// newest frame's jitter, and its history accumulated one jittered frame over and over. A windowed frame that opens a
    /// second recording after its first submit had the same exposure.
    /// </para>
    /// <para>
    /// A single present-bounded submission per frame is unchanged: its writes stay in the segment the present opened,
    /// and the present is still the only rotation. Device-free on every operating system.
    /// </para>
    /// </summary>
    public sealed class D3D11RingSubmissionTests
    {
        static readonly byte[] First = { 11, 12, 13, 14 };
        static readonly byte[] Second = { 21, 22, 23, 24 };
        static readonly byte[] Third = { 31, 32, 33, 34 };

        [Fact]
        public void A_write_after_a_submission_that_wrote_lands_in_the_next_segment_and_leaves_the_submitted_one_alone()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            var submitter = new Submitter(harness);

            submitter.Record(First);
            submitter.Submit();   // value 1, which the GPU has not reached
            submitter.Record(Second);

            Assert.Equal(1, harness.Allocator.CurrentSegment);
            Assert.Equal(First, Segment(harness, 0));
            Assert.Equal(Second, Segment(harness, 1));
            Assert.Equal(1UL, harness.Allocator.SegmentOwner(0));
        }

        [Fact]
        public void Every_submission_of_a_headless_loop_keeps_its_own_segment_until_the_ring_wraps()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            harness.Completion.Completed = 10;   // everything already finished, so the wrap is free
            var submitter = new Submitter(harness);

            byte[][] values = { First, Second, Third };
            for (int i = 0; i < values.Length; i++)
            {
                submitter.Record(values[i]);
                Assert.Equal(i, harness.Allocator.CurrentSegment);
                submitter.Submit();
            }

            Assert.Equal(First, Segment(harness, 0));
            Assert.Equal(Second, Segment(harness, 1));
            Assert.Equal(Third, Segment(harness, 2));
            Assert.Equal(new[] { 1UL, 2UL, 3UL },
                new[] { harness.Allocator.SegmentOwner(0), harness.Allocator.SegmentOwner(1), harness.Allocator.SegmentOwner(2) });
        }

        [Fact]
        public void A_wrap_onto_a_segment_a_queued_submission_reads_waits_for_it_with_the_submit_lock_free()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 2);
            harness.Completion.SubmitLock = harness.SubmitLock;
            var submitter = new Submitter(harness);

            submitter.Record(First);
            submitter.Submit();                 // segment 0, value 1
            submitter.Record(Second);           // segment 1, never used, no wait
            submitter.Submit();                 // value 2
            harness.Completion.CompleteAfterPolls = harness.Completion.PollCount + 3;
            harness.Completion.CompleteTo = 1;
            submitter.Record(Third);            // back to segment 0, which waits for value 1

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.True(harness.Completion.Completed >= 1UL);
            Assert.Equal(1L, harness.Allocator.TotalBackpressure.Count);
            Assert.Equal(0, harness.Completion.PollsHoldingTheSubmitLock);
            Assert.Equal(Third, Segment(harness, 0));
            Assert.Equal(Second, Segment(harness, 1));
        }

        [Fact]
        public void A_segment_wait_flushes_the_submitted_work_once_before_it_spins_and_a_free_segment_flushes_nothing()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 2);
            harness.Completion.SubmitLock = harness.SubmitLock;
            var submitter = new Submitter(harness);

            submitter.Record(First);
            submitter.Submit();                 // segment 0, value 1
            submitter.Record(Second);           // segment 1 is free: one poll at most, no flush
            submitter.Submit();                 // value 2
            Assert.Equal(0, harness.Completion.FlushCount);

            int pollsBefore = harness.Completion.PollCount;
            harness.Completion.CompleteAfterPolls = pollsBefore + 3;
            harness.Completion.CompleteTo = 1;
            submitter.Record(Third);            // segment 0 waits for value 1

            Assert.Equal(1, harness.Completion.FlushCount);
            Assert.Equal(pollsBefore + 1, harness.Completion.PollCountAtFirstFlush);
            Assert.False(harness.Completion.LastFlushCallerHeldTheSubmitLock);
            Assert.Equal(Third, Segment(harness, 0));
        }

        [Fact]
        public void The_flush_before_a_segment_wait_reaches_the_timeline_under_the_submit_lock_and_a_dead_device_skips_it()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 3 };
            var liveness = new FakeD3D11DeviceLiveness();
            object submitLock = new();
            timeline.SubmitLock = submitLock;
            using var fences = new D3D11FenceSubsystem(timeline, submitLock, liveness);
            var allocator = new D3D11RingAllocator(2, fences, submitLock);

            allocator.OnSubmitted(fences.SignalEndOfReplay(null));
            allocator.BeginFrame();             // segment 1, never used: no flush
            Assert.Equal(0, timeline.FlushCount);

            allocator.BeginFrame();             // segment 0, owned by a value the GPU has not reached
            Assert.Equal(1, timeline.FlushCount);
            Assert.True(timeline.LastFlushHeldTheSubmitLock);
            Assert.Equal(1, timeline.PollCountAtFirstFlush);

            allocator.OnSubmitted(fences.SignalEndOfReplay(null));
            liveness.IsDead = true;
            allocator.BeginFrame();
            allocator.BeginFrame();             // back to segment 0: a dead device answers complete, flushes nothing
            Assert.Equal(1, timeline.FlushCount);
        }

        [Fact]
        public void A_submission_that_wrote_no_uniforms_moves_nothing()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            var submitter = new Submitter(harness);

            submitter.Submit();   // a copy or an empty fenced submission
            submitter.Record(First);

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(First, Segment(harness, 0));
        }

        [Fact]
        public void A_present_after_the_submission_is_the_only_rotation_of_a_windowed_frame()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            var submitter = new Submitter(harness);

            submitter.Record(First);
            submitter.Submit();
            harness.Allocator.BeginFrame();     // the present boundary
            submitter.Record(Second);

            Assert.Equal(1, harness.Allocator.CurrentSegment);
            Assert.Equal(1UL, harness.Allocator.FrameIndex);
            Assert.Equal(Second, Segment(harness, 1));
            Assert.Equal(new byte[First.Length], Segment(harness, 2));
        }

        [Fact]
        public void Writes_inside_one_recording_still_share_its_segment()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            var submitter = new Submitter(harness);

            submitter.Record(First, Second);

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(Second, Segment(harness, 0));
        }

        static byte[] Segment(D3D11RingHarness harness, int segment)
        {
            int at = (int)harness.Ring.FrameBaseBytes(segment);
            return harness.Memory.Bytes.AsSpan(at, First.Length).ToArray();
        }

        // One deferred list, recorded and submitted through the real driver with a signal and the harness's rings, the
        // way the device submits.
        sealed class Submitter
        {
            readonly D3D11RingHarness _harness;
            readonly D3D11SubmitSignalTests.FakeD3D11SubmitSignal _signal = new();
            readonly D3D11CountingEmitter _emitterTemplate = new(new D3D11EmitterCallLog());
            readonly IGpuCommandList _list = D3D11CommandDrivers.CreateDeferred();
            readonly FakeRingBackedBuffer _buffer;
            bool _pending;

            internal Submitter(D3D11RingHarness harness)
            {
                _harness = harness;
                _buffer = new FakeRingBackedBuffer(harness.Ring);
            }

            internal void Record(params byte[][] writes)
            {
                _list.Begin();
                foreach (byte[] bytes in writes) _list.UpdateBuffer<byte>(_buffer, 0, bytes);
                _list.Draw(1);
                _list.End();
                _pending = true;
            }

            internal void Submit()
            {
                if (!_pending)
                {
                    _list.Begin();
                    _list.End();
                }
                D3D11CountingEmitter emitter = _emitterTemplate;
                D3D11CommandDrivers.Submit(_harness.SubmitLock, _list, ref emitter, _signal, null, _harness.Allocator);
                _pending = false;
            }
        }
    }
}
