using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.Goldens;

/// <summary>
/// One decode-then-re-encode attempt over a standalone file.
/// </summary>
/// <param name="Decoded">True when the shipped decoder accepted the bytes.</param>
/// <param name="Reason">The stable refusal token, or null when it decoded.</param>
/// <param name="Canonical">The file's OWN canonical bytes, decompressed, or null when it refused.</param>
/// <param name="ReEncoded">What re-encoding the decoded values produced, or null when it refused.</param>
/// <param name="Stage">Which half a throw came out of, for an assertion message that names it.</param>
internal sealed record GoldenRoundTrip(
    bool Decoded,
    string? Reason,
    byte[]? Canonical,
    byte[]? ReEncoded,
    string Stage);

/// <summary>
/// The shared decode, canonicalise and re-encode path the golden assertions and the decoder fuzzer both
/// run. It holds the ONE piece of format knowledge a test owns rather than reads: where each container puts
/// its <c>compression</c> byte and its two lengths, so a file's canonical bytes can be rebuilt from the file
/// itself rather than from what a decoder says about it. That independence is the point: a round trip
/// compared against bytes the decoder produced would agree with a decoder that normalised.
/// <para>
/// The layout here is checked against all six goldens on every run, because the recorded canonical length and
/// the recorded hash are both taken over what <see cref="TryCanonical"/> returns.
/// </para>
/// </summary>
internal static class GoldenCodecs
{
    /// <summary>The <c>KECC</c> chunk kind, as <c>goldens.json</c> names it.</summary>
    public const string ChunkKind = "chunk";

    /// <summary>The <c>KECM</c> manifest kind.</summary>
    public const string ManifestKind = "manifest";

    /// <summary>The <c>KECR</c> remap rule chunk kind.</summary>
    public const string RulesKind = "rules";

    /// <summary>The <c>KECT</c> per-language text chunk kind.</summary>
    public const string TextKind = "text";

    /// <summary>Which of the four formats a run of bytes is, by magic, or null for anything else.</summary>
    public static string? KindOf(ReadOnlySpan<byte> file)
    {
        if (file.Length < ContentPackFormat.MagicBytes)
        {
            return null;
        }

        ReadOnlySpan<byte> magic = file[..ContentPackFormat.MagicBytes];
        if (magic.SequenceEqual(ContentPackFormat.ChunkMagic)) return ChunkKind;
        if (magic.SequenceEqual(ContentPackFormat.ManifestMagic)) return ManifestKind;
        if (magic.SequenceEqual(ContentPackFormat.RuleChunkMagic)) return RulesKind;
        if (magic.SequenceEqual(ContentPackFormat.TextChunkMagic)) return TextKind;
        return null;
    }

