using System;

namespace KhaozEngine.Imaging
{
    /// <summary>
    /// The growable output of a JPEG encode. Marker segments go in as whole bytes, entropy-coded data goes in as
    /// most-significant-bit-first codes with every 0xFF data byte stuffed as 0xFF 0x00, and <see cref="FlushBits"/>
    /// pads the last partial byte with 1 bits.
    /// </summary>
    internal sealed class JpegBitWriter
    {
        byte[] _buffer;
        int _length;
        ulong _bits;
        int _bitCount;

        internal JpegBitWriter(int initialCapacity) => _buffer = new byte[Math.Max(initialCapacity, 256)];

        internal void WriteByte(byte value)
        {
            if (_length == _buffer.Length) Grow(1);
            _buffer[_length++] = value;
        }

        internal void WriteBytes(ReadOnlySpan<byte> values)
        {
            if (_length + values.Length > _buffer.Length) Grow(values.Length);
            values.CopyTo(_buffer.AsSpan(_length));
            _length += values.Length;
        }

        internal void WriteUInt16(int value)
        {
            WriteByte((byte)(value >> 8));
            WriteByte((byte)value);
        }

        /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="value"/>, at most 24.</summary>
        internal void WriteBits(int value, int count)
        {
            _bits = (_bits << count) | ((uint)value & ((1u << count) - 1));
            _bitCount += count;
            while (_bitCount >= 8)
            {
                _bitCount -= 8;
                byte b = (byte)(_bits >> _bitCount);
                WriteByte(b);
                if (b == 0xFF) WriteByte(0x00);
            }
        }

        /// <summary>Completes the entropy-coded segment, padding its final byte with 1 bits.</summary>
        internal void FlushBits()
        {
            if (_bitCount > 0) WriteBits(0x7F, 8 - _bitCount);
            _bits = 0;
        }

        internal byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        void Grow(int needed) => Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + needed));
    }
}
