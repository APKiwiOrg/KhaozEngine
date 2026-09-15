using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>A text chunk as stored: the file, its hash, and both lengths.</summary>
public sealed class EncodedTextChunk
{
    public required string LanguageTag { get; init; }
    public required string Hash { get; init; }
    public required byte[] StoredFile { get; init; }
    public required int UncompressedBytes { get; init; }
    public required int EntryCount { get; init; }

    public int StoredBytes => StoredFile.Length;
}

/// <summary>
/// The <c>KECT</c> per-language text chunk of spec section 7.6. A variable length header of
/// <c>17 + tagLen</c> bytes, then an entry list of derived key and value, ordinal ascending by key. The
/// canonical bytes the hash is taken over are the whole logical chunk uncompressed, header included.
/// </summary>
public static class ContentTextChunkCodec
{
    public static readonly byte[] Magic = "KECT"u8.ToArray();

    public const int MaxKeyBytes = 192;
    public const int MaxValueBytes = 8192;

    public static EncodedTextChunk Encode(string languageTag, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageTag);
        ArgumentNullException.ThrowIfNull(entries);

        byte[] tagBytes = Encoding.UTF8.GetBytes(languageTag);
        if (tagBytes.Length is < 1 or > 35) throw new ArgumentException("A language tag is 1 to 35 bytes.", nameof(languageTag));

        int bodyBytes = ContentVarint.Size((uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            int keyLength = Encoding.UTF8.GetByteCount(entries[i].Key);
            int valueLength = Encoding.UTF8.GetByteCount(entries[i].Value);
            if (keyLength is < 1 or > MaxKeyBytes) throw new ArgumentException("A text key is 1 to 192 bytes.", nameof(entries));
            if (valueLength > MaxValueBytes) throw new ArgumentException("A text value is at most 8192 bytes.", nameof(entries));
            bodyBytes += 1 + keyLength + ContentVarint.Size((uint)valueLength) + valueLength;
        }

        byte[] body = new byte[bodyBytes];
        int written = ContentVarint.Write(body, (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            int keyLength = Encoding.UTF8.GetByteCount(entries[i].Key);
            body[written++] = (byte)keyLength;
            written += Encoding.UTF8.GetBytes(entries[i].Key, body.AsSpan(written));
            int valueLength = Encoding.UTF8.GetByteCount(entries[i].Value);
            written += ContentVarint.Write(body.AsSpan(written), (uint)valueLength);
            written += Encoding.UTF8.GetBytes(entries[i].Value, body.AsSpan(written));
        }

        int headerBytes = ContentPackFormat.TextHeaderFixedBytes + tagBytes.Length;
        byte[] canonical = new byte[headerBytes + body.Length];
        WriteHeader(canonical, tagBytes, ContentPackFormat.CompressionNone, body.Length, body.Length);
        body.CopyTo(canonical.AsSpan(headerBytes));
        string hash = ContentHash.OfTextChunk(canonical);

        byte[] compressed = ContentChunkCodec.Compress(body);
        byte[] stored;
        if (compressed.Length >= body.Length)
        {
            stored = canonical;
        }
        else
        {
            stored = new byte[headerBytes + compressed.Length];
            WriteHeader(stored, tagBytes, ContentPackFormat.CompressionBrotli, body.Length, compressed.Length);
            compressed.CopyTo(stored.AsSpan(headerBytes));
        }

        return new EncodedTextChunk
        {
            LanguageTag = languageTag,
            Hash = hash,
            StoredFile = stored,
            UncompressedBytes = body.Length,
            EntryCount = entries.Count,
        };
    }

    /// <summary>Decodes a stored text chunk into a dictionary. Total: a reason token, never a throw.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> file, out Dictionary<string, string>? entries, out string reason)
    {
        entries = null;
        if (file.Length < ContentPackFormat.TextHeaderFixedBytes + 1) { reason = "text-truncated-header"; return false; }
        if (!file[..4].SequenceEqual(Magic)) { reason = "text-magic"; return false; }
        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (formatVersion != ContentPackFormat.TextChunkFormatVersion) { reason = "text-format-version"; return false; }
        int tagLength = file[6];
        if (tagLength is < 1 or > 35) { reason = "text-language-tag"; return false; }
        int headerBytes = ContentPackFormat.TextHeaderFixedBytes + tagLength;
        if (file.Length < headerBytes) { reason = "text-truncated-header"; return false; }
        byte compression = file[7 + tagLength];
        byte reserved = file[8 + tagLength];
        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(file[(9 + tagLength)..]);
        uint storedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[(13 + tagLength)..]);
        if (reserved != 0) { reason = "chunk-reserved-set"; return false; }
        if (uncompressed > ContentPackFormat.MaxChunkUncompressedBytes) { reason = "chunk-too-large"; return false; }
        if (storedBytes != file.Length - headerBytes) { reason = "chunk-stored-length"; return false; }

        byte[] body = new byte[uncompressed];
        ReadOnlySpan<byte> storedBody = file[headerBytes..];
        if (compression == ContentPackFormat.CompressionNone)
        {
            storedBody.CopyTo(body);
        }
        else if (!BrotliDecoder.TryDecompress(storedBody, body, out int decompressed) || decompressed != uncompressed)
        {
            reason = "chunk-decompress";
            return false;
        }

        int cursor = 0;
        if (!ContentVarint.TryRead(body, ref cursor, out uint entryCount)) { reason = "text-entry-count"; return false; }
        var map = new Dictionary<string, string>((int)entryCount, StringComparer.Ordinal);
        for (uint i = 0; i < entryCount; i++)
        {
            if (cursor >= body.Length) { reason = "text-truncated-entry"; return false; }
            int keyLength = body[cursor++];
            if (keyLength is < 1 or > MaxKeyBytes) { reason = "text-key-length"; return false; }
            if (cursor + keyLength > body.Length) { reason = "text-truncated-entry"; return false; }
            string key = Encoding.UTF8.GetString(body, cursor, keyLength);
            cursor += keyLength;
            if (!ContentVarint.TryRead(body, ref cursor, out uint valueLength)) { reason = "text-value-length"; return false; }
            if (valueLength > MaxValueBytes) { reason = "text-value-length"; return false; }
            if (cursor + (int)valueLength > body.Length) { reason = "text-truncated-entry"; return false; }
            string value = Encoding.UTF8.GetString(body, cursor, (int)valueLength);
            cursor += (int)valueLength;
            map[key] = value;
        }
        if (cursor != body.Length) { reason = "text-trailing-bytes"; return false; }
        entries = map;
        reason = string.Empty;
        return true;
    }

    private static void WriteHeader(Span<byte> destination, byte[] tagBytes, byte compression, int uncompressed, int stored)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ContentPackFormat.TextChunkFormatVersion);
        destination[6] = (byte)tagBytes.Length;
        tagBytes.CopyTo(destination[7..]);
        destination[7 + tagBytes.Length] = compression;
        destination[8 + tagBytes.Length] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(9 + tagBytes.Length)..], (uint)uncompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(13 + tagBytes.Length)..], (uint)stored);
    }
}
