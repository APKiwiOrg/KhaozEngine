using System;
using System.IO;
using System.Text;

namespace KhaozEngine.Movement;

/// <summary>Bounded format-4 primitives. Counts are checked against both source caps and bytes before allocation.</summary>
internal ref struct TerrainEvidenceReader(ReadOnlySpan<byte> data)
{
    NavBakeReader _reader = new(data);
    internal int Remaining => _reader.Remaining;
    internal ReadOnlySpan<byte> Bytes(int count) => _reader.TryReadBytes(count, out var bytes) ? bytes : throw Corrupt();
    internal byte Byte() => _reader.TryReadUInt8(out var value) ? value : throw Corrupt();
    internal bool Boolean() => Byte() switch { 0 => false, 1 => true, _ => throw Corrupt() };
    internal uint Unsigned()
    {
        uint value = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte b = Byte();
            if (shift == 28 && b > 15) throw Corrupt();
            value |= (uint)(b & 127) << shift;
            if (b < 128)
            {
                if (shift != 0 && b == 0) throw Corrupt();
                return value;
            }
        }
        throw Corrupt();
    }
    internal int Count(long cap, int minimumBytes)
    {
        uint value = Unsigned();
        if (value > Math.Min(cap, Array.MaxLength) || value > Remaining / minimumBytes) throw Corrupt();
        return (int)value;
    }
    internal int Index(int length)
    { uint value = Unsigned(); return value < length ? (int)value : throw Corrupt(); }
    internal long Address() => _reader.TryReadUInt64(out var value) ? unchecked((long)value) : throw Corrupt();
    internal float Single() => _reader.TryReadSingle(out var value) && float.IsFinite(value) ? value : throw Corrupt();
    internal double Double()
    {
        if (!_reader.TryReadUInt64(out var bits)) throw Corrupt();
        double value = BitConverter.UInt64BitsToDouble(bits);
        return double.IsFinite(value) ? value : throw Corrupt();
    }
    internal string Text()
    {
        int count = Count(Array.MaxLength, 1);
        var bytes = Bytes(count);
        if (bytes.Length == 0 || !System.Text.Unicode.Utf8.IsValid(bytes)) throw Corrupt();
        return Encoding.UTF8.GetString(bytes);
    }
    internal static InvalidDataException Corrupt() => new("Invalid terrain evidence payload.");
}

internal static class TerrainEvidenceBinary
{
    internal static void Unsigned(NavBakeWriter writer, uint value)
    {
        while (value >= 128) { writer.WriteUInt8((byte)(value | 128)); value >>= 7; }
        writer.WriteUInt8((byte)value);
    }
    internal static void Text(NavBakeWriter writer, string value)
    {
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
        if (bytes.Length == 0) throw new ArgumentException("A terrain source name is empty.", nameof(value));
        Unsigned(writer, (uint)bytes.Length);
        writer.WriteBytes(bytes);
    }
    internal static bool SingleExact(double value) => BitConverter.DoubleToUInt64Bits(value) == BitConverter.DoubleToUInt64Bits((double)(float)value);
}
