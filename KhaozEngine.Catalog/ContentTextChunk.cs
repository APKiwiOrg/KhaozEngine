using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// One decoded <c>KECT</c> chunk: the language tag, the canonical header, and the UNCOMPRESSED body kept as
/// it arrived.
/// <para>
/// There is no decoded entry here and no <c>Dictionary&lt;string, string&gt;</c>. Spec 7.6's decoded
/// language is the body itself plus one index over it, and nothing in a chunk exists as UTF-16 until
/// something asks for it, which is what keeps a 50,000 item language at about 9.6 MB rather than the 44 MB
/// the string dictionary measured. The index and the string cache are the reader's, not this type's.
/// </para>
/// </summary>
public sealed class ContentTextChunk
{
    readonly byte[] _canonicalHeader;
    readonly byte[] _body;

    internal ContentTextChunk(string languageTag, byte[] canonicalHeader, byte[] body, int entryCount)
    {
        LanguageTag = languageTag;
        _canonicalHeader = canonicalHeader;
        _body = body;
        EntryCount = entryCount;
    }

    /// <summary>The BCP-47 language tag, which is inside the hash as well as beside it.</summary>
    public string LanguageTag { get; }

    /// <summary>How many entries the body carries, which the decode already walked and checked.</summary>
    public int EntryCount { get; }

    /// <summary>
    /// The canonical header, <c>17 + tagLen</c> bytes with <c>compression</c> forced to 0 and
    /// <c>storedBytes</c> forced equal to <c>uncompressedBytes</c>. It is the first half of the canonical
    /// bytes the chunk hash is taken over.
    /// </summary>
    public ReadOnlySpan<byte> CanonicalHeader => _canonicalHeader;

    /// <summary>The uncompressed body, which is the storage a reader indexes rather than a copy of it.</summary>
    public ReadOnlySpan<byte> Body => _body;

    /// <summary>
    /// The chunk's content address, taken over the canonical header and the body IN PLACE, with no
    /// intermediate copy of the canonical bytes. That matters here: a language chunk is megabytes, so
    /// materialising the canonical form to verify it would double the resident cost of a verification.
    /// </summary>
    public string ComputeHash() => ContentHash.OfChunkStreaming(_canonicalHeader, _body, ContentHash.TextDomain);

    /// <summary>Walks the entries in the order the body carries them, which is ascending ordinal by key.</summary>
    public ContentTextChunkEnumerator EnumerateEntries() => new(_body);
}

/// <summary>
/// Walks a decoded text chunk's entries without allocating and without materialising a string. The decode
/// already checked every length and the ordering, so this is a pure walk.
/// </summary>
public ref struct ContentTextChunkEnumerator
{
    readonly ReadOnlySpan<byte> _body;
    int _offset;
    int _remaining;
    int _keyStart;
    int _keyLength;
    int _valueStart;
    int _valueLength;

    internal ContentTextChunkEnumerator(ReadOnlySpan<byte> body)
    {
        _body = body;
        _offset = 0;
        _remaining = 0;
        if (ContentVarint.TryRead(body, ref _offset, out uint entryCount, out _))
        {
            _remaining = (int)entryCount;
        }
    }

    /// <summary>The current entry's key, as the UTF-8 bytes the body holds.</summary>
    public readonly ReadOnlySpan<byte> Key => _body.Slice(_keyStart, _keyLength);

    /// <summary>The current entry's value, as the UTF-8 bytes the body holds.</summary>
    public readonly ReadOnlySpan<byte> Value => _body.Slice(_valueStart, _valueLength);

    /// <summary>Advances to the next entry, or returns false at the end.</summary>
    public bool MoveNext()
    {
        if (_remaining <= 0)
        {
            return false;
        }

        _remaining--;
        _keyLength = _body[_offset++];
        _keyStart = _offset;
        _offset += _keyLength;

        _ = ContentVarint.TryRead(_body, ref _offset, out uint valueLength, out _);
        _valueLength = (int)valueLength;
        _valueStart = _offset;
        _offset += _valueLength;
        return true;
    }
}
