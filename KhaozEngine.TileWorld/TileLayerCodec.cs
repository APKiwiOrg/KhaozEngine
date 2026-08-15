using System;
using System.Buffers.Binary;

namespace KhaozEngine.TileWorld;

/// <summary>Base64 of little-endian element bytes, the on-disk form of every dense layer.</summary>
internal static class TileLayerCodec
{
    /// <summary>Encodes a height layer.</summary>
    public static string Encode(ReadOnlySpan<short> values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Encodes a material-id layer.</summary>
    public static string Encode(ReadOnlySpan<ushort> values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Encodes a byte layer (shape, rotation, settings).</summary>
    public static string Encode(ReadOnlySpan<byte> values) => Convert.ToBase64String(values);

    /// <summary>Decodes a height layer, naming <paramref name="what"/> on a bad length or bad base64.</summary>
    public static short[] DecodeShorts(string b64, int expectedCount, string what)
    {
        byte[] bytes = Bytes(b64, expectedCount * 2, what);
        var v = new short[expectedCount];
        for (int i = 0; i < expectedCount; i++) v[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2));
        return v;
    }

    /// <summary>Decodes a material-id layer, naming <paramref name="what"/> on a bad length or bad base64.</summary>
    public static ushort[] DecodeUShorts(string b64, int expectedCount, string what)
    {
        byte[] bytes = Bytes(b64, expectedCount * 2, what);
        var v = new ushort[expectedCount];
        for (int i = 0; i < expectedCount; i++) v[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return v;
    }

    /// <summary>Decodes a byte layer, naming <paramref name="what"/> on a bad length or bad base64.</summary>
    public static byte[] DecodeBytes(string b64, int expectedCount, string what) => Bytes(b64, expectedCount, what);

    static byte[] Bytes(string b64, int expectedBytes, string what)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException ex) { throw new TileWorldException($"{what}: layer is not valid base64", ex); }
        if (bytes.Length != expectedBytes)
            throw new TileWorldException($"{what}: layer has {bytes.Length} bytes, expected {expectedBytes}");
        return bytes;
    }
}
