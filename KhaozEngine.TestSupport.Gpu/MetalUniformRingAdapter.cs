using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE METAL SIDE OF THE SHARED RING TESTS (M-P5, M-T5). It drives the SHIPPED
    /// <see cref="MetalRingAllocator"/> and <see cref="MetalUniformRing"/> over a pinned array standing in for a
    /// Shared <c>MTLBuffer</c>'s <c>contents()</c> pointer.
    ///
    /// <para><b>THE THIRD ADAPTER IS WHAT KEEPS THE SHARED ROWS SHARED.</b> Section 9.4 writes the ring policy
    /// out as a ten-row inventory precisely because the three backends deliberately do NOT share the ring's code,
    /// and "share the tests" with no adapter on the third side quietly becomes two backends' tests plus a third
    /// implementation nobody checked. Seven of those ten rows run here.</para>
    ///
    /// <para><b>WHAT EACH MEMBER MAPS ONTO, AND THE ONE THAT DIFFERS IN MECHANISM.</b> <c>SubmitWork</c> does
    /// what <c>MetalGpuDevice.SubmitOnMacOs</c> does inside its lock and nothing else: allocate and encode values
    /// up to V, REGISTER V as accepted, and hand the segment this recording captured to
    /// <see cref="MetalRingAllocator.RecordSegmentOwner"/> with that value, which is the step
    /// <c>MetalCommandList.MarkSubmitted</c> makes on the shipped path. <c>CompleteWork</c> advances the fake
    /// shared event's counter, which is what <c>signaledValue</c> answers and what the gate reads. Everything else
    /// is the shipped member of the same name.</para>
    ///
    /// <para><b>WHY THE VALUE IS REGISTERED AND NOT MERELY ALLOCATED, since the adapter has to choose.</b> A
    /// submit that threw between the allocation and the commit took a value nothing will ever signal, so a
    /// segment gated on the allocation high-water would block forever. Registering here is what makes the adapter
    /// model an ACCEPTED submission rather than an attempted one.</para>
    ///
    /// <para><b>ONE IMPLICIT RECORDING, WHICH IS WHAT THE SHARED INTERFACE DESCRIBES.</b> The shipped model is
    /// segment-per-RECORDING: a command list captures its segment at <c>Begin</c> and every record-time write and
    /// every submit of that recording names the capture. This adapter has no command list, so it holds the
    /// capture itself, and with exactly one recording open at a time the captured segment and the allocator's
    /// current segment are the same number. The rows that need two concurrent recordings are this backend's own
    /// and live in <c>MetalRecordingSegmentTests</c>, because the shared interface cannot express a second
    /// list.</para>
    ///
    /// <para><b>THERE IS NO MAPPING TO MODEL AND NO MEMORY TYPE TO CHOOSE.</b> Every buffer this backend creates
    /// is <c>MTLStorageModeShared</c> and its <c>contents()</c> pointer is stable for the buffer's life (M-M2),
    /// so the ring takes a pointer at construction and that is the whole of it. The Direct3D 11 adapter beside
    /// this one has to keep a map lifecycle out of the way and the Vulkan one has to map a host-visible chunk
    /// first. Here there is neither, which is the asymmetry section 9.2 predicts.</para>
    ///
    /// <para><b>AND THE ROTATION BOUNDARY IS A COMMAND LIST'S <c>Begin</c> ON THIS BACKEND rather than a present
    /// (M-R2), which the shared rows do not and should not see.</b> <see cref="BeginFrame"/> is the allocator's
    /// own member either way, named <c>BeginRecording</c> there because that is what it opens. Where it is CALLED
    /// from is a backend fact, and putting it in the interface would make a shared row assert about a call site
    /// rather than about the policy.</para>
    /// </summary>
    internal sealed class MetalUniformRingAdapter : IGpuUniformRingUnderTest
    {
        readonly byte[] _bytes;
        readonly GCHandle _pin;
        readonly SharedEvent _event;
        readonly MetalTimeline _timeline;
        readonly MetalRingAllocator _allocator;
        readonly MetalUniformRing _ring;

        // The segment the one implicit recording captured, which is what a command list holds on the shipped
        // path. Segment 0 before the first BeginFrame, exactly as a device's first recording finds it.
        int _segment;

        internal MetalUniformRingAdapter(uint sizeInBytes, int framesInFlight)
        {
            _event = new SharedEvent();
            _timeline = new MetalTimeline(_event);
            _allocator = new MetalRingAllocator(
                framesInFlight, _timeline, new WaitAccumulator(), new object());

            _bytes = new byte[(int)MetalRingStride.TotalBytesFor(sizeInBytes, framesInFlight)];
            _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);

            _ring = new MetalUniformRing(_allocator, _pin.AddrOfPinnedObject(), sizeInBytes);
        }

        /// <inheritdoc/>
        public string BackendName => "MetalNative";

        /// <inheritdoc/>
        public int FramesInFlight => _allocator.FramesInFlight;

        /// <inheritdoc/>
        public uint LogicalSizeBytes => _ring.SizeInBytes;

        /// <inheritdoc/>
        public int CurrentSegment => _allocator.CurrentSegment;

        /// <inheritdoc/>
        public int StallCount => _allocator.StallCount;

        /// <inheritdoc/>
        public int PendingPatchCount => _ring.PendingPatchCount;

        /// <inheritdoc/>
        public ulong SegmentBaseBytes(int segment) => _ring.SegmentBaseBytes(segment);

        /// <inheritdoc/>
        public void SubmitWork(ulong completionValue)
        {
            // Values are allocated one at a time, and each one ENCODES its signal, exactly as a submission does:
            // allocation and encode are one step on this backend, so the timeline's own monotonicity is respected
            // rather than side-stepped by writing the field.
            while (_timeline.LastAllocated < completionValue)
            {
                _timeline.EncodeSignalForSubmit(IntPtr.Zero);
            }

            _timeline.RegisterSubmitted(completionValue);

            // And the segment this recording captured is what the submission READS, which is the gate's input.
            // MetalCommandList.MarkSubmitted makes this call on the shipped path, inside the submit lock.
            _allocator.RecordSegmentOwner(_segment, completionValue);
        }

        /// <inheritdoc/>
        public void CompleteWork(ulong completionValue) => _event.Completed = completionValue;

        /// <inheritdoc/>
        public void BeginFrame() => _segment = _allocator.BeginRecording();

        /// <inheritdoc/>
        public void WriteAtRecordTime(uint offsetBytes, ReadOnlySpan<byte> data)
            => _ring.Write(_segment, offsetBytes, data);

        /// <inheritdoc/>
        public void WriteOffTimeline(uint offsetBytes, ReadOnlySpan<byte> data)
            => _allocator.UpdateBuffer(_ring, offsetBytes, data);

        /// <inheritdoc/>
        public byte[] ReadSegment(int segment, uint offsetBytes, int length)
            => _ring.ReadSegment(segment, offsetBytes, length);

        /// <inheritdoc/>
        public void Dispose()
        {
            _allocator.Forget(_ring);
            _timeline.Dispose();
            if (_pin.IsAllocated) _pin.Free();
        }

        // The MTLSharedEvent's three native calls with no device behind them. WaitUntil jumps the counter to the
        // value asked for, which is what a GPU that finishes the work does, so a shared row that drives a real
        // stall terminates instead of hanging the suite. The timeout is ignored for the same reason: the slice
        // exists to make a device-loss liveness flip observable, and nothing here can die.
        sealed class SharedEvent : IMetalSharedEvent
        {
            ulong _completed;

            internal ulong Completed
            {
                get => System.Threading.Volatile.Read(ref _completed);
                set => System.Threading.Volatile.Write(ref _completed, value);
            }

            public ulong Read() => Completed;

            public bool WaitUntil(ulong value, ulong timeoutMs)
            {
                if (Completed < value) Completed = value;
                return true;
            }

            public void EncodeSignal(IntPtr commandBuffer, ulong value)
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
