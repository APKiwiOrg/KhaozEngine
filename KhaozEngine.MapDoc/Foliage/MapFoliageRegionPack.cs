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
            throw new MapDocumentException(count < 1 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.WorkCapacityExceeded,
                "foliage pack chunk count exceeds capacity or is empty.")
            {
                FieldPath = "Count", ActualCount = count, ActualInteger = count, MinimumInteger = 1,
                MaximumInteger = MapFoliagePayloadLimits.MaxChunksPerLayer,
                Budget = count < 1 ? null : "foliage pack chunks", Limit = count < 1 ? null : MapFoliagePayloadLimits.MaxChunksPerLayer,
            };
        return checked(HeaderLength + count * RecordLength);
    }
    internal static byte[] Encode(IReadOnlyList<MapFoliageChunk> chunks)
    {
        if (MapFoliageStorageWork.Current is { } work) work.PackEncodes++;
        int length = ByteLength(chunks.Count);
        MapFoliageChunk[] sorted = chunks.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X).ToArray();
        var bytes = new byte[length];
        "KEFOLPK1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            MapFoliageChunk chunk = sorted[i];
            if (chunk.Key.X is < 0 or > 32767 || chunk.Key.Z is < 0 or > 32767 || (i > 0 && sorted[i - 1].Key == chunk.Key))
                throw chunk.Key.X is < 0 or > 32767 || chunk.Key.Z is < 0 or > 32767
                    ? InvalidKey(chunk.Key, i, "foliage pack has invalid or duplicate chunk keys.")
                    : new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, "foliage pack has invalid or duplicate chunk keys.")
                    { FoliageChunk = chunk.Key, Index = i, SubjectKind = "foliage pack chunk" };
            Span<byte> record = bytes.AsSpan(HeaderLength + i * RecordLength, RecordLength);
            BinaryPrimitives.WriteInt32LittleEndian(record, chunk.Key.X);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], chunk.Key.Z);
            Convert.FromHexString(chunk.Sha256).CopyTo(record.Slice(8, 32));
            chunk.Density.CopyTo(record[40..]);
        }
        return bytes;
    }
    internal static IReadOnlyList<MapFoliageChunk> Decode(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        if (span.Length < HeaderLength || span.Length > MaxByteLength || !span[..8].SequenceEqual("KEFOLPK1"u8))
            throw new MapDocumentException(span.Length < HeaderLength ? MapDocumentRefusal.SchemaArrayLengthMismatch :
                span.Length > MaxByteLength ? MapDocumentRefusal.WorkCapacityExceeded : MapDocumentRefusal.SchemaMalformed,
                "foliage pack has invalid magic or byte length.")
            {
                FieldPath = span.Length < HeaderLength || span.Length > MaxByteLength ? "ByteLength" : "Magic",
                ActualByteCount = span.Length, MinimumInteger = HeaderLength, MaximumInteger = MaxByteLength,
                Budget = span.Length > MaxByteLength ? "foliage pack bytes" : null,
                Limit = span.Length > MaxByteLength ? MaxByteLength : null,
            };
        int count = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        int expectedLength = ByteLength(count);
        if (expectedLength != span.Length) throw new MapDocumentException(MapDocumentRefusal.SchemaArrayLengthMismatch,
            "foliage pack byte length does not match its count.")
        { FieldPath = "ByteLength", ActualByteCount = span.Length, ExpectedByteCount = expectedLength, ActualCount = count };
        var chunks = new MapFoliageChunk[count];
        MapFoliageChunkKey prior = new(-1, -1);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = span.Slice(HeaderLength + i * RecordLength, RecordLength);
            var key = new MapFoliageChunkKey(BinaryPrimitives.ReadInt32LittleEndian(record), BinaryPrimitives.ReadInt32LittleEndian(record[4..]));
            if (key.X is < 0 or > 32767 || key.Z is < 0 or > 32767 ||
                (i > 0 && (key.Z < prior.Z || (key.Z == prior.Z && key.X <= prior.X))))
                throw key.X is < 0 or > 32767 || key.Z is < 0 or > 32767
                    ? InvalidKey(key, i, "foliage pack keys are not unique and ordered by Z then X.")
                    : new MapDocumentException(MapDocumentRefusal.OrderInvalid, "foliage pack keys are not unique and ordered by Z then X.")
                    { FoliageChunk = key, Index = i, FieldPath = "Key" };
            var chunk = new MapFoliageChunk(key, record[40..]);
            string expectedHash = Convert.ToHexStringLower(record.Slice(8, 32));
            if (chunk.Sha256 != expectedHash)
                throw new MapDocumentException(MapDocumentRefusal.DigestMismatch, "foliage pack raw chunk digest mismatch.")
                { FoliageChunk = key, Index = i, ExpectedHash = expectedHash, ActualHash = chunk.Sha256, DigestKind = "foliage density" };
            chunks[i] = chunk; prior = key;
        }
        return Array.AsReadOnly(chunks);
    }
    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void RequireDigest(string digest)
    {
        if (digest is not { Length: 64 } || digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new MapDocumentException(MapDocumentRefusal.DigestMalformed, "foliage: invalid SHA-256 name.") { ActualHash = digest };
    }

    static MapDocumentException InvalidKey(MapFoliageChunkKey key, int index, string message) =>
        new(MapDocumentRefusal.ValueOutsideRange, message)
        {
            FoliageChunk = key, Index = index, FieldPath = key.X is < 0 or > 32767 ? "Key.X" : "Key.Z",
            ActualInteger = key.X is < 0 or > 32767 ? key.X : key.Z, MinimumInteger = 0, MaximumInteger = 32767,
        };

}
