using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;

namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>KECT</c> per-language text chunk of spec 7.6: one chunk set per language, listed in the manifest
/// with its own hash so a client downloads only the languages it wants.
/// <para>
/// The header is VARIABLE length, <c>17 + tagLen</c>, because the language tag sits inside it. The canonical
/// bytes the hash is taken over include that header WHOLE, tag and all, so the same strings under two
/// language tags are two different chunks with two different hashes. This was the one format spec 7.8 named
/// a canonical form for without anyone writing it down, and the symptom of two implementers guessing
/// differently is a client that fetches a chunk, verifies it, fails, and discards it forever.
/// </para>
/// <para>
/// The body is <c>[entryCount varint]</c> then, ASCENDING ORDINAL BY KEY,
/// <c>[keyLen byte][key UTF-8][valueLen varint][value UTF-8]</c>. The value cap bounds one entry and the two
/// header-level size refusals bound the file, which is the pairing the entry cap alone was never going to
/// give.
/// </para>
/// </summary>
public static class ContentTextChunkCodec
{
    /// <summary>The widest key the length byte may declare, contracts 12.2.</summary>
    public const int MaxKeyBytes = 192;

    /// <summary>The widest value, generous for a UI string and bounded so a malformed length cannot make a reader allocate a gigabyte.</summary>
    public const int MaxValueBytes = 8192;

    /// <summary>The widest BCP-47 language tag.</summary>
    public const int MaxLanguageTagBytes = 35;

    /// <summary>The first four bytes are not <c>KECT</c>.</summary>
    public const string ReasonMagic = "text-magic";

    /// <summary>The chunk's format version is not this reader's, which refuses the whole record.</summary>
    public const string ReasonFormatVersion = "text-format-version";

    /// <summary>The file ends inside the variable-length header, or a body field runs past the end.</summary>
    public const string ReasonTruncated = "text-truncated";

    /// <summary>The language tag is empty, over 35 bytes, or not valid UTF-8.</summary>
    public const string ReasonLanguageTag = "text-language-tag";

    /// <summary>The reserved byte is non-zero, the fail-closed rule for an extension a reader cannot skip.</summary>
    public const string ReasonReservedSet = "chunk-reserved-set";

    /// <summary>The compression byte names a compressor this reader does not have.</summary>
    public const string ReasonCompression = "chunk-compression";

    /// <summary>
    /// The declared uncompressed length is over the chunk ceiling, taken from the header alone, or a Brotli
    /// body expanded past the length its own header claims.
    /// </summary>
    public const string ReasonTooLarge = ContentCompression.ReasonTooLarge;

    /// <summary>The declared stored length is not the body length actually received.</summary>
    public const string ReasonStoredLength = "chunk-stored-length";

    /// <summary>The body is not a stream this decoder can finish reading.</summary>
    public const string ReasonDecompress = ContentCompression.ReasonDecompress;

    /// <summary>A key length is 0 or over 192.</summary>
    public const string ReasonKeyLength = "text-key-length";

    /// <summary>A key is not valid UTF-8, so the chunk is not the canonical form of the strings it carries.</summary>
    public const string ReasonKeyEncoding = "text-key-encoding";

    /// <summary>A value length is over 8,192.</summary>
    public const string ReasonValueLength = "text-value-length";

    /// <summary>A value is not valid UTF-8, which would reach a player as a run of replacement characters.</summary>
    public const string ReasonValueEncoding = "text-value-encoding";

    /// <summary>Entries are not strictly ascending ordinal by key, so the chunk is not canonical.</summary>
    public const string ReasonEntryOrder = "text-entry-order";

    /// <summary>Bytes remain after the last entry, so the chunk describes fewer entries than it carries.</summary>
    public const string ReasonTrailingBytes = "text-trailing-bytes";

    /// <summary>
    /// The canonical uncompressed bytes the text chunk hash is taken over: the variable header with
    /// <c>compression</c> forced to 0 and <c>storedBytes</c> forced equal to <c>uncompressedBytes</c>, then
    /// the uncompressed body.
    /// </summary>
    public static byte[] Canonical(string languageTag, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        byte[] tag = MeasureTag(languageTag);
        byte[] body = EncodeBody(entries);
        int headerBytes = ContentPackFormat.TextHeaderFixedBytes + tag.Length;

        byte[] canonical = new byte[headerBytes + body.Length];
        WriteHeader(canonical, tag, ContentPackFormat.CompressionNone, body.Length, body.Length);
        body.CopyTo(canonical.AsSpan(headerBytes));
        return canonical;
    }

    /// <summary>The chunk's content address, under the <c>kec/text/</c> sub-domain.</summary>
    public static string Hash(string languageTag, IReadOnlyList<KeyValuePair<string, string>> entries)
        => ContentHash.OfTextChunk(Canonical(languageTag, entries));

