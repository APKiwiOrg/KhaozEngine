using System;
using System.Buffers;
using System.Buffers.Binary;

namespace KhaozEngine.Movement;

/// <summary>Little-endian reader over bake bytes. Every read returns false and leaves the position unchanged when
/// too few bytes remain, so a truncated input is a status and never an exception.</summary>
internal ref struct NavBakeReader
{
    private readonly ReadOnlySpan<byte> _data;

    public NavBakeReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public int Position { get; private set; }

    public readonly int Remaining => _data.Length - Position;

    public bool TryReadUInt8(out byte value)
    {
        value = 0;
        if (Remaining < 1) return false;
        value = _data[Position];
        Position++;
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        value = 0;
        if (Remaining < 2) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Position, 2));
        Position += 2;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(Position, 4));
        Position += 4;
        return true;
    }

    public bool TryReadInt32(out int value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(Position, 4));
        Position += 4;
        return true;
    }

    /// <summary>Reads a float from its IEEE 754 bit pattern.</summary>
    public bool TryReadSingle(out float value)
    {
        bool read = TryReadUInt32(out uint bits);
        value = BitConverter.UInt32BitsToSingle(bits);
        return read;
    }

    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (count < 0 || Remaining < count) return false;
        value = _data.Slice(Position, count);
        Position += count;
        return true;
    }

    /// <summary>Reads a <c>uint8</c> length and that many bytes, the short ASCII string layout. Content is not
    /// validated here.</summary>
    public bool TryReadShortString(out ReadOnlySpan<byte> value)
    {
        value = default;
        int start = Position;
        if (!TryReadUInt8(out byte length)) return false;
        if (TryReadBytes(length, out value)) return true;
        Position = start;
        return false;
    }

    /// <summary>Reads a <c>uint16</c> length and that many bytes. Content is not validated here.</summary>
    public bool TryReadLongString(out ReadOnlySpan<byte> value)
    {
        value = default;
        int start = Position;
        if (!TryReadUInt16(out ushort length)) return false;
        if (TryReadBytes(length, out value)) return true;
        Position = start;
        return false;
    }
}

/// <summary>Little-endian writer for bake bytes over an <see cref="ArrayBufferWriter{T}"/>.</summary>
internal sealed class NavBakeWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public void WriteUInt8(byte value)
    {
        _buffer.GetSpan(1)[0] = value;
        _buffer.Advance(1);
    }

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.GetSpan(2), value);
        _buffer.Advance(2);
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
    }

    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
    }

    /// <summary>Writes a float as its IEEE 754 bit pattern.</summary>
    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));

    public void WriteBytes(ReadOnlySpan<byte> value) => _buffer.Write(value);

    /// <summary>Writes a <c>uint8</c> length and the bytes. The caller has validated the length.</summary>
    public void WriteShortString(ReadOnlySpan<byte> value)
    {
        if (value.Length > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        WriteUInt8((byte)value.Length);
        WriteBytes(value);
    }

    /// <summary>Writes a <c>uint16</c> length and the bytes. The caller has validated the length.</summary>
    public void WriteLongString(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        WriteUInt16((ushort)value.Length);
        WriteBytes(value);
    }
}
