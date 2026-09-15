using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;

namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>KECR</c> remap rule chunk of spec 7.7. ONE per manifest, at a reserved address outside any content
/// type's id space, holding the FULL rule list from sequence 1 rather than a delta, because a page can be
/// arbitrarily old and a delta pack would make the loader hold every intervening version.
/// <para>
/// It is in BOTH manifests and its contents are identical in both: a rule is <c>(id, id, kind)</c> and never
/// a value, so it carries no server-only information by construction.
/// </para>
/// <para>
/// The header is offsets 0 magic, 4 <c>formatVersion</c> uint16, 6 <c>compression</c>, 7 <c>reserved</c>, 8
/// <c>ruleCount</c> uint32, 12 <c>uncompressedBytes</c> uint32, 16 <c>storedBytes</c> uint32, then the body.
/// A gap in the sequence, a non-ascending sequence and an unknown kind are all REFUSALS rather than skips.
/// </para>
/// </summary>
public static class ContentRuleChunkCodec
{
    /// <summary>The first four bytes are not <c>KECR</c>.</summary>
    public const string ReasonMagic = "rule-magic";

    /// <summary>The chunk's format version is not this reader's, which refuses the whole record.</summary>
    public const string ReasonFormatVersion = "rule-format-version";

    /// <summary>The file is shorter than the fixed header.</summary>
    public const string ReasonTruncatedHeader = "rule-truncated-header";

    /// <summary>The reserved byte is non-zero, the fail-closed rule for an extension a reader cannot skip.</summary>
    public const string ReasonReservedSet = "chunk-reserved-set";

    /// <summary>The compression byte names a compressor this reader does not have.</summary>
    public const string ReasonCompression = "chunk-compression";

    /// <summary>The declared uncompressed length is over the chunk ceiling, taken from the header alone.</summary>
    public const string ReasonTooLarge = "chunk-too-large";

    /// <summary>The declared stored length is not the body length actually received.</summary>
    public const string ReasonStoredLength = "chunk-stored-length";

    /// <summary>The body did not decompress to the length its header declared.</summary>
    public const string ReasonDecompress = "chunk-decompress";

    /// <summary>A sequence number skips one, so the chunk is not the full list from sequence 1.</summary>
    public const string ReasonSequenceGap = "rule-sequence-gap";

    /// <summary>A sequence number goes backwards, so the apply order the rules were published in is lost.</summary>
    public const string ReasonSequenceOrder = "rule-sequence-order";

    /// <summary>Bytes remain after the last rule, so the chunk describes fewer rules than it carries.</summary>
    public const string ReasonTrailingBytes = "rule-trailing-bytes";

    /// <summary>
    /// The canonical uncompressed bytes the chunk hash is taken over: the header with <c>compression</c>
    /// forced to 0 and <c>storedBytes</c> forced equal to <c>uncompressedBytes</c>, then the body. That is
    /// what makes the hash independent of the compressor, so a compressor change is a no-op for every cached
    /// client.
    /// </summary>
    public static byte[] Canonical(IReadOnlyList<RemapRule> rulesInSequenceOrder)
    {
        ArgumentNullException.ThrowIfNull(rulesInSequenceOrder);

        int bodyBytes = 0;
        for (int i = 0; i < rulesInSequenceOrder.Count; i++)
        {
            bodyBytes += RemapRuleCodec.Size(rulesInSequenceOrder[i]);
        }

        byte[] canonical = new byte[ContentPackFormat.RuleHeaderBytes + bodyBytes];
        WriteHeader(canonical, rulesInSequenceOrder.Count, ContentPackFormat.CompressionNone, bodyBytes, bodyBytes);

        int written = ContentPackFormat.RuleHeaderBytes;
        for (int i = 0; i < rulesInSequenceOrder.Count; i++)
        {
            written += RemapRuleCodec.Write(canonical.AsSpan(written), rulesInSequenceOrder[i]);
        }

        return canonical;
    }

    /// <summary>The chunk's content address, under the <c>kec/rules/</c> sub-domain.</summary>
    public static string Hash(IReadOnlyList<RemapRule> rulesInSequenceOrder)
        => ContentHash.OfRuleChunk(Canonical(rulesInSequenceOrder));

