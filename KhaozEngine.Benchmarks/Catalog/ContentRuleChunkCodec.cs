using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>One remap rule, contracts 8.4's encoding and not one byte more.</summary>
public readonly record struct ContentRemapRule(
    int Sequence,
    int IntroducedIn,
    ushort TypeId,
    byte Kind,
    int FromId,
    int ToId,
    byte[] Payload);

/// <summary>
/// The <c>KECR</c> remap rule chunk of spec section 7.7. ONE per manifest, the FULL rule list from
/// sequence 1 rather than a delta, identical in both manifests because a rule is ids and a kind and never
/// a value.
/// </summary>
public static class ContentRuleChunkCodec
{
    public static readonly byte[] Magic = "KECR"u8.ToArray();

    public static (byte[] StoredFile, string Hash) Encode(IReadOnlyList<ContentRemapRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var body = new List<byte>(rules.Count * 12 + 8);
        Span<byte> scratch = stackalloc byte[10];
        foreach (ContentRemapRule rule in rules)
        {
            AppendVarint(body, scratch, ContentVarint.ZigZag(rule.Sequence));
            AppendVarint(body, scratch, ContentVarint.ZigZag(rule.IntroducedIn));
            BinaryPrimitives.WriteUInt16LittleEndian(scratch, rule.TypeId);
            body.Add(scratch[0]);
            body.Add(scratch[1]);
            body.Add(rule.Kind);
            AppendVarint(body, scratch, ContentVarint.ZigZag(rule.FromId));
            AppendVarint(body, scratch, ContentVarint.ZigZag(rule.ToId));
            byte[] payload = rule.Payload ?? [];
            if (payload.Length > 64) throw new ArgumentException("A remap rule payload is at most 64 bytes.", nameof(rules));
            body.Add((byte)payload.Length);
            body.AddRange(payload);
        }

        byte[] bodyBytes = body.ToArray();
        byte[] canonical = new byte[ContentPackFormat.RuleHeaderBytes + bodyBytes.Length];
        WriteHeader(canonical, rules.Count, ContentPackFormat.CompressionNone, bodyBytes.Length, bodyBytes.Length);
        bodyBytes.CopyTo(canonical.AsSpan(ContentPackFormat.RuleHeaderBytes));
        string hash = ContentHash.OfRuleChunk(canonical);

        byte[] compressed = ContentChunkCodec.Compress(bodyBytes);
        if (compressed.Length >= bodyBytes.Length) return (canonical, hash);
        byte[] stored = new byte[ContentPackFormat.RuleHeaderBytes + compressed.Length];
        WriteHeader(stored, rules.Count, ContentPackFormat.CompressionBrotli, bodyBytes.Length, compressed.Length);
        compressed.CopyTo(stored.AsSpan(ContentPackFormat.RuleHeaderBytes));
        return (stored, hash);
    }

    /// <summary>Decodes a rule chunk. A sequence gap, disorder or an unknown kind is a refusal, not a skip.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> file, out List<ContentRemapRule>? rules, out string reason)
    {
        rules = null;
        if (file.Length < ContentPackFormat.RuleHeaderBytes) { reason = "rule-truncated-header"; return false; }
        if (!file[..4].SequenceEqual(Magic)) { reason = "rule-magic"; return false; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[4..]) != ContentPackFormat.RuleChunkFormatVersion)
        { reason = "rule-format-version"; return false; }
        byte compression = file[6];
        if (file[7] != 0) { reason = "chunk-reserved-set"; return false; }
        uint ruleCount = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        uint storedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        if (uncompressed > ContentPackFormat.MaxChunkUncompressedBytes) { reason = "chunk-too-large"; return false; }
        if (storedBytes != file.Length - ContentPackFormat.RuleHeaderBytes) { reason = "chunk-stored-length"; return false; }

        byte[] body = new byte[uncompressed];
        ReadOnlySpan<byte> storedBody = file[ContentPackFormat.RuleHeaderBytes..];
        if (compression == ContentPackFormat.CompressionNone) storedBody.CopyTo(body);
        else if (!BrotliDecoder.TryDecompress(storedBody, body, out int written) || written != uncompressed)
        { reason = "chunk-decompress"; return false; }

        var decoded = new List<ContentRemapRule>((int)ruleCount);
        int cursor = 0;
        int expectedSequence = 1;
        for (uint i = 0; i < ruleCount; i++)
        {
            if (!ContentVarint.TryReadSigned(body, ref cursor, out int sequence)) { reason = "rule-truncated"; return false; }
            if (sequence != expectedSequence) { reason = sequence < expectedSequence ? "rule-sequence-order" : "rule-sequence-gap"; return false; }
            expectedSequence++;
            if (!ContentVarint.TryReadSigned(body, ref cursor, out int introducedIn)) { reason = "rule-truncated"; return false; }
            if (cursor + 3 > body.Length) { reason = "rule-truncated"; return false; }
            ushort typeId = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(cursor));
            cursor += 2;
            byte kind = body[cursor++];
            if (kind is 0 or > 2) { reason = "rule-kind"; return false; }
            if (!ContentVarint.TryReadSigned(body, ref cursor, out int fromId)) { reason = "rule-truncated"; return false; }
            if (!ContentVarint.TryReadSigned(body, ref cursor, out int toId)) { reason = "rule-truncated"; return false; }
            if (cursor >= body.Length) { reason = "rule-truncated"; return false; }
            int payloadLength = body[cursor++];
            if (payloadLength > 64 || cursor + payloadLength > body.Length) { reason = "rule-payload"; return false; }
            byte[] payload = body.AsSpan(cursor, payloadLength).ToArray();
            cursor += payloadLength;
            decoded.Add(new ContentRemapRule(sequence, introducedIn, typeId, kind, fromId, toId, payload));
        }
        if (cursor != body.Length) { reason = "rule-trailing-bytes"; return false; }
        rules = decoded;
        reason = string.Empty;
        return true;
    }

    private static void AppendVarint(List<byte> body, Span<byte> scratch, uint value)
    {
        int written = ContentVarint.Write(scratch, value);
        for (int i = 0; i < written; i++) body.Add(scratch[i]);
    }

    private static void WriteHeader(Span<byte> destination, int ruleCount, byte compression, int uncompressed, int stored)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ContentPackFormat.RuleChunkFormatVersion);
        destination[6] = compression;
        destination[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)ruleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)uncompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], (uint)stored);
    }
}
