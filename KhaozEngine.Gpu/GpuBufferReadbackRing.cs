using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Fixed staging slots for asynchronous buffer copies, retaining a caller tag and completion fence per
    /// entry. All buffers and metadata arrays are allocated once in the constructor. Warmed recording and
    /// reading allocate nothing. Use the ring from one thread.
    /// <para>
    /// This is a thin layer over <see cref="IGpuDevice.TryReadBuffer"/>. It never waits, flushes, submits,
    /// synchronously maps or takes a blocking lock. On Direct3D 11 a poll completes only after a Present or
    /// drain, because Submit does not flush. The ring adds no headless flush policy.
    /// </para>
    /// <para>
    /// The caller keeps each copy's source and fence alive and submits its list once with that fence. Never
    /// reset, resubmit or dispose a fence until every associated ring has consumed its entries, or discarded
    /// them if the list was never submitted. The ring never resets or disposes a caller fence.
    /// </para>
    /// <para>
    /// The device is borrowed and must stay usable while entries are pending. Every command list, source
    /// buffer and fence passed to the ring must belong to that device. A foreign fence is retained by
    /// <see cref="TryRecord"/> and refused later by the device's read.
    /// </para>
    /// </summary>
    public sealed class GpuBufferReadbackRing : IDisposable
    {
        readonly IGpuDevice _device;
        readonly uint _slotSizeInBytes;
        readonly IGpuBuffer[] _buffers;
        readonly bool[] _occupied;
        readonly Entry[] _pending;
        bool _disposed;

        /// <summary>Allocate exactly <paramref name="slotCount"/> owned staging buffers of the requested size.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="device"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The slot count or slot size is not positive.</exception>
        public GpuBufferReadbackRing(IGpuDevice device, int slotCount, uint slotSizeInBytes)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
            if (slotSizeInBytes == 0) throw new ArgumentOutOfRangeException(nameof(slotSizeInBytes));

            _device = device;
            _slotSizeInBytes = slotSizeInBytes;
            _buffers = new IGpuBuffer[slotCount];
            _occupied = new bool[slotCount];
            _pending = new Entry[slotCount];
            IGpuResourceFactory factory = device.Factory;
            int created = 0;
            try
            {
                for (; created < slotCount; created++)
                    _buffers[created] = factory.CreateBuffer(
                        new GpuBufferDescription(slotSizeInBytes, GpuBufferUsage.Staging));
            }
            catch
            {
                for (int i = 0; i < created; i++) _buffers[i].Dispose();
                throw;
            }
        }

        /// <summary>The fixed number of staging slots.</summary>
        public int Capacity => _buffers.Length;

        /// <summary>Entries awaiting consumption, including recordings not yet submitted.</summary>
        public int PendingCount { get; private set; }

        /// <summary>
        /// Record a copy into offset zero of a free staging slot in the caller's open list. Retains the tag,
        /// byte count and caller-owned completion fence. A full ring returns false without recording anything.
        /// The request is validated before looking for space, and a failed copy reserves no slot.
        /// </summary>
        /// <exception cref="ArgumentNullException">A command list, source or completion fence is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The nonzero copy does not fit a slot or the source, or
        /// the source offset is not a multiple of four, or the copy exceeds <see cref="int.MaxValue"/> bytes
        /// and cannot fit in a destination span.</exception>
        /// <exception cref="ObjectDisposedException">The ring has been disposed.</exception>
        public bool TryRecord(IGpuCommandList commands, IGpuBuffer source, uint sourceOffsetBytes,
            uint sizeInBytes, ulong tag, IGpuFence completion)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(completion);
            if (sizeInBytes == 0 || sizeInBytes > _slotSizeInBytes || sizeInBytes > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
            GpuCopyAlignment.RequireAlignedOffset(sourceOffsetBytes, nameof(sourceOffsetBytes),
                "A buffer readback ring copy", "source");
            uint sourceSize = source.SizeInBytes;
            if (sourceOffsetBytes > sourceSize)
                throw new ArgumentOutOfRangeException(nameof(sourceOffsetBytes));
            if (sizeInBytes > sourceSize - sourceOffsetBytes)
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes));

            if (PendingCount == Capacity) return false;
            int slot = 0;
            while (_occupied[slot]) slot++;
            commands.CopyBuffer(source, sourceOffsetBytes, _buffers[slot], 0, sizeInBytes);
            _pending[PendingCount++] = new Entry(slot, sizeInBytes, tag, completion);
            _occupied[slot] = true;
            return true;
        }

        /// <summary>
        /// Try only the oldest pending entry through <see cref="IGpuDevice.TryReadBuffer"/>. Empty, pending or
        /// busy returns false with zero outputs and the destination untouched. Success releases the slot and
        /// copies only its recorded byte count, leaving any larger destination's tail untouched.
        /// </summary>
        /// <exception cref="ArgumentException">The destination is too small for the oldest entry. The entry
        /// is retained and is not polled.</exception>
        /// <exception cref="ObjectDisposedException">The ring has been disposed.</exception>
        public bool TryRead(Span<byte> destination, out ulong tag, out uint sizeInBytes)
        {
            tag = 0;
            sizeInBytes = 0;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (PendingCount == 0) return false;
            Entry entry = _pending[0];
            if ((uint)destination.Length < entry.SizeInBytes)
                throw new ArgumentException("The destination cannot hold the oldest readback entry.",
                    nameof(destination));
            if (!_device.TryReadBuffer(_buffers[entry.Slot], entry.Completion, 0,
                destination.Slice(0, (int)entry.SizeInBytes))) return false;

            tag = entry.Tag;
            sizeInBytes = entry.SizeInBytes;
            _occupied[entry.Slot] = false;
            PendingCount--;
            Array.Copy(_pending, 1, _pending, 0, PendingCount);
            _pending[PendingCount] = default;
            return true;
        }

        /// <summary>
        /// Stably remove only entries retaining this exact fence, releasing their slots without touching any
        /// fence. Only for an abandoned list that was never submitted. Using a submitted fence is a caller
        /// error, even while it is unsignalled. Abandon the list before reusing its slots.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="completion"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The ring has been disposed.</exception>
        public void DiscardUnsubmitted(IGpuFence completion)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(completion);
            int retained = 0;
            for (int i = 0; i < PendingCount; i++)
            {
                Entry entry = _pending[i];
                if (ReferenceEquals(entry.Completion, completion)) _occupied[entry.Slot] = false;
                else _pending[retained++] = entry;
            }
            Array.Clear(_pending, retained, PendingCount - retained);
            PendingCount = retained;
        }

        /// <summary>Dispose only owned staging buffers, after all entries have been consumed or validly discarded.</summary>
        /// <exception cref="InvalidOperationException">Entries remain pending, even if their fences have signalled.</exception>
        public void Dispose()
        {
            if (_disposed) return;
            if (PendingCount != 0)
                throw new InvalidOperationException("Consume or discard every pending readback before disposing the ring.");
            _disposed = true;
            foreach (IGpuBuffer buffer in _buffers) buffer.Dispose();
        }

        readonly struct Entry
        {
            internal readonly int Slot;
            internal readonly uint SizeInBytes;
            internal readonly ulong Tag;
            internal readonly IGpuFence Completion;

            internal Entry(int slot, uint sizeInBytes, ulong tag, IGpuFence completion)
            {
                Slot = slot;
                SizeInBytes = sizeInBytes;
                Tag = tag;
                Completion = completion;
            }
        }
    }
}