    /// <summary>
    /// The file as STORED: the canonical bytes when Brotli does not shrink the body, and a compressed body
    /// under a header that says so when it does. Writes the rules in the order it is handed them, so a
    /// publisher owns the sequence and a test can build a file this reader refuses.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<RemapRule> rulesInSequenceOrder)
    {
        byte[] canonical = Canonical(rulesInSequenceOrder);
        ReadOnlySpan<byte> body = canonical.AsSpan(ContentPackFormat.RuleHeaderBytes);

        byte[] scratch = new byte[body.Length];
        if (!BrotliEncoder.TryCompress(body, scratch, out int compressed, ContentPackFormat.BrotliQuality, ContentPackFormat.BrotliWindow)
            || compressed >= body.Length)
        {
            return canonical;
        }

        byte[] stored = new byte[ContentPackFormat.RuleHeaderBytes + compressed];
        WriteHeader(stored, rulesInSequenceOrder.Count, ContentPackFormat.CompressionBrotli, body.Length, compressed);
        scratch.AsSpan(0, compressed).CopyTo(stored.AsSpan(ContentPackFormat.RuleHeaderBytes));
        return stored;
    }

    /// <summary>
    /// Decodes a rule chunk. Total: false plus a stable reason token, never a throw. The two size refusals
    /// are taken from the fixed header BEFORE any buffer is sized and before the decompressor is touched,
    /// because the hash is over the uncompressed bytes and so cannot bound what it has not decompressed yet.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> file,
        [MaybeNullWhen(false)] out RemapRuleSet rules,
        out string? reason)
    {
        rules = null;
        if (file.Length < ContentPackFormat.RuleHeaderBytes) { reason = ReasonTruncatedHeader; return false; }
        if (!file[..ContentPackFormat.MagicBytes].SequenceEqual(ContentPackFormat.RuleChunkMagic)) { reason = ReasonMagic; return false; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[4..]) != ContentPackFormat.RuleChunkFormatVersion) { reason = ReasonFormatVersion; return false; }

        byte compression = file[6];
        if (compression is not (ContentPackFormat.CompressionNone or ContentPackFormat.CompressionBrotli)) { reason = ReasonCompression; return false; }
        if (file[7] != 0) { reason = ReasonReservedSet; return false; }

        uint ruleCount = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint uncompressedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        uint storedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        if (uncompressedBytes > ContentPackFormat.MaxChunkUncompressedBytes) { reason = ReasonTooLarge; return false; }
        if (storedBytes != (uint)(file.Length - ContentPackFormat.RuleHeaderBytes)) { reason = ReasonStoredLength; return false; }

        byte[] body = new byte[uncompressedBytes];
        ReadOnlySpan<byte> stored = file[ContentPackFormat.RuleHeaderBytes..];
        if (compression == ContentPackFormat.CompressionNone)
        {
            if (stored.Length != body.Length) { reason = ReasonStoredLength; return false; }
            stored.CopyTo(body);
        }
        else if (!BrotliDecoder.TryDecompress(stored, body, out int decompressed) || decompressed != body.Length)
        {
            // The destination is exactly the declared length, so a stream that expands past what its
            // container claims is refused here rather than allocated for.
            reason = ReasonDecompress;
            return false;
        }

        var decoded = new List<RemapRule>((int)Math.Min(ruleCount, 1024));
        int cursor = 0;
        int expectedSequence = 1;
        for (uint i = 0; i < ruleCount; i++)
        {
            if (!RemapRuleCodec.TryRead(body, ref cursor, out RemapRule? rule, out reason)) { return false; }
            if (rule.Sequence != expectedSequence)
            {
                reason = rule.Sequence < expectedSequence ? ReasonSequenceOrder : ReasonSequenceGap;
                return false;
            }

            expectedSequence++;
            decoded.Add(rule);
        }

        if (cursor != body.Length) { reason = ReasonTrailingBytes; return false; }

        rules = new RemapRuleSet(decoded);
        reason = null;
        return true;
    }

    static void WriteHeader(Span<byte> destination, int ruleCount, byte compression, int uncompressedBytes, int storedBytes)
    {
        ContentPackFormat.RuleChunkMagic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ContentPackFormat.RuleChunkFormatVersion);
        destination[6] = compression;
        destination[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)ruleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)uncompressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], (uint)storedBytes);
    }
}
