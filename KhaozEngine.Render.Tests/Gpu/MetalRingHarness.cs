using System;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A WHOLE NATIVE METAL RING SUBSYSTEM WITH NO DEVICE UNDER IT: a fake shared event, a real
    /// <see cref="MetalTimeline"/> over it, a real <see cref="MetalRingAllocator"/>, real
    /// <see cref="MetalUniformRing"/>s over pinned arrays, a real <see cref="MetalStagingArena"/> over a fake
    /// source, and a real <see cref="MetalCommandList"/> holding all of it.
    ///
    /// <para><b>WHY THE WHOLE SUBSYSTEM RATHER THAN ONE PIECE.</b> The things row 8 can get wrong are relations
    /// between pieces: which segment a write lands in depends on the allocator's index, whether an acquire blocks
    /// depends on a value the submit path registered, and whether a uniform write opens an encoder depends on a
    /// fork three types away from the ring. Assembling the real types over fakes at the two native seams
    /// (<see cref="IMetalSharedEvent"/> and <see cref="IMetalStagingSource"/>) is what makes those relations
    /// assertable on the Linux and Windows legs rather than only on the one leg with a Metal device.</para>
    ///
    /// <para><b>THE PINNED ARRAY IS THE <c>contents()</c> POINTER.</b> A Shared <c>MTLBuffer</c>'s pointer is
    /// stable for its life and is plain memory both sides address, so a pinned managed array is a faithful stand
    /// in for everything above the allocation itself. What no fake here can prove is that the GPU sees those
    /// bytes, which is <c>MetalRingGpuTests</c>.</para>
    /// </summary>
    internal sealed class MetalRingHarness : IDisposable
    {
        readonly System.Collections.Generic.List<GCHandle> _pins = new();

        internal MetalRingHarness(int framesInFlight = MetalFramesInFlight.Default)
        {
            FramesInFlight = framesInFlight;
            Event = new FakeMetalSharedEvent();
            Liveness = new FakeMetalDeviceLiveness();
            Timeline = new MetalTimeline(Event, Liveness);
            Backpressure = new MetalBackpressure();
            SubmitLock = new object();
            Rings = new MetalRingAllocator(framesInFlight, Timeline, Backpressure, SubmitLock);
            Staging = new FakeMetalStagingSource();
            Blit = new FakeMetalBlitApi();
        }

        /// <summary>The depth every ring and every arena in this harness is cut to.</summary>
        internal int FramesInFlight { get; }

        /// <summary>The counter the gate polls. Set <see cref="FakeMetalSharedEvent.Completed"/> to model the GPU
        /// finishing work.</summary>
        internal FakeMetalSharedEvent Event { get; }

        /// <summary>The device's liveness token, which is also the identity every buffer would carry.</summary>
        internal FakeMetalDeviceLiveness Liveness { get; }

        /// <summary>The real timeline, with only its three native calls faked.</summary>
        internal MetalTimeline Timeline { get; }

        /// <summary>MM4's accumulator, whose only source is the ring's segment gate.</summary>
        internal MetalBackpressure Backpressure { get; }

        /// <summary>The device's submit lock, which the allocator shares rather than owns.</summary>
        internal object SubmitLock { get; }

        /// <summary>The real allocator.</summary>
        internal MetalRingAllocator Rings { get; }

        /// <summary>The arena's two native calls, as pinned arrays and numbers.</summary>
        internal FakeMetalStagingSource Staging { get; }

        /// <summary>The one copy a bulk upload emits, as a log.</summary>
        internal FakeMetalBlitApi Blit { get; }

        /// <summary>
        /// A real ring over a pinned array of exactly the allocation a ring-backed buffer of
        /// <paramref name="sizeInBytes"/> would take. The array is returned so a test can read any segment
        /// directly, which is the only way a policy about WHERE bytes land is observable.
        /// </summary>
        internal MetalUniformRing NewRing(uint sizeInBytes, out byte[] backing)
        {
            backing = new byte[(int)MetalRingStride.TotalBytesFor(sizeInBytes, FramesInFlight)];

            GCHandle pin = GCHandle.Alloc(backing, GCHandleType.Pinned);
            _pins.Add(pin);

            return new MetalUniformRing(Rings, pin.AddrOfPinnedObject(), sizeInBytes);
        }

        /// <summary>A real arena on this harness's fake source, cut to the same depth.</summary>
        internal MetalStagingArena NewArena(ulong blockBytes = MetalStagingArena.DefaultBlockBytes,
            ulong retentionBytes = MetalStagingArena.DefaultRetentionBytes)
            => new(Staging, FramesInFlight, blockBytes, retentionBytes);

        /// <summary>
        /// A real command list wired to this harness. <paramref name="owner"/> is the opaque token the submit
        /// path compares by reference, exactly as the device passes <c>this</c>.
        /// </summary>
        internal MetalCommandList NewList(object owner, FakeMetalCommandBufferSource? buffers = null,
            FakeMetalEncoderCalls? calls = null, MetalUncommittedBuffers? uncommitted = null,
            MetalStagingArena? arena = null)
            => new(buffers ?? new FakeMetalCommandBufferSource(),
                uncommitted ?? new MetalUncommittedBuffers(FramesInFlight, new RecordingLogger()),
                new FakeMetalEncoderSink(calls ?? new FakeMetalEncoderCalls()),
                owner, Rings, arena ?? NewArena(), Blit, Liveness);

        /// <summary>
        /// SUBMIT A SEALED RECORDING, which is <c>MetalGpuDevice.SubmitOnMacOs</c>'s lock body with the one native
        /// call taken out: allocate and encode a value, register it as accepted, and hand it to the list, which is
        /// what tells the arena which value its blocks wait for and the ring which submission read its segment.
        /// Returns the value, because the segment owner a test asserts on is exactly that number.
        /// <para>
        /// THE ORDER AND THE LOCK ARE THE POINT. A test that called <c>MarkSubmitted</c> outside the lock would
        /// be asserting about a shape the device does not have, and the ring's owner is registered under this lock
        /// precisely so a concurrent <c>Begin</c> cannot rotate onto a segment before its owner exists.
        /// </para>
        /// </summary>
        internal ulong Submit(MetalCommandList list)
        {
            lock (SubmitLock)
            {
                ulong value = Timeline.EncodeSignalForSubmit(IntPtr.Zero);
                Timeline.RegisterSubmitted(value);
                list.MarkSubmitted(value);
                return value;
            }
        }

        public void Dispose()
        {
            Timeline.Dispose();
            Staging.Dispose();

            foreach (GCHandle pin in _pins)
            {
                if (pin.IsAllocated) pin.Free();
            }

            _pins.Clear();
        }
    }
}
