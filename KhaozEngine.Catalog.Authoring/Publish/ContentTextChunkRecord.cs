using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One language's KECT chunk as a publish computes it: the exact wire tag, the content hash, the stored file
/// for a chunk this publish encoded, and whether it was carried forward instead. The bytes are an OWNED copy,
/// so a caller reusing its buffer cannot change what the writer puts.
/// <para>
/// <b>A REUSED chunk carries no bytes.</b> Its hash names a file an earlier version already wrote, which is
/// what makes an unchanged language free on the next publish, empty languages included.
/// </para>
/// </summary>
public sealed class ContentTextChunkRecord
{
    /// <summary>Builds one chunk record.</summary>
    /// <param name="wireTag">The exact spelling both manifests name this chunk by.</param>
    /// <param name="hash">The content address, lower hex.</param>
    /// <param name="storedFile">The file as it goes to the pack store, copied, and empty on a reused chunk.</param>
    /// <param name="isReused">Whether this chunk was carried forward rather than encoded.</param>
    /// <exception cref="ArgumentNullException">A string is null.</exception>
    /// <exception cref="ArgumentException">The tag is illegal, the hash is not lower hex, or a reused chunk carries bytes.</exception>
    public ContentTextChunkRecord(string wireTag, string hash, ReadOnlyMemory<byte> storedFile, bool isReused)
    {
        ArgumentNullException.ThrowIfNull(wireTag);
        _ = ContentTextLanguageTag.Normalize(wireTag);
        if (isReused && !storedFile.IsEmpty)
        {
            throw new ArgumentException("A reused text chunk carries no bytes, because an earlier version wrote its file.", nameof(storedFile));
        }

        WireTag = wireTag;
        Hash = ContentTextLanguage.RequireHash(hash, nameof(hash));
        StoredFile = storedFile.ToArray();
        IsReused = isReused;
    }

    /// <summary>The exact spelling both manifests name this chunk by.</summary>
    public string WireTag { get; }

    /// <summary>The content address, lower hex.</summary>
    public string Hash { get; }

    /// <summary>The stored file the writer puts, and empty on a reused chunk.</summary>
    public ReadOnlyMemory<byte> StoredFile { get; }

    /// <summary>Whether this chunk was carried forward rather than encoded.</summary>
    public bool IsReused { get; }

    /// <summary>The same chunk as the next version carries it forward: the hash without the bytes.</summary>
    public ContentTextChunkRecord AsReused() => IsReused ? this : new ContentTextChunkRecord(WireTag, Hash, default, true);
}
