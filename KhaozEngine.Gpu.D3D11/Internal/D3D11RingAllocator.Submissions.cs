using System;
using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE RING'S SUBMISSION BOOKKEEPING: which segment a submission owns, and the rotation a record-time write owes
    /// once a submission has carried record-time writes out of the current segment. The frame boundary and the gate
    /// it shares live in <c>D3D11RingAllocator.cs</c>.
    /// </summary>
    internal sealed partial class D3D11RingAllocator
    {
        // Segment rotations since the device was created: one per frame boundary and one per rotation a record-time
        // write owes after a submission (see BeforeRecordWrite). CurrentSegment is this modulo FramesInFlight.
        ulong _rotationIndex;

        // Whether record-time writes have landed since the last submission, and whether a submission has since
        // carried such writes out of the current segment. Volatile, because the record path reads both without the
        // submit lock and OnSubmitted writes them under it. One thread records and submits (decision W5), so on the
        // shipped path the pair says exactly whether the next record-time write would land in memory a queued
        // submission reads.
        volatile bool _writtenSinceSubmit;
        volatile bool _currentSegmentSubmitted;

        /// <summary>
        /// RECORD WHICH SUBMISSION THE CURRENT SEGMENT WAS LAST USED BY, from the value that submission signalled.
        /// Called by the submit path right after the end-of-replay signal, inside the submit lock.
        /// <para>
        /// This is the other half of the gate. Without it a segment carries no target, so it is handed back out
        /// with no wait and the ring behaves exactly like the corruption U5 exists to prevent. A submit that
        /// signalled nothing (value 0) records nothing, which is why the drivers refuse a ring allocator handed to
        /// a submit with no signal sink.
        /// </para>
        /// <para>
        /// The value is monotonic, so the last submission of a frame is the highest, and taking the maximum keeps
        /// that true even if a caller records them out of order.
        /// </para>
        /// </summary>
        internal void OnSubmitted(ulong completionValue)
        {
            if (completionValue == 0) return;
            if (completionValue > _segmentOwner[_segment]) _segmentOwner[_segment] = completionValue;

            // A submission that carried record-time writes leaves the segment to that submission, so the next
            // record-time write opens another one (see BeforeRecordWrite).
            if (!_writtenSinceSubmit) return;
            _writtenSinceSubmit = false;
            _currentSegmentSubmitted = true;
        }

        /// <summary>
        /// BEFORE EVERY RECORD-TIME WRITE, from <see cref="D3D11UniformRing.Write"/>: once a submission has carried
        /// record-time writes out of the current segment, open the next segment before this write lands, behind the
        /// same completion gate the frame boundary applies.
        /// <para>
        /// A SUBMISSION READS ITS SEGMENT WHEN THE GPU GETS TO IT, which can be well after the submit returned, and a
        /// record-time write is a memcpy through a <c>NO_OVERWRITE</c> mapping. So a write into the segment a queued
        /// submission reads rewrites that submission's uniforms under it. The frame boundary is the only other
        /// rotation, and a headless loop never reaches it: every <c>Render3DSnapshot</c> capture and every GPU fact
        /// submits frame after frame with no present, and each queued frame drew the newest frame's uniforms. A
        /// windowed frame that opens a second recording after its first submit had the same exposure.
        /// </para>
        /// <para>
        /// THE WINDOWED FRAME IS UNCHANGED. Its one submission is followed by the present, whose rotation clears the
        /// owed one, so the next frame's writes open nothing. A submission that wrote no uniforms, a copy or an empty
        /// fenced submission, owes nothing either. The segment is then the uniform version one submission reads,
        /// which is the versioning the Metal backend applies at every recording.
        /// </para>
        /// <para>
        /// A replay binds the segment current when it runs, so a recording that writes uniforms is submitted before
        /// a later recording writes uniforms after another submission. That is the order one thread recording and
        /// submitting (decision W5) already gives. Called with the submit lock free, and refused otherwise, for the
        /// reason <see cref="BeginFrame"/> is: the gate can wait for the GPU.
        /// </para>
        /// </summary>
        internal void BeforeRecordWrite()
        {
            if (_currentSegmentSubmitted)
            {
                if (Monitor.IsEntered(_submitLock))
                {
                    throw new InvalidOperationException(
                        "A record-time uniform write on the native Direct3D 11 ring owed a segment rotation while the "
                        + "caller held the submit lock. The rotation waits for the GPU to finish with the segment it "
                        + "opens, and decision W4 holds the submit lock for microseconds. Record outside the lock.");
                }

                Rotate();
            }

            if (!_writtenSinceSubmit) _writtenSinceSubmit = true;
        }

        // Advance to the next segment, wait there while the GPU still reads it, then apply its pending patches and
        // publish it. The frame boundary and a record-time write's owed rotation both come here.
        void Rotate()
        {
            _rotationIndex++;
            int next = (int)(_rotationIndex % (ulong)FramesInFlight);
            AcquireSegment(next);
            AdoptSegmentUnderLock(next);
        }
    }
}
