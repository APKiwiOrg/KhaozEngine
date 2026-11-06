using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Only verified digest names can address writer-owned surface files.</summary>
internal static class MapSurfaceStorageLayout
{
    internal const int MaxPageBytes = 1_048_576;
    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void RequireDigest(string digest)
    {
        if (digest is not { Length: 64 } || digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new MapDocumentException(MapDocumentRefusal.DigestMalformed, "invalid surface storage SHA-256 name")
                { ActualHash = digest, FieldPath = "sha256" };
    }
    internal static string PathOf(string root, char kind, string digest)
    {
        RequireDigest(digest);
        string dir = Path.Combine(root, "tiles", "surfaces", kind.ToString());
        return kind switch
        {
            'p' => Path.Combine(dir, digest[..2], digest + ".json"),
            'i' or 'd' => Path.Combine(dir, digest + ".json"),
            _ => throw new ArgumentException("invalid surface storage kind", nameof(kind)),
        };
    }
    internal static byte[] Read(string root, char kind, string digest)
    {
        string path = PathOf(root, kind, digest);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long byteCount = stream.Length;
        if (byteCount is < 1 or > MaxPageBytes) throw ByteLimit(byteCount);
        byte[] bytes = new byte[(int)byteCount];
        stream.ReadExactly(bytes);
        RequireUnchangedRead(bytes, digest, stream.ReadByte());
        return bytes;
    }
    static void RequireUnchangedRead(ReadOnlySpan<byte> bytes, string digest, int trailingByte)
    {
        string? actualHash = trailingByte == -1 ? Digest(bytes) : null;
        if (trailingByte != -1 || actualHash != digest)
            throw new MapDocumentException(trailingByte != -1 ? MapDocumentRefusal.StorageReadChanged : MapDocumentRefusal.DigestMismatch,
                "surface storage byte digest mismatch")
            { ExpectedHash = digest, ActualHash = actualHash, ExpectedByteCount = bytes.Length,
                ActualByteCount = trailingByte != -1 ? (long)bytes.Length + 1 : bytes.Length,
                FieldPath = trailingByte != -1 ? "ByteCount" : "sha256" };
    }
    /// <summary>Injects the captured read values, including a trailing byte, into the production snapshot guard.</summary>
    internal static void RequireUnchangedReadForTests(ReadOnlySpan<byte> bytes, string digest, int trailingByte) =>
        RequireUnchangedRead(bytes, digest, trailingByte);

    static MapDocumentException ByteLimit(long count) => new(count < 1 ? MapDocumentRefusal.ValueOutsideRange
        : MapDocumentRefusal.WorkCapacityExceeded, "surface storage byte limit")
    { FieldPath = "ByteCount", ActualByteCount = count, ActualInteger = count,
        MinimumInteger = 1, MaximumInteger = MaxPageBytes, Budget = "MaxPageBytes", Limit = MaxPageBytes };

    internal static void Write(string root, char kind, byte[] bytes, MapDocumentSaveOptions save)
    {
        if (bytes.Length is < 1 or > MaxPageBytes) throw ByteLimit(bytes.Length);
        string digest = Digest(bytes), path = PathOf(root, kind, digest);
        if (File.Exists(path))
        {
            _ = Read(root, kind, digest);
            return;
        }
        save.OnStep?.Invoke(MapTiledSaveStep.BeforeTileWrite);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + MapTileFile.TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            if (save.Durability == MapSaveDurability.PowerFail) stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path);
        save.OnStep?.Invoke(MapTiledSaveStep.AfterTileWrite);
    }
}
