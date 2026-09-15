using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The content addressed store of spec section 8.2: one file per hash under a two level shard,
/// <c>&lt;root&gt;/&lt;hash[0..2]&gt;/&lt;hash[2..4]&gt;/&lt;hash&gt;.kec</c>, written temp then moved. The
/// version pointer lives outside the shard tree under <c>versions/</c>, because a shard name is derived
/// from a hash and a version number is not one.
/// <para>
/// The spike's copy is synchronous. The shipped store is async over <c>IPackStore</c>; the measurement
/// wants the file system cost without a task per chunk in the middle of it.
/// </para>
/// </summary>
public sealed class FileSystemPackStore
{
    public FileSystemPackStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public long BytesWritten { get; private set; }

    public int FilesWritten { get; private set; }

    public string PathFor(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return Path.Combine(Root, hash[..2], hash.Substring(2, 2), hash + ".kec");
    }

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    public byte[]? Get(string hash)
    {
        string path = PathFor(hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Put(string hash, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string path = PathFor(hash);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(path)) return;
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
        lock (this)
        {
            BytesWritten += bytes.Length;
            FilesWritten++;
        }
    }

    public void ResetWriteCounters()
    {
        lock (this)
        {
            BytesWritten = 0;
            FilesWritten = 0;
        }
    }

    /// <summary>The version pointer of publish step 9: the version's two manifest hashes, one per line.</summary>
    public void PutVersionPointer(int versionNumber, string serverManifestHash, string clientManifestHash)
    {
        string directory = Path.Combine(Root, "versions");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, serverManifestHash + "\n" + clientManifestHash + "\n", Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    public IReadOnlyList<string>? GetVersionPointer(int versionNumber)
    {
        string path = Path.Combine(Root, "versions",
            versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!File.Exists(path)) return null;
        return File.ReadAllLines(path);
    }
}
