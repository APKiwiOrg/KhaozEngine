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

    /// <summary>Decodes a height layer, carrying its layer, source and plane on a refusal.</summary>
    public static short[] DecodeShorts(string b64, int expectedCount, string layerName, string? sourceName = null, int? plane = null)
    {
        byte[] bytes = Bytes(b64, expectedCount * 2, layerName, sourceName, plane);
        var v = new short[expectedCount];
        for (int i = 0; i < expectedCount; i++) v[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2));
        return v;
    }

    /// <summary>Decodes a material-id layer, carrying its layer, source and plane on a refusal.</summary>
    public static ushort[] DecodeUShorts(string b64, int expectedCount, string layerName, string? sourceName = null, int? plane = null)
    {
        byte[] bytes = Bytes(b64, expectedCount * 2, layerName, sourceName, plane);
        var v = new ushort[expectedCount];
        for (int i = 0; i < expectedCount; i++) v[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return v;
    }

    /// <summary>Decodes a byte layer, carrying its layer, source and plane on a refusal.</summary>
    public static byte[] DecodeBytes(string b64, int expectedCount, string layerName, string? sourceName = null, int? plane = null) =>
        Bytes(b64, expectedCount, layerName, sourceName, plane);

    static byte[] Bytes(string b64, int expectedBytes, string layerName, string? sourceName, int? plane)
    {
        string what = sourceName is null ? layerName : $"{sourceName} plane {plane} {layerName}";
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException ex)
        {
            throw new TileWorldException(TileWorldRefusal.LayerBase64Malformed, $"{what}: layer is not valid base64", ex)
            { SourceName = sourceName, Plane = plane, LayerName = layerName };
        }
        if (bytes.Length != expectedBytes)
            throw new TileWorldException(TileWorldRefusal.LayerByteCountMismatch, $"{what}: layer has {bytes.Length} bytes, expected {expectedBytes}")
            { SourceName = sourceName, Plane = plane, LayerName = layerName, ActualByteCount = bytes.Length, ExpectedByteCount = expectedBytes };
        return bytes;
    }
}
