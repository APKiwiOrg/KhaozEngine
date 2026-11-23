using System;
using System.IO;

namespace KhaozEngine.Attachments;

/// <summary>The canonical little-endian KHLF lift footprint format.</summary>
public static class LiftFootprintFile
{
    /// <summary>The supported lift footprint format version.</summary>
    public const int FormatVersion = 1;

    internal static ReadOnlySpan<byte> Magic => "KHLF"u8;

    /// <summary>Write the footprint's canonical bytes.</summary>
    public static byte[] Write(LiftFootprint footprint)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write((ushort)FormatVersion);
        writer.Write((byte)footprint.Level);
        writer.Write((byte)0);
        writer.Write(Convert.FromHexString(footprint.BodyRegionMapSha256));
        writer.Write((uint)footprint.BodyTriangleCount);
        writer.Write((uint)footprint.Parts.Count);

        foreach (LiftFootprintPart part in footprint.Parts)
        {
            writer.Write(part.PartId);
            writer.Write((uint)part.TriangleCount);
            foreach (LiftFootprintTriangle triangle in part.Triangles)
            {
                ReadOnlySpan<int> keys = triangle.CellKeys;
                uint header = ((uint)keys.Length << 1) | (triangle.Swinging ? 1u : 0u);
                WriteVarint(writer, header);
                int previous = 0;
                foreach (int key in keys)
                {
                    WriteVarint(writer, (uint)(key - previous));
                    previous = key;
                }
                foreach (ushort height in triangle.HeightUnits)
                    writer.Write(height);
            }
        }

        writer.Write((uint)footprint.BodyNormals.Count);
        foreach (LiftFootprintBodyNormal entry in footprint.BodyNormals)
        {
            writer.Write((uint)entry.BodyTriangleIndex);
            writer.Write(entry.Normal.X);
            writer.Write(entry.Normal.Y);
            writer.Write(entry.Normal.Z);
        }

        return stream.ToArray();
    }

    /// <summary>Read a canonical footprint and attach the supplied source name to any data refusal.</summary>
    /// <exception cref="LiftFootprintDataException">The bytes are invalid or noncanonical.</exception>
    public static LiftFootprint Parse(ReadOnlyMemory<byte> bytes, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            return new LiftFootprintFileReader(bytes.Span).Read();
        }
        catch (LiftFootprintDataException error)
        {
            throw ((IAttachmentDataException)error).WithSource(sourceName);
        }
    }

    static void WriteVarint(BinaryWriter writer, uint value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }
}
