using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DIRECT3D 11 SIDE OF THE SHARED RING TESTS (V-P5), and the half that "share the tests" quietly loses if
    /// nobody writes it. It drives the SHIPPED <see cref="D3D11RingAllocator"/> and
    /// <see cref="D3D11UniformRing"/>, not a copy of them, over a pinned array standing in for the mapped
    /// constant buffer.
    ///
    /// <para><b>WHAT EACH MEMBER MAPS ONTO, HONESTLY.</b> <c>SubmitWork</c> is
    /// <see cref="D3D11RingAllocator.OnSubmitted"/>, which the shipped submit path calls inside the submit lock
    /// right after the end-of-replay signal, so the adapter is doing exactly what a submit does and nothing more.
    /// <c>CompleteWork</c> advances the fake completion timeline, which is the read half of
    /// <see cref="ID3D11CompletionRead"/> the ring gates on and never a submit receipt. <c>BeginFrame</c> is
    /// <see cref="D3D11RingAllocator.BeginFrame"/> verbatim. <c>WriteAtRecordTime</c> is
    /// <see cref="D3D11UniformRing.Write"/>, <c>WriteOffTimeline</c> is
    /// <see cref="D3D11RingAllocator.UpdateBuffer"/>, and <c>ReadSegment</c> reads the pinned array back.</para>
    ///
    /// <para><b>THE ONE MEMBER THAT IS AN APPROXIMATION, NAMED RATHER THAN HIDDEN.</b>
    /// <see cref="StallCount"/> reads <see cref="D3D11RingAllocator.TotalBackpressure"/>, which is cumulative
    /// since the allocator was created, because the shipped per-frame value is ROLLED by <c>BeginFrame</c> and a
    /// shared row that asserted the rolled one would be asserting the roll rather than the stall. The cumulative
    /// half is the same measurement without that confound, and it is the half the other backend has.</para>
    ///
    /// <para><b>THE MAPPING IS DEGENERATE HERE AND THAT IS DELIBERATE.</b> This adapter maps the ring ONCE, at
    /// construction, and leaves it mapped, which is what
    /// <see cref="D3D11RingMapScope.AcrossRecording"/> makes legal outside a submit. The map lifecycle is not part
    /// of the shared policy at all (the other backend has none) and it has its own per-backend tests, so the
    /// adapter keeps it out of the way rather than making it a shared row by accident.</para>
    /// </summary>
    internal sealed class D3D11UniformRingAdapter : IGpuUniformRingUnderTest
    {
        readonly RingMemory _memory;
        readonly D3D11RingAllocator _allocator;
        readonly Completion _completion;
        readonly D3D11UniformRing _ring;

        ulong _highestSubmitted;

        internal D3D11UniformRingAdapter(uint sizeInBytes, int framesInFlight)
        {
            _completion = new Completion();
            _allocator = new D3D11RingAllocator(framesInFlight, _completion, new object());
            _memory = new RingMemory(D3D11UniformRing.TotalBytesFor(sizeInBytes, framesInFlight));
            _ring = new D3D11UniformRing(_allocator, _memory, sizeInBytes);

            // Mapped once and left mapped: see the class note. Under AcrossRecording the ring reuses the mapping
            // for every write, so this is the same state a record phase is in.
            _allocator.EnsureMapped(_ring);
        }

        /// <inheritdoc/>
        public string BackendName => "Direct3D11Native";

        /// <inheritdoc/>
        public int FramesInFlight => _allocator.FramesInFlight;

        /// <inheritdoc/>
        public uint LogicalSizeBytes => _ring.SizeInBytes;

        /// <inheritdoc/>
        public int CurrentSegment => _allocator.CurrentSegment;

        /// <inheritdoc/>
        public int StallCount => (int)_allocator.TotalBackpressure.Count;

        /// <inheritdoc/>
        public int PendingPatchCount => _ring.PendingPatchCount;

        /// <inheritdoc/>
        public ulong SegmentBaseBytes(int segment) => _ring.FrameBaseBytes(segment);

        /// <inheritdoc/>
        public void SubmitWork(ulong completionValue)
        {
            _allocator.OnSubmitted(completionValue);
            if (completionValue > _highestSubmitted) _highestSubmitted = completionValue;
        }

        /// <inheritdoc/>
        public void CompleteWork(ulong completionValue) => _completion.Completed = completionValue;

        /// <inheritdoc/>
        /// <remarks>
        /// THE GPU FINISHING IS WHAT ENDS A STALL, AND THIS IS WHERE THE FAKE MODELS IT. The shipped Direct3D 11
        /// gate SPINS on the completion value, so a fake that never advanced would hang the suite rather than fail
        /// it. The release is armed around this call alone and only fires from the SECOND poll onwards, so the
        /// gate's own first read still sees reality and a genuine stall is still counted. It is deliberately NOT
        /// armed around an off-timeline write, whose single poll under the lock has to answer honestly for the
        /// gating row to mean anything.
        /// </remarks>
        public void BeginFrame()
        {
            _completion.ArmStallRelease(_highestSubmitted);
            try
            {
                _allocator.BeginFrame();
            }
            finally
            {
                _completion.DisarmStallRelease();
            }
        }

        /// <inheritdoc/>
        public void WriteAtRecordTime(uint offsetBytes, ReadOnlySpan<byte> data) => _ring.Write(offsetBytes, data);

        /// <inheritdoc/>
        public void WriteOffTimeline(uint offsetBytes, ReadOnlySpan<byte> data)
            => _allocator.UpdateBuffer(_ring, offsetBytes, data);

        /// <inheritdoc/>
        public byte[] ReadSegment(int segment, uint offsetBytes, int length)
            => _memory.Bytes.AsSpan((int)(_ring.FrameBaseBytes(segment) + offsetBytes), length).ToArray();

        /// <inheritdoc/>
        public void Dispose()
        {
            _allocator.Forget(_ring);
            _memory.Dispose();
        }

        // The ring's two native calls with no device behind them: a pinned array standing in for the mapped
        // constant buffer. Pinning is what lets a test read the bytes back, since the ring writes through a raw
        // pointer and an unpinned array is a collector move away from being somewhere else.
        sealed class RingMemory : ID3D11RingMemory, IDisposable
        {
            GCHandle _pin;

            internal RingMemory(uint totalBytes)
            {
                Bytes = new byte[totalBytes];
                _pin = GCHandle.Alloc(Bytes, GCHandleType.Pinned);
            }

            internal byte[] Bytes { get; }

            public IntPtr MapWriteNoOverwrite() => _pin.AddrOfPinnedObject();

            public void Unmap()
            {
            }

            public void Dispose()
            {
                if (_pin.IsAllocated) _pin.Free();
            }
        }

        // The completion timeline's read half, driven by hand. Never a submit receipt: a ring recycling against one
        // would hand out a segment the moment the CPU finished ASKING for the work.
        sealed class Completion : ID3D11CompletionRead
        {
            ulong _completed;
            ulong _releaseTo;
            bool _armed;
            int _pollsSinceArmed;

            internal ulong Completed
            {
                get => _completed;
                set => _completed = value;
            }

            public ulong CompletedValue
            {
                get
                {
                    if (!_armed) return _completed;

                    _pollsSinceArmed++;

                    // The FIRST poll is the gate's own check and must see reality, or no stall is ever counted.
                    // Every later poll is the spin, and the GPU finishing the work is what ends it.
                    if (_pollsSinceArmed > 1 && _completed < _releaseTo) _completed = _releaseTo;

                    return _completed;
                }
            }

            internal void ArmStallRelease(ulong releaseTo)
            {
                _releaseTo = releaseTo;
                _pollsSinceArmed = 0;
                _armed = true;
            }

            internal void DisarmStallRelease() => _armed = false;
        }
    }
}
