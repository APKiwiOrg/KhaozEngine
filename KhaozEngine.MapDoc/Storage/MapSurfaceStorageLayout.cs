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
            throw new MapDocumentException("invalid surface storage SHA-256 name");
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
        if (stream.Length is < 1 or > MaxPageBytes) throw new MapDocumentException("surface storage byte limit");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || Digest(bytes) != digest)
            throw new MapDocumentException("surface storage byte digest mismatch");
        return bytes;
    }
    internal static void Write(string root, char kind, byte[] bytes, MapDocumentSaveOptions save)
    {
        if (bytes.Length is < 1 or > MaxPageBytes) throw new MapDocumentException("surface storage byte limit");
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
