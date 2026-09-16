using System;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// ONE decoded language of the text chunks, resident the way spec 7.6 says a decoded language is held: the
/// decompressed chunk BODY itself, plus one open-addressed index of entry offsets over it.
/// <para>
/// <b>There is no decoded entry here and no <c>Dictionary&lt;string, string&gt;</c>.</b> Nothing in the
/// chunk exists as UTF-16 until something asks for it, which is what keeps a 50,000 item language at about
/// 9.6 MB resident against the 44.2 MB a string dictionary measured at the same size. The index holds each
/// entry's byte offset plus one, hashed on that entry's UTF-8 key and probed by an ordinal span compare,
/// exactly as a type's <c>KeyIds</c> table probes its rows.
/// </para>
/// <para>
/// The body is the chunk's own array rather than a copy of it, so decoding a language costs the
/// decompression and the bucket table and nothing else. The resolved-string cache is NOT here: it belongs
/// to <see cref="ContentStringCatalog"/>, because the thing worth bounding is the number of live strings
/// across every language rather than per language.
/// </para>
/// </summary>
public sealed class ContentTextIndex
{
    readonly byte[] body;
    readonly int[] buckets;
    readonly int entryCount;

    ContentTextIndex(string languageTag, byte[] body, int[] buckets, int entryCount)
    {
        LanguageTag = languageTag;
        this.body = body;
        this.buckets = buckets;
        this.entryCount = entryCount;
    }

    /// <summary>The BCP-47 tag of the language this holds, which is inside the chunk's hash as well as beside it.</summary>
    public string LanguageTag { get; }

    /// <summary>How many entries the language carries.</summary>
    public int EntryCount => entryCount;

    /// <summary>
    /// What this retains: the body and the bucket table. It is the resident figure budget P10 is stated
    /// against, and it deliberately does not count the strings a caller has asked for, which are the
    /// catalog's bounded cache.
    /// </summary>
    public long ApproximateBytes => body.LongLength + ((long)buckets.Length * sizeof(int));

    /// <summary>
    /// Indexes an already decoded chunk. The decode walked and checked every length, every key and the
    /// ordering, so this cannot fail: it is one pass building the bucket table.
    /// </summary>
    /// <param name="chunk">The decoded chunk, whose body becomes this index's storage rather than a copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="chunk"/> is null.</exception>
    public static ContentTextIndex Over(ContentTextChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        byte[] body = chunk.BodyArray;
        int capacity = CapacityFor(chunk.EntryCount);
        int[] buckets = new int[capacity];
        int mask = capacity - 1;

        int cursor = 0;
        _ = ContentVarint.TryRead(body, ref cursor, out uint declared, out _);
        for (uint i = 0; i < declared; i++)
        {
            int record = cursor;
            int keyLength = body[cursor++];
            ReadOnlySpan<byte> key = body.AsSpan(cursor, keyLength);
            cursor += keyLength;
            _ = ContentVarint.TryRead(body, ref cursor, out uint valueLength, out _);
            cursor += (int)valueLength;

            int slot = (int)(ContentKey.HashBytes(key) & (uint)mask);
            while (buckets[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            buckets[slot] = record + 1;
        }

        return new ContentTextIndex(chunk.LanguageTag, body, buckets, (int)declared);
    }

    /// <summary>
    /// The client's own path: stored chunk bytes out of the pack cache straight to a resident language. It
    /// is the decoder that can refuse, and it refuses with a stable token rather than throwing, because
    /// these are bytes that came off a wire.
    /// </summary>
    /// <param name="file">The stored <c>KECT</c> file.</param>
    /// <param name="index">The resident language, or null on any refusal.</param>
    /// <param name="reason">The stable reason token, or null on success.</param>
    public static bool TryDecode(
        ReadOnlySpan<byte> file,
        [MaybeNullWhen(false)] out ContentTextIndex index,
        out string? reason)
    {
        if (!ContentTextChunkCodec.TryDecode(file, out ContentTextChunk? chunk, out reason))
        {
            index = null;
            return false;
        }

        index = Over(chunk);
        return true;
    }

    /// <summary>
    /// The entry offset one key resolves to, plus nothing else. A caller holds the OFFSET rather than the
    /// value, which is what lets the catalog key a resolved-string cache on a number it already has.
    /// </summary>
    /// <param name="key">The derived key, as UTF-8 bytes.</param>
    /// <param name="entryOffset">The entry's offset into the body, or -1 on a miss.</param>
    public bool TryGetOffset(ReadOnlySpan<byte> key, out int entryOffset)
    {
        if (buckets.Length == 0 || key.Length == 0)
        {
            entryOffset = -1;
            return false;
        }

        int mask = buckets.Length - 1;
        int slot = (int)(ContentKey.HashBytes(key) & (uint)mask);
        while (true)
        {
            int candidate = buckets[slot];
            if (candidate == 0)
            {
                entryOffset = -1;
                return false;
            }

            if (KeyAt(candidate - 1).SequenceEqual(key))
            {
                entryOffset = candidate - 1;
                return true;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>The value as a SLICE of the body. No allocation, and nothing here becomes UTF-16.</summary>
    /// <param name="key">The derived key, as UTF-8 bytes.</param>
    /// <param name="value">The value's bytes, or empty on a miss.</param>
    public bool TryGetUtf8(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        if (TryGetOffset(key, out int entryOffset))
        {
            value = ValueAt(entryOffset);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>The key at one entry offset, as the bytes the body holds.</summary>
    /// <param name="entryOffset">An offset <see cref="TryGetOffset"/> answered with.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entryOffset"/> is not inside the body.</exception>
    public ReadOnlySpan<byte> KeyAt(int entryOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryOffset, body.Length);
        return body.AsSpan(entryOffset + 1, body[entryOffset]);
    }

    /// <summary>The value at one entry offset, as the bytes the body holds.</summary>
    /// <param name="entryOffset">An offset <see cref="TryGetOffset"/> answered with.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entryOffset"/> is not inside the body.</exception>
    public ReadOnlySpan<byte> ValueAt(int entryOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryOffset, body.Length);

        int cursor = entryOffset;
        int keyLength = body[cursor++];
        cursor += keyLength;
        _ = ContentVarint.TryRead(body, ref cursor, out uint valueLength, out _);
        return body.AsSpan(cursor, (int)valueLength);
    }

    /// <summary>
    /// Buckets for an entry count: a power of two at twice the entries, so the table stays half empty and a
    /// linear probe stays short. The same sizing as the runtime's key index, for the same reason.
    /// </summary>
    static int CapacityFor(int entries)
    {
        if (entries <= 0)
        {
            return 0;
        }

        int capacity = 2;
        while (capacity < entries * 2)
        {
            capacity <<= 1;
        }

        return capacity;
    }
}
