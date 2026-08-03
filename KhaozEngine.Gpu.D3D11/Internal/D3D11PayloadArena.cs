using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The per-list reusable CPU arena decision U4 calls for: record-time bulk payloads are copied in here and
    /// the op carries an offset and a length instead of a pointer. One array per command list, grown to the
    /// frame's high-water mark and then rewound rather than reallocated, so a steady frame allocates nothing.
    /// <para>
    /// Only bulk payloads land here. Section 5.1 is explicit that uniform writes do NOT: they go straight into
    /// the mapped ring (U1 and U2), so the memcpy the renderer already performs IS the memcpy into GPU-visible
    /// memory and there is no second copy. That routing decision belongs to the ring in work-breakdown row 8. All
    /// this row does is give every <c>UpdateBuffer</c> a byte home that outlives the caller's span, which it must
    /// have, because a recorded span is dangling by the time the list is submitted.
    /// </para>
    /// <para>
    /// <see cref="Reset"/> rewinds without clearing, and that is safe here in a way it is not for
    /// <see cref="D3D11ReferenceList"/>: bytes are not references, so stale content keeps nothing alive. It is
    /// unreadable too, since a slice is only ever taken at an offset and length an op recorded.
    /// </para>
    /// </summary>
    internal sealed class D3D11PayloadArena
    {
        byte[] _bytes;
        int _length;

        internal D3D11PayloadArena(int capacity = 4096) => _bytes = new byte[capacity < 1 ? 1 : capacity];

        /// <summary>Bytes written by the current recording.</summary>
        internal int Length => _length;

        /// <summary>The arena's current allocation, which is the high-water mark it has grown to.</summary>
        internal int Capacity => _bytes.Length;

        /// <summary>Copy <paramref name="data"/> in and return the offset an op should carry.</summary>
        internal int Append(ReadOnlySpan<byte> data)
        {
            int offset = _length;
            int needed = offset + data.Length;
            if (needed > _bytes.Length)
            {
                int grown = _bytes.Length;
                while (grown < needed) grown *= 2;
                // Resize preserves what is already written, which is required: ops recorded earlier in this
                // frame carry offsets into the same array.
                Array.Resize(ref _bytes, grown);
            }

            data.CopyTo(_bytes.AsSpan(offset));
            _length = needed;
            return offset;
        }

        /// <summary>The bytes an op recorded. Valid until the next <see cref="Reset"/>, which is the recording's
        /// lifetime and exactly as long as the op that names them.</summary>
        internal ReadOnlySpan<byte> Slice(int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > _length)
                throw new InvalidOperationException(
                    $"Command-stream payload [{offset}, {offset + length}) is outside the {_length} bytes this "
                    + "recording holds. The op encoder and the replay switch disagree about this command.");

            return _bytes.AsSpan(offset, length);
        }

        /// <summary>Rewind for a new recording. Keeps the allocation.</summary>
        internal void Reset() => _length = 0;
    }
}
