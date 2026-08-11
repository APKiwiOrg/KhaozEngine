using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ONE RING, ITS ALLOCATOR, ITS TIMELINE AND THE SUBMIT LOCK THEY SHARE, wired exactly as
    /// <c>VulkanGpuDevice</c> wires them. Shared rather than nested in one test class, because the ring's own
    /// arithmetic and its semantics are two files and both need the same four.
    /// <para>
    /// THE MEMORY IS A PINNED ARRAY, which is all a Vulkan ring's memory is from this side: row 6 maps a
    /// host-visible chunk once at creation and never unmaps it (V-M3), so the ring holds a pointer and there is no
    /// map lifecycle to fake. The Direct3D 11 harness beside this one needs an <c>ID3D11RingMemory</c> for exactly
    /// the calls this backend does not make.
    /// </para>
    /// </summary>
    internal sealed class VulkanRingHarness : IDisposable
    {
        readonly byte[] _bytes;
        GCHandle _pin;

        internal VulkanRingHarness(ulong sizeInBytes, int framesInFlight)
        {
            SubmitLock = new object();
            Semaphore = new FakeVulkanTimelineSemaphore();
            Liveness = new DeviceLiveness();
            Timeline = new VulkanTimeline(Semaphore, Liveness);
            Backpressure = new WaitAccumulator();
            Allocator = new VulkanRingAllocator(framesInFlight, Timeline, Backpressure, SubmitLock);

            _bytes = new byte[(int)VulkanRingStride.TotalBytesFor(sizeInBytes, framesInFlight, 0)];
            _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);

            Ring = new VulkanUniformRing(Allocator, _pin.AddrOfPinnedObject(), sizeInBytes);
        }

        /// <summary>The device's ONE submit lock, which the off-timeline write takes and which
        /// <c>BeginFrame</c> refuses a caller for.</summary>
        internal object SubmitLock { get; }

        /// <summary>The timeline semaphore's three native calls, driven by hand.</summary>
        internal FakeVulkanTimelineSemaphore Semaphore { get; }

        /// <summary>The device's liveness token, so a test can kill the device under a wait.</summary>
        internal DeviceLiveness Liveness { get; }

        /// <summary>The device's one completion timeline, which the segment gate reads.</summary>
        internal VulkanTimeline Timeline { get; }

        /// <summary>The device's ONE backpressure accumulator, shared with the command list's slot wait.</summary>
        internal WaitAccumulator Backpressure { get; }

        /// <summary>The device's ring allocator.</summary>
        internal VulkanRingAllocator Allocator { get; }

        /// <summary>The ring under test.</summary>
        internal VulkanUniformRing Ring { get; }

        /// <summary>The whole allocation, segment by segment, exactly as the ring wrote it.</summary>
        internal byte[] Bytes => _bytes;

        /// <summary>One accepted submission signalling <paramref name="value"/>, exactly as
        /// <c>VulkanSubmitQueue</c> makes one: values allocated one at a time inside the lock, and registered only
        /// after the submit succeeded.</summary>
        internal void Submit(ulong value)
        {
            lock (SubmitLock)
            {
                while (Timeline.LastAllocated < value) Timeline.NextSubmitValue();
                Timeline.RegisterSubmitted(value);
            }
        }

        /// <summary>The GPU reaching <paramref name="value"/>.</summary>
        internal void Complete(ulong value) => Semaphore.Completed = value;

        public void Dispose()
        {
            Timeline.Dispose();
            if (_pin.IsAllocated) _pin.Free();
        }
    }

    /// <summary>
    /// A buffer that answers the write path's routing question the way a real ring-backed uniform buffer will.
    /// Buffers are <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/519">row 9</see>'s, so this is what
    /// lets row 8's ROUTING (a uniform write goes to the ring, everything else goes to the arena) be exercised
    /// before that row exists.
    /// </summary>
    internal sealed class FakeVulkanRingBackedBuffer : IGpuBuffer, IVulkanRingBacked
    {
        internal FakeVulkanRingBackedBuffer(VulkanUniformRing ring)
        {
            Ring = ring;
            SizeInBytes = (uint)ring.SizeInBytes;
        }

        /// <inheritdoc/>
        public uint SizeInBytes { get; }

        /// <inheritdoc/>
        public VulkanUniformRing? Ring { get; }

        public void Dispose()
        {
        }
    }

    /// <summary>A buffer that is NOT ring-backed, which is what a vertex, index, indirect or storage buffer will
    /// be. It answers the arena's question instead of the ring's.</summary>
    internal sealed class FakeVulkanUploadBuffer : IGpuBuffer, IVulkanRingBacked, IVulkanUploadDestination
    {
        internal FakeVulkanUploadBuffer(ulong deviceBuffer, uint sizeInBytes, GpuBufferUsage usage)
        {
            DeviceBuffer = deviceBuffer;
            SizeInBytes = sizeInBytes;
            UploadUsage = usage;
        }

        /// <inheritdoc/>
        public uint SizeInBytes { get; }

        /// <inheritdoc/>
        public VulkanUniformRing? Ring => null;

        /// <inheritdoc/>
        public ulong DeviceBuffer { get; }

        /// <inheritdoc/>
        public GpuBufferUsage UploadUsage { get; }

        public void Dispose()
        {
        }
    }
}