    /// <summary>A registry carrying the six engine types, fresh, so no test shares one with another.</summary>
    public static ContentTypeRegistry EngineRegistry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        return registry;
    }

    /// <summary>
    /// The canonical uncompressed bytes of a stored file: the header with <c>compression</c> forced to 0 and
    /// <c>storedBytes</c> forced equal to <c>uncompressedBytes</c>, then the decompressed body. A manifest is
    /// never compressed, so its canonical bytes are the file. Returns false for a file whose container fields
    /// do not hold together, which is every file a decoder refuses for a size reason.
    /// </summary>
    public static bool TryCanonical(ReadOnlySpan<byte> file, out byte[] canonical)
    {
        canonical = [];
        string? kind = KindOf(file);
        if (kind is null)
        {
            return false;
        }

        if (kind == ManifestKind)
        {
            canonical = file.ToArray();
            return true;
        }

        // Spec 7.2, 7.6 and 7.7: each container puts its compression byte and its two lengths in its own
        // place, and the text header is variable because the language tag sits inside it.
        int headerBytes;
        int compressionAt;
        int uncompressedAt;
        switch (kind)
        {
            case ChunkKind:
                headerBytes = ContentPackFormat.ChunkHeaderBytes;
                compressionAt = 25;
                uncompressedAt = 28;
                break;
            case RulesKind:
                headerBytes = ContentPackFormat.RuleHeaderBytes;
                compressionAt = 6;
                uncompressedAt = 12;
                break;
            default:
                if (file.Length < 7) return false;
                int tagLength = file[6];
                headerBytes = ContentPackFormat.TextHeaderFixedBytes + tagLength;
                compressionAt = 7 + tagLength;
                uncompressedAt = 9 + tagLength;
                break;
        }

        int storedAt = uncompressedAt + 4;
        if (file.Length < headerBytes || storedAt + 4 > headerBytes)
        {
            return false;
        }

        uint uncompressedBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[uncompressedAt..]);
        if (uncompressedBytes > ContentPackFormat.MaxChunkUncompressedBytes)
        {
            return false;
        }

        byte[] body = new byte[uncompressedBytes];
        ReadOnlySpan<byte> stored = file[headerBytes..];
        switch (file[compressionAt])
        {
            case ContentPackFormat.CompressionNone:
                if (stored.Length != body.Length) return false;
                stored.CopyTo(body);
                break;
            case ContentPackFormat.CompressionBrotli:
                if (!BrotliDecoder.TryDecompress(stored, body, out int written) || written != body.Length) return false;
                break;
            default:
                return false;
        }

        canonical = new byte[headerBytes + body.Length];
        file[..headerBytes].CopyTo(canonical);
        canonical[compressionAt] = ContentPackFormat.CompressionNone;
        BinaryPrimitives.WriteUInt32LittleEndian(canonical.AsSpan(storedAt), uncompressedBytes);
        body.CopyTo(canonical.AsSpan(headerBytes));
        return true;
    }

    /// <summary>
    /// Decodes a file through the shipped decoder for its magic and re-encodes whatever came back. A file
    /// that decodes MUST re-encode to its own canonical bytes, which is what catches a decoder that
    /// normalises a difference away and so stops a canonical format being canonical.
    /// </summary>
    /// <param name="file">The stored bytes, compressed where the publisher compressed them.</param>
    /// <param name="side">The side a <c>KECM</c> is expected to be, which is part of its identity.</param>
    public static GoldenRoundTrip RoundTrip(byte[] file, ContentManifestSide side)
    {
        ArgumentNullException.ThrowIfNull(file);

        switch (KindOf(file))
        {
            case ChunkKind:
                return Chunk(file);
            case ManifestKind:
                return Manifest(file, side);
            case RulesKind:
                return Rules(file);
            case TextKind:
                return Text(file);
            default:
                return new GoldenRoundTrip(false, ContentChunkCodec.ReasonMagic, null, null, "magic");
        }
    }

    static GoldenRoundTrip Chunk(byte[] file)
    {
        if (!ContentChunkCodec.TryDecode(file, registry: null, out ContentChunk? chunk, out string? reason))
        {
            return new GoldenRoundTrip(false, reason, null, null, "decode");
        }

        if (!TryCanonical(file, out byte[] canonical))
        {
            return new GoldenRoundTrip(true, null, null, null, "canonical");
        }

        // A chunk carrying type id 0 cannot be re-encoded, because 0 is reserved and no registration may take
        // it, so there is no registration to hand the encoder. Every other id is registrable in its own band.
        if (chunk.Type.Value == 0)
        {
            return new GoldenRoundTrip(true, null, canonical, canonical, "type-zero");
        }

        var rows = new ContentChunkRow[chunk.RowCount];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new ContentChunkRow(chunk.DefinitionIdAt(i), chunk.IsRetiredAt(i), chunk.RowBodyAt(i).ToArray());
        }

        EncodedContentChunk encoded = ContentChunkCodec.Encode(
            Registration(chunk.Type, chunk.SlotCount),
            chunk.ChunkIndex,
            chunk.Visibility,
            rows);
        return new GoldenRoundTrip(true, null, canonical, encoded.Canonical.ToArray(), "re-encode");
    }

    static GoldenRoundTrip Manifest(byte[] file, ContentManifestSide side)
    {
        if (!ContentManifestCodec.TryDecode(file, side, out ContentManifest? manifest, out string? reason))
        {
            return new GoldenRoundTrip(false, reason, null, null, "decode");
        }

        return new GoldenRoundTrip(true, null, file, ContentManifestCodec.Encode(manifest), "re-encode");
    }

    static GoldenRoundTrip Rules(byte[] file)
    {
        if (!ContentRuleChunkCodec.TryDecode(file, out RemapRuleSet? rules, out string? reason))
        {
            return new GoldenRoundTrip(false, reason, null, null, "decode");
        }

        return !TryCanonical(file, out byte[] canonical)
            ? new GoldenRoundTrip(true, null, null, null, "canonical")
            : new GoldenRoundTrip(true, null, canonical, ContentRuleChunkCodec.Canonical(rules.Rules), "re-encode");
    }

    static GoldenRoundTrip Text(byte[] file)
    {
        if (!ContentTextChunkCodec.TryDecode(file, out ContentTextChunk? chunk, out string? reason))
        {
            return new GoldenRoundTrip(false, reason, null, null, "decode");
        }

        if (!TryCanonical(file, out byte[] canonical))
        {
            return new GoldenRoundTrip(true, null, null, null, "canonical");
        }

        // The decoded text chunk holds its canonical header and its body as BYTES, so the re-encode here is
        // that pair concatenated. The string-level re-encode is the goldens' own assertion, where every entry
        // is known to be valid UTF-8: a mutated entry need not be, and going through UTF-16 would substitute
        // a replacement character and fail a round trip the decoder never broke.
        byte[] reEncoded = new byte[chunk.CanonicalHeader.Length + chunk.Body.Length];
        chunk.CanonicalHeader.CopyTo(reEncoded);
        chunk.Body.CopyTo(reEncoded.AsSpan(chunk.CanonicalHeader.Length));
        return new GoldenRoundTrip(true, null, canonical, reEncoded, "re-encode");
    }

    static ContentTypeRegistration Registration(ContentTypeId type, int chunkSlots)
    {
        ContentRegistrationBand band = type.IsEngine
            ? ContentRegistrationBand.Engine
            : type.IsInstances ? ContentRegistrationBand.Instances : ContentRegistrationBand.Game;

        var schema = new ContentFieldSchema(Array.Empty<ContentFieldEntry>());
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            band,
            type.Value,
            "round-trip",
            new BlankCodec(type, schema),
            validator: null,
            schema,
            ContentVisibility.Client,
            chunkSlots,
            maxRowBytes: 1);
        _ = registry.TryGet(type, out ContentTypeRegistration? registration);
        return registration!;
    }

    /// <summary>
    /// A codec over an empty schema. The chunk encoder takes a registration for the type id and the slot
    /// count alone and never touches the codec, so re-encoding a decoded chunk needs no knowledge of what
    /// its rows mean: the row bodies go back verbatim.
    /// </summary>
    sealed class BlankCodec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema);
}
