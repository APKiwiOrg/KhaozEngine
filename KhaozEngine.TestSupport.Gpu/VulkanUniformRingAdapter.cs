using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE VULKAN SIDE OF THE SHARED RING TESTS (V-P5). It drives the SHIPPED
    /// <see cref="VulkanRingAllocator"/> and <see cref="VulkanUniformRing"/> over a pinned array standing in for
    /// the chunk's persistent mapping.
    ///
    /// <para><b>WHAT EACH MEMBER MAPS ONTO, HONESTLY, AND THE ONE THAT DIFFERS IN MECHANISM.</b>
    /// <c>SubmitWork</c> has no callback to make here: this backend's segment owner is
    /// <see cref="VulkanTimeline.LastSubmitted"/> read at the frame boundary under the submit lock, so the adapter
    /// expresses "the current frame submitted work signalling V" the way a real submit does, by allocating values
    /// up to V on the timeline and REGISTERING V. That goes through
    /// <see cref="VulkanTimeline.NextSubmitValue"/> and <see cref="VulkanTimeline.RegisterSubmitted"/>, which is
    /// exactly what <c>VulkanSubmitQueue</c> does on a successful submit and nothing else.
    /// <c>CompleteWork</c> advances the fake semaphore's counter, which is what
    /// <c>vkGetSemaphoreCounterValue</c> answers and what the gate reads. Everything else is the shipped member of
    /// the same name.</para>
    ///
    /// <para><b>WHY <c>LastSubmitted</c> AND NOT <c>LastAllocated</c>, since the adapter has to choose.</b> A
    /// submit that failed with a non-loss result took a value nothing will ever signal, so a segment gated on the
    /// allocation high-water would block forever. The deferred-disposal retire list gates on the allocation
    /// high-water instead, for the opposite reason. Registering here is what makes the adapter model an ACCEPTED
    /// submission rather than an attempted one.</para>
    ///
    /// <para><b>THERE IS NO MAPPING TO MODEL.</b> Row 6 maps a host-visible chunk once at creation and never
    /// unmaps it, so the ring takes a pointer at construction and that is the whole of it. The Direct3D 11 adapter
    /// beside this one has to keep a map lifecycle out of the way. Here there is none to keep out of the way,
    /// which is the asymmetry section 9.2 predicts.</para>
    /// </summary>
    internal sealed class VulkanUniformRingAdapter : IGpuUniformRingUnderTest
    {
        readonly byte[] _bytes;
        readonly GCHandle _pin;
        readonly Semaphore _semaphore;
        readonly VulkanTimeline _timeline;
        readonly VulkanRingAllocator _allocator;
        readonly VulkanUniformRing _ring;

        internal VulkanUniformRingAdapter(uint sizeInBytes, int framesInFlight)
        {
            _semaphore = new Semaphore();
            _timeline = new VulkanTimeline(_semaphore);
            _allocator = new VulkanRingAllocator(
                framesInFlight, _timeline, new VulkanBackpressure(), new object());

            _bytes = new byte[(int)VulkanRingStride.TotalBytesFor(
                sizeInBytes, framesInFlight, VulkanRingStride.OffsetAlignmentFloor)];
            _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);

            _ring = new VulkanUniformRing(_allocator, _pin.AddrOfPinnedObject(), sizeInBytes);
        }

        /// <inheritdoc/>
        public string BackendName => "VulkanNative";

        /// <inheritdoc/>
        public int FramesInFlight => _allocator.FramesInFlight;

        /// <inheritdoc/>
        public uint LogicalSizeBytes => (uint)_ring.SizeInBytes;

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
            // Values are allocated one at a time, exactly as a submission takes one, so the timeline's own
            // monotonicity is respected rather than side-stepped by writing the field.
            while (_timeline.LastAllocated < completionValue) _timeline.NextSubmitValue();

            _timeline.RegisterSubmitted(completionValue);
        }

        /// <inheritdoc/>
        public void CompleteWork(ulong completionValue) => _semaphore.Completed = completionValue;

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

        // The timeline semaphore's three native calls with no device behind them. WaitUntil jumps the counter to
        // the value asked for, which is what a GPU that finishes the work does, so a shared row that drives a real
        // stall terminates instead of hanging the suite.
        sealed class Semaphore : IVulkanTimelineSemaphore
        {
            ulong _completed;

            internal ulong Completed
            {
                get => System.Threading.Volatile.Read(ref _completed);
                set => System.Threading.Volatile.Write(ref _completed, value);
            }

            public ulong Read() => Completed;

            public bool WaitUntil(ulong value)
            {
                if (Completed < value) Completed = value;
                return true;
            }

            public void Dispose()
            {
            }
        }
    }
}
