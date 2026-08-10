using System;
using System.Threading;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SEGMENT-PER-RECORDING VERSIONING, WITH MORE THAN ONE RECORDING OPEN, device-free. These are the rows the
    /// shared ring interface cannot express, because it models exactly one implicit recording and every claim
    /// here is about what happens when a SECOND list begins.
    ///
    /// <para><b>WHY THAT IS THE ORDINARY CASE RATHER THAN AN EXOTIC ONE.</b> Rotation happens at
    /// <c>MetalCommandList.Begin</c> on this backend, and shipped engine paths open several lists per frame:
    /// <c>Render3DPreview.Capture</c>, every <c>OceanFftProducer</c> prime pass, and one per
    /// <c>RetireBarrier.Submit</c> whenever completion fences are supported, which on this backend is always. So a
    /// recording that read the allocator's live segment at write time would be reading a number another list moves
    /// under it, several times a frame, in a shipped frame loop.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either a recording's writes landed in a version it does not bind (which
    /// presents as another list's uniforms, intermittently, with nothing thrown), or a segment's owner names the
    /// wrong submission (which presents as the gate handing a segment out while the GPU is still reading it,
    /// several frames from the cause), or two concurrent <c>Begin</c>s claimed one segment (which is both at
    /// once). None of the three is visible on a device and none is visible to a golden.</para>
    /// </summary>
    public sealed class MetalRecordingSegmentTests : IDisposable
    {
        readonly MetalRingHarness _harness = new();
        readonly object _owner = new();

        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        /// <summary>
        /// THE HEADLINE: each recording's writes land in the segment IT captured, and a second list beginning
        /// mid-recording does not move them. The last write here is made AFTER the other list has rotated the
        /// allocator, which is the exact ordering that would have sent it into the other recording's version.
        /// </summary>
        [Fact]
        public void EachRecordingsWritesLandInTheSegmentItCaptured()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);
            using MetalCommandList first = _harness.NewList(_owner);
            using MetalCommandList second = _harness.NewList(_owner);

            byte[] firstPayload = Payload(32, seed: 1);
            byte[] secondPayload = Payload(32, seed: 2);

            first.Begin();
            Record(first, ring, firstPayload);

            // The second Begin rotates the allocator's current segment. The first list is still recording.
            second.Begin();
            Record(second, ring, secondPayload);

            // And the first list writes again, with the allocator's current segment now naming the SECOND list's
            // version. This is the write that used to land in the wrong segment.
            Record(first, ring, firstPayload);

            Assert.Equal(1, first.RingSegment);
            Assert.Equal(2, second.RingSegment);
            Assert.NotEqual(first.RingSegment, second.RingSegment);

            Assert.Equal(firstPayload, ring.ReadSegment(first.RingSegment, 0, firstPayload.Length));
            Assert.Equal(secondPayload, ring.ReadSegment(second.RingSegment, 0, secondPayload.Length));

            first.End();
            second.End();
            _harness.Submit(first);
            _harness.Submit(second);
        }

        /// <summary>
        /// A SEGMENT'S OWNER IS THE SUBMISSION THAT READ IT, and not the highest value submitted by then. The two
        /// lists submit in the OPPOSITE order to the one they began in, so the segment claimed first ends up owned
        /// by the LARGER value: a design that read the timeline's high-water when a segment stopped being current
        /// would give both segments the same number.
        /// </summary>
        [Fact]
        public void EachSegmentIsOwnedByTheValueOfTheSubmissionThatReadIt()
        {
            using MetalCommandList first = _harness.NewList(_owner);
            using MetalCommandList second = _harness.NewList(_owner);

            first.Begin();
            second.Begin();
            first.End();
            second.End();

            ulong secondValue = _harness.Submit(second);
            ulong firstValue = _harness.Submit(first);

            Assert.True(firstValue > secondValue, "the second submission has to take the higher value");

            Assert.Equal(firstValue, _harness.Rings.SegmentOwner(first.RingSegment));
            Assert.Equal(secondValue, _harness.Rings.SegmentOwner(second.RingSegment));
        }

        /// <summary>
        /// THE INTERLEAVING THAT USED TO UNDER-RECORD: a list ENDS, another list's <c>Begin</c> rotates past it,
        /// and only THEN does it submit. Recording the owner when the segment stopped being current would have
        /// read the timeline before that submission existed, leaving the segment owned by nothing, and the wrap
        /// back to it would have started writing while a live submission was still reading it.
        /// <para>
        /// The assertion is on the value the gate WAITED FOR, not merely on the stall count, because a stall on
        /// the wrong value is the failure this closes.
        /// </para>
        /// </summary>
        [Fact]
        public void TheWrapWaitsForTheLateSubmissionThatActuallyReadTheSegment()
        {
            using MetalCommandList first = _harness.NewList(_owner);
            using MetalCommandList second = _harness.NewList(_owner);

            first.Begin();
            int owned = first.RingSegment;
            first.End();

            // The rotation moves past the first list's segment BEFORE that list has submitted anything.
            second.Begin();

            ulong firstValue = _harness.Submit(first);
            second.End();
            _harness.Submit(second);

            Assert.Equal(firstValue, _harness.Rings.SegmentOwner(owned));
            Assert.Equal(0, _harness.Rings.StallCount);

            // All the way round to the segment the first recording used, with the GPU still at zero.
            first.Begin();
            first.End();
            _harness.Submit(first);

            first.Begin();

            Assert.Equal(owned, first.RingSegment);
            Assert.Equal(1, _harness.Rings.StallCount);
            Assert.Equal(firstValue, _harness.Event.LastWaitValue);

            first.End();
            _harness.Submit(first);
        }

        /// <summary>
        /// TWO THREADS BEGINNING AT ONCE CLAIM TWO DIFFERENT SEGMENTS AND SKIP NONE, which is what the rotation
        /// being one atomic step under the submit lock buys. The index was a plain <c>++</c> before, and two
        /// threads reading it together would hand one segment to two live recordings, so both would write one
        /// version and both submissions would read it.
        /// <para>
        /// NO SLEEPS AND NO TIMING. The barrier aligns the two starts, the assertion is the postcondition of the
        /// round, and every round is checked, so this is a tripwire on the invariant rather than a race the test
        /// hopes to lose.
        /// </para>
        /// </summary>
        [Fact]
        public void TwoConcurrentBeginsClaimDifferentSegmentsAndSkipNone()
        {
            const int rounds = 64;

            using MetalCommandList first = _harness.NewList(_owner);
            using MetalCommandList second = _harness.NewList(_owner);

            for (int round = 0; round < rounds; round++)
            {
                using var start = new Barrier(2);

                var begunFirst = new Thread(() =>
                {
                    start.SignalAndWait();
                    first.Begin();
                });

                var begunSecond = new Thread(() =>
                {
                    start.SignalAndWait();
                    second.Begin();
                });

                begunFirst.Start();
                begunSecond.Start();
                begunFirst.Join();
                begunSecond.Join();

                Assert.NotEqual(first.RingSegment, second.RingSegment);

                // Exactly two claims happened, so nothing was skipped and nothing was claimed twice. Which of the
                // two threads got the lower index is genuinely undecided, so the pair is asserted as a set.
                Assert.Equal((ulong)((round + 1) * 2), _harness.Rings.RecordingIndex);

                int depth = _harness.FramesInFlight;
                int lower = (int)((((ulong)round * 2) + 1) % (ulong)depth);
                int upper = (int)((((ulong)round * 2) + 2) % (ulong)depth);

                Assert.True(
                    (first.RingSegment == lower && second.RingSegment == upper)
                    || (first.RingSegment == upper && second.RingSegment == lower),
                    $"round {round} claimed {first.RingSegment} and {second.RingSegment} where the rotation owed "
                    + $"{lower} and {upper}");

                // Ended and NOT submitted, so no owner is recorded and no later round can stall. The next Begin
                // discards each sealed record, which is the seam's own reusable-list case.
                first.End();
                second.End();
            }

            Assert.Equal(0, _harness.Rings.StallCount);
        }

        // The record-time upload exactly as MetalCommandList.UpdateBufferCore makes it, with the list's CAPTURED
        // segment. It goes through MetalBufferUpload rather than through list.UpdateBuffer because that overload
        // needs a MetalBuffer, and a MetalBuffer cannot be built without an MTLDevice: its only constructor runs
        // behind MetalBuffer.Create, which allocates. The nil destination handle is the staging path's and this
        // is the ring path, which never reads it.
        void Record(MetalCommandList list, MetalUniformRing ring, byte[] data)
            => MetalBufferUpload.Record(ring, list.RingSegment, IntPtr.Zero, ring.SizeInBytes, 0, data,
                list.Encoders, list.Arena, _harness.Blit);

        static byte[] Payload(int length, byte seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)((seed * 17) + i + 1);
            return bytes;
        }
    }
}
