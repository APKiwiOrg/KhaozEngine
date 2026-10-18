using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The bounded KEFOLPK1 binary envelope. Logical digests cover density only, never keys or pack addresses.</summary>
internal static class MapFoliageRegionPack
{
    internal const int HeaderLength = 12;
    internal const int RecordLength = 1064;
    internal const int MaxByteLength = 17_432_588;
    internal static int DataOffset(int index) => checked(52 + index * RecordLength);
    internal static int ByteLength(int count)
    {
        if (count is < 1 or > MapFoliagePayloadLimits.MaxChunksPerLayer)
            throw new MapDocumentException("foliage pack chunk count exceeds capacity or is empty.");
        return checked(HeaderLength + count * RecordLength);
    }
    internal static byte[] Encode(IReadOnlyList<MapFoliageChunk> chunks)
    {
        int length = ByteLength(chunks.Count);
        MapFoliageChunk[] sorted = chunks.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X).ToArray();
        var bytes = new byte[length];
        "KEFOLPK1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            MapFoliageChunk chunk = sorted[i];
            if (chunk.Key.X is < 0 or > 32767 || chunk.Key.Z is < 0 or > 32767 || (i > 0 && sorted[i - 1].Key == chunk.Key))
                throw new MapDocumentException("foliage pack has invalid or duplicate chunk keys.");
            Span<byte> record = bytes.AsSpan(HeaderLength + i * RecordLength, RecordLength);
            BinaryPrimitives.WriteInt32LittleEndian(record, chunk.Key.X);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], chunk.Key.Z);
            SHA256.HashData(chunk.Density, record.Slice(8, 32));
            chunk.Density.CopyTo(record[40..]);
        }
        return bytes;
    }
    internal static IReadOnlyList<MapFoliageChunk> Decode(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        if (span.Length < HeaderLength || span.Length > MaxByteLength || !span[..8].SequenceEqual("KEFOLPK1"u8))
            throw new MapDocumentException("foliage pack has invalid magic or byte length.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        if (ByteLength(count) != span.Length) throw new MapDocumentException("foliage pack byte length does not match its count.");
        var chunks = new MapFoliageChunk[count];
        MapFoliageChunkKey prior = new(-1, -1);
        Span<byte> digest = stackalloc byte[32];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = span.Slice(HeaderLength + i * RecordLength, RecordLength);
            var key = new MapFoliageChunkKey(BinaryPrimitives.ReadInt32LittleEndian(record), BinaryPrimitives.ReadInt32LittleEndian(record[4..]));
            if (key.X is < 0 or > 32767 || key.Z is < 0 or > 32767 ||
                (i > 0 && (key.Z < prior.Z || (key.Z == prior.Z && key.X <= prior.X))))
                throw new MapDocumentException("foliage pack keys are not unique and ordered by Z then X.");
            SHA256.HashData(record[40..], digest);
            if (!digest.SequenceEqual(record.Slice(8, 32))) throw new MapDocumentException("foliage pack raw chunk digest mismatch.");
            chunks[i] = new(key, record[40..]); prior = key;
        }
        return Array.AsReadOnly(chunks);
    }
    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void RequireDigest(string digest)
    {
        if (digest is not { Length: 64 } || digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new MapDocumentException("foliage: invalid SHA-256 name.");
    }
}
