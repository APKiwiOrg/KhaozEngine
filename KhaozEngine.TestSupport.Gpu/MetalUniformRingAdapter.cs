using System;
using System.Runtime.InteropServices;
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
    /// <para><b>WHAT EACH MEMBER MAPS ONTO, AND THE ONE THAT DIFFERS IN MECHANISM.</b> <c>SubmitWork</c> has no
    /// callback to make: this backend's segment owner is <see cref="MetalTimeline.LastSubmitted"/> read at the
    /// frame boundary under the submit lock, so the adapter expresses "the current frame submitted work
    /// signalling V" the way a real submit does, by allocating and encoding values up to V and then REGISTERING
    /// V. That is exactly what <c>MetalGpuDevice.SubmitOnMacOs</c> does inside its lock and nothing else.
    /// <c>CompleteWork</c> advances the fake shared event's counter, which is what <c>signaledValue</c> answers
    /// and what the gate reads. Everything else is the shipped member of the same name.</para>
    ///
    /// <para><b>WHY <see cref="MetalTimeline.LastSubmitted"/> AND NOT <see cref="MetalTimeline.LastAllocated"/>,
    /// since the adapter has to choose.</b> A submit that threw between the allocation and the commit took a
    /// value nothing will ever signal, so a segment gated on the allocation high-water would block forever.
    /// Registering here is what makes the adapter model an ACCEPTED submission rather than an attempted
    /// one.</para>
    ///
    /// <para><b>THERE IS NO MAPPING TO MODEL AND NO MEMORY TYPE TO CHOOSE.</b> Every buffer this backend creates
    /// is <c>MTLStorageModeShared</c> and its <c>contents()</c> pointer is stable for the buffer's life (M-M2),
    /// so the ring takes a pointer at construction and that is the whole of it. The Direct3D 11 adapter beside
    /// this one has to keep a map lifecycle out of the way and the Vulkan one has to map a host-visible chunk
    /// first. Here there is neither, which is the asymmetry section 9.2 predicts.</para>
    ///
    /// <para><b>AND THE FRAME BOUNDARY IS A COMMAND LIST'S <c>Begin</c> ON THIS BACKEND rather than a present
    /// (M-R2), which the shared rows do not and should not see.</b> <see cref="BeginFrame"/> is the allocator's
    /// own member either way. Where it is CALLED from is a backend fact, and putting it in the interface would
    /// make a shared row assert about a call site rather than about the policy.</para>
    /// </summary>
    internal sealed class MetalUniformRingAdapter : IGpuUniformRingUnderTest
    {
        readonly byte[] _bytes;
        readonly GCHandle _pin;
        readonly SharedEvent _event;
        readonly MetalTimeline _timeline;
        readonly MetalRingAllocator _allocator;
        readonly MetalUniformRing _ring;

        internal MetalUniformRingAdapter(uint sizeInBytes, int framesInFlight)
        {
            _event = new SharedEvent();
            _timeline = new MetalTimeline(_event);
            _allocator = new MetalRingAllocator(
                framesInFlight, _timeline, new MetalBackpressure(), new object());

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
        public ulong SegmentBaseBytes(int segment) => _ring.FrameBaseBytes(segment);

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
        }

        /// <inheritdoc/>
        public void CompleteWork(ulong completionValue) => _event.Completed = completionValue;

        /// <inheritdoc/>
        public void BeginFrame() => _allocator.BeginFrame();

        /// <inheritdoc/>
        public void WriteAtRecordTime(uint offsetBytes, ReadOnlySpan<byte> data) => _ring.Write(offsetBytes, data);

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
