using System;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Stored JSON uses the payload's UTF-8 ceiling, independently of binary pack budgets. Counting discards
/// bytes instead of allocating a second manifest. The membership preflight uses a lower bound on actual JSON size,
/// so it can refuse an impossible directory before allocating its coordinate rows without rejecting a legal one.</summary>
internal static class MapFoliageManifestBudget
{
    internal static void Check(MapFoliageSet set, MapFoliageStorageIndex index,
        long maxUtf8Bytes = MapFoliagePayloadLimits.MaxUtf8Bytes)
    {
        using var stream = new Counter(maxUtf8Bytes);
        using var writer = new Utf8JsonWriter(stream);
        MapFoliageStorage.WriteManifest(writer, set, index);
        writer.Flush();
    }

    internal static void CheckMemberships(long count, long maxUtf8Bytes = MapFoliagePayloadLimits.MaxUtf8Bytes)
    {
        // {"x":0,"z":0} is the smallest possible coordinate object. Commas and all other metadata add bytes.
        if (count < 0 || maxUtf8Bytes < 0 || count > maxUtf8Bytes / 13) throw Limit(maxUtf8Bytes);
    }

    static MapDocumentException Limit(long limit) => new($"foliage stored JSON exceeds {limit} UTF-8 bytes.");

    sealed class Counter(long limit) : Stream
    {
        long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - _length) throw Limit(limit);
            _length += buffer.Length;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