    /// <summary>
    /// The file as STORED: the canonical bytes when Brotli does not shrink the body, and a compressed body
    /// under a header that says so when it does. Writes the entries in the order it is handed them, so a
    /// publisher owns the ordinal sort and a test can build a file this reader refuses.
    /// </summary>
    public static byte[] Encode(string languageTag, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        byte[] tag = MeasureTag(languageTag);
        byte[] canonical = Canonical(languageTag, entries);
        int headerBytes = ContentPackFormat.TextHeaderFixedBytes + tag.Length;
        ReadOnlySpan<byte> body = canonical.AsSpan(headerBytes);

        byte[] scratch = new byte[body.Length];
        if (!ContentCompression.TryCompress(body, ContentPackFormat.BrotliQuality, scratch, out int compressed))
        {
            return canonical;
        }

        byte[] stored = new byte[headerBytes + compressed];
        WriteHeader(stored, tag, ContentPackFormat.CompressionBrotli, body.Length, compressed);
        scratch.AsSpan(0, compressed).CopyTo(stored.AsSpan(headerBytes));
        return stored;
    }

    /// <summary>
    /// Decodes a text chunk, checking every entry's lengths and the ordinal ordering. Total: false plus a
    /// stable reason token, never a throw. The two size refusals are taken from the variable header BEFORE
    /// any buffer is sized and before the compressor is touched.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> file,
        [MaybeNullWhen(false)] out ContentTextChunk chunk,
        out string? reason)
    {
        chunk = null;
        if (file.Length < ContentPackFormat.TextHeaderFixedBytes + 1) { reason = ReasonTruncated; return false; }
        if (!file[..ContentPackFormat.MagicBytes].SequenceEqual(ContentPackFormat.TextChunkMagic)) { reason = ReasonMagic; return false; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[4..]) != ContentPackFormat.TextChunkFormatVersion) { reason = ReasonFormatVersion; return false; }

        int tagLength = file[6];
        if (tagLength is < 1 or > MaxLanguageTagBytes) { reason = ReasonLanguageTag; return false; }

        int headerBytes = ContentPackFormat.TextHeaderFixedBytes + tagLength;
        if (file.Length < headerBytes) { reason = ReasonTruncated; return false; }
        if (!TryUtf8(file.Slice(7, tagLength), out string? languageTag)) { reason = ReasonLanguageTag; return false; }

        byte compression = file[7 + tagLength];
        if (compression is not (ContentPackFormat.CompressionNone or ContentPackFormat.CompressionBrotli)) { reason = ReasonCompression; return false; }
        if (file[8 + tagLength] != 0) { reason = ReasonReservedSet; return false; }

        uint uncompressedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[(9 + tagLength)..]);
        uint storedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[(13 + tagLength)..]);
        if (uncompressedBytes > ContentPackFormat.MaxChunkUncompressedBytes) { reason = ReasonTooLarge; return false; }
        if (storedBytes != (uint)(file.Length - headerBytes)) { reason = ReasonStoredLength; return false; }

        byte[] body = new byte[uncompressedBytes];
        ReadOnlySpan<byte> stored = file[headerBytes..];
        if (compression == ContentPackFormat.CompressionNone)
        {
            if (stored.Length != body.Length) { reason = ReasonStoredLength; return false; }
            stored.CopyTo(body);
        }
        else if (!ContentCompression.TryFill(stored, body, out reason))
        {
            return false;
        }

        if (!TryCheckBody(body, out int entryCount, out reason))
        {
            return false;
        }

        byte[] canonicalHeader = new byte[headerBytes];
        WriteHeader(canonicalHeader, file.Slice(7, tagLength).ToArray(), ContentPackFormat.CompressionNone, body.Length, body.Length);
        chunk = new ContentTextChunk(languageTag, canonicalHeader, body, entryCount);
        reason = null;
        return true;
    }

    static bool TryCheckBody(ReadOnlySpan<byte> body, out int entryCount, out string? reason)
    {
        entryCount = 0;
        int cursor = 0;
        if (!ContentVarint.TryRead(body, ref cursor, out uint declared, out reason))
        {
            return false;
        }

        int previousKeyStart = -1;
        int previousKeyLength = 0;
        for (uint i = 0; i < declared; i++)
        {
            if (cursor >= body.Length) { reason = ReasonTruncated; return false; }
            int keyLength = body[cursor++];
            if (keyLength is < 1 or > MaxKeyBytes) { reason = ReasonKeyLength; return false; }
            if (cursor + keyLength > body.Length) { reason = ReasonTruncated; return false; }

            ReadOnlySpan<byte> key = body.Slice(cursor, keyLength);
            if (!IsUtf8(key)) { reason = ReasonKeyEncoding; return false; }
            if (previousKeyStart >= 0 && body.Slice(previousKeyStart, previousKeyLength).SequenceCompareTo(key) >= 0)
            {
                // Ordinal over the UTF-8 BYTES, which is the canonical order for a byte format and which
                // agrees with an ordinal string compare for every key contracts 12.2 allows. STRICT, so a
                // repeated key is the same refusal as a reordered one.
                reason = ReasonEntryOrder;
                return false;
            }

            previousKeyStart = cursor;
            previousKeyLength = keyLength;
            cursor += keyLength;

            if (!ContentVarint.TryRead(body, ref cursor, out uint valueLength, out reason)) { return false; }
            if (valueLength > MaxValueBytes) { reason = ReasonValueLength; return false; }
            if (cursor + valueLength > body.Length) { reason = ReasonTruncated; return false; }
            if (!IsUtf8(body.Slice(cursor, (int)valueLength))) { reason = ReasonValueEncoding; return false; }
            cursor += (int)valueLength;
        }

        if (cursor != body.Length) { reason = ReasonTrailingBytes; return false; }

        entryCount = (int)declared;
        reason = null;
        return true;
    }

    static byte[] EncodeBody(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int bodyBytes = ContentVarint.Size((uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            (int keyBytes, int valueBytes) = Measure(entries[i]);
            bodyBytes += 1 + keyBytes + ContentVarint.Size((uint)valueBytes) + valueBytes;
        }

        byte[] body = new byte[bodyBytes];
        int written = ContentVarint.Write(body, (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            (int keyBytes, int valueBytes) = Measure(entries[i]);
            body[written++] = (byte)keyBytes;
            written += Encoding.UTF8.GetBytes(entries[i].Key, body.AsSpan(written));
            written += ContentVarint.Write(body.AsSpan(written), (uint)valueBytes);
            written += Encoding.UTF8.GetBytes(entries[i].Value, body.AsSpan(written));
        }

        return body;
    }

    static (int KeyBytes, int ValueBytes) Measure(KeyValuePair<string, string> entry)
    {
        ArgumentNullException.ThrowIfNull(entry.Key);
        ArgumentNullException.ThrowIfNull(entry.Value);

        int keyBytes = Encoding.UTF8.GetByteCount(entry.Key);
        if (keyBytes is < 1 or > MaxKeyBytes)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"A text key is 1 to {MaxKeyBytes} UTF-8 bytes, and '{entry.Key}' is {keyBytes}."));
        }

        int valueBytes = Encoding.UTF8.GetByteCount(entry.Value);
        return valueBytes <= MaxValueBytes
            ? (keyBytes, valueBytes)
            : throw new ArgumentException(FormattableString.Invariant(
                $"A text value is at most {MaxValueBytes} UTF-8 bytes, and the value of '{entry.Key}' is {valueBytes}."));
    }

    static byte[] MeasureTag(string languageTag)
    {
        ArgumentNullException.ThrowIfNull(languageTag);
        byte[] tag = Encoding.UTF8.GetBytes(languageTag);
        return tag.Length is >= 1 and <= MaxLanguageTagBytes
            ? tag
            : throw new ArgumentException(FormattableString.Invariant(
                $"A language tag is 1 to {MaxLanguageTagBytes} UTF-8 bytes, and '{languageTag}' is {tag.Length}."), nameof(languageTag));
    }

    static void WriteHeader(Span<byte> destination, byte[] tag, byte compression, int uncompressedBytes, int storedBytes)
    {
        ContentPackFormat.TextChunkMagic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ContentPackFormat.TextChunkFormatVersion);
        destination[6] = (byte)tag.Length;
        tag.CopyTo(destination[7..]);
        destination[7 + tag.Length] = compression;
        destination[8 + tag.Length] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(9 + tag.Length)..], (uint)uncompressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(13 + tag.Length)..], (uint)storedBytes);
    }

    /// <summary>
    /// UTF-16 from UTF-8 without throwing and without silently substituting, so a tag a decoder accepted
    /// re-encodes to the bytes it came from.
    /// </summary>
    /// <summary>
    /// The ONE UTF-8 rule this format holds every run of bytes to: strictly valid, no replacement of an
    /// invalid sequence. The language tag was checked this way from the start, and keys and values were NOT,
    /// so a chunk carrying an invalid sequence decoded and surfaced later as U+FFFD, which is a mojibake
    /// string on a player's screen behind a chunk that verified. It also breaks the canonical property: two
    /// different byte sequences would read back as the same string.
    /// </summary>
    static bool IsUtf8(ReadOnlySpan<byte> bytes) => Utf8.IsValid(bytes);

    static bool TryUtf8(ReadOnlySpan<byte> bytes, [MaybeNullWhen(false)] out string text)
    {
        if (!IsUtf8(bytes))
        {
            text = null;
            return false;
        }

        Span<char> buffer = stackalloc char[MaxLanguageTagBytes];
        OperationStatus status = Utf8.ToUtf16(bytes, buffer, out int read, out int written, replaceInvalidSequences: false);
        if (status != OperationStatus.Done || read != bytes.Length)
        {
            text = null;
            return false;
        }

        text = new string(buffer[..written]);
        return true;
    }
}
