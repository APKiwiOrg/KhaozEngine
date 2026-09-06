using System;

namespace KhaozEngine.Imaging;

internal static class PngCrc
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        uint crc = 0xffffffff;
        foreach (byte value in first) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        foreach (byte value in second) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc ^ 0xffffffff;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint value = n;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[n] = value;
        }
        return table;
    }
}
