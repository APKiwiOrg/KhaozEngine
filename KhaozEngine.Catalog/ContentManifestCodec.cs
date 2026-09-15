using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;

namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>KECM</c> manifest FILE of spec 7.4. Hashes are RAW 32 bytes here and lower hex only where a hash
/// appears as text, which is contracts 15's rule and which halves the manifest.
/// <para>
/// The digest text is <see cref="ContentManifestText"/> and is a different thing from this layout. The
/// per-chunk <c>uncompressedBytes</c> is in the file and outside the digest, so the loader can size a
/// type's body buffer while holding a manifest and nothing else, and
/// <see cref="ContentManifest.TryMatchChunkHeader"/> is what binds it to reality.
/// </para>
/// <para>
/// <see cref="Encode"/> writes the entries in the ORDER IT IS HANDED THEM and validates neither the order
/// nor the slot counts, so a publisher sorts and a test can build a file this reader refuses.
/// The decode side is total: every rejection is false plus a stable reason token, never a throw,
/// because these bytes arrive from a remote peer.
/// </para>
/// </summary>
public static class ContentManifestCodec
{
    /// <summary>The fixed part, up to and including the remap rule chunk hash. The type list follows.</summary>
    public const int FixedHeaderBytes = 56;

    /// <summary>The first four bytes are not <c>KECM</c>.</summary>
    public const string ReasonMagic = "manifest-magic";

    /// <summary>The file's format version is not this reader's, which refuses the whole record.</summary>
    public const string ReasonFormatVersion = "manifest-format-version";

    /// <summary>The manifest's engine format generation is above this reader's (contracts 7.4).</summary>
    public const string ReasonFormatGeneration = "manifest-format-generation";

    /// <summary>The file ends inside a field.</summary>
    public const string ReasonTruncated = "manifest-truncated";

    /// <summary>The <c>side</c> byte is not the side the reader asked for, or names no side at all.</summary>
    public const string ReasonWrongSide = "manifest-wrong-side";

    /// <summary>The reserved byte is non-zero, the fail-closed rule for an extension a reader cannot skip.</summary>
    public const string ReasonReservedSet = "chunk-reserved-set";

    /// <summary>A type key is empty, over 64 bytes, past the end, or not valid UTF-8.</summary>
    public const string ReasonTypeKey = "manifest-type-key";

    /// <summary>Types are not strictly ascending by type id.</summary>
    public const string ReasonTypeOrder = "manifest-type-order";

    /// <summary>
    /// A type's slot count is one no registration could declare, or disagrees with the local one, which
    /// means the two sides disagree about what a chunk index means.
    /// </summary>
    public const string ReasonChunkSlots = "manifest-chunk-slots";

    /// <summary>A type's chunks are not strictly ascending by index.</summary>
    public const string ReasonChunkOrder = "manifest-chunk-order";

    /// <summary>A visibility byte names no level, or a client manifest names a server-only type.</summary>
    public const string ReasonVisibility = "manifest-visibility";

    /// <summary>A language tag is empty, over 35 bytes, past the end, or not valid UTF-8.</summary>
    public const string ReasonLanguageTag = "manifest-language-tag";

    /// <summary>Languages are not strictly ascending ordinal by tag.</summary>
    public const string ReasonLanguageOrder = "manifest-language-order";

    /// <summary>Bytes remain after the last language, so the file describes more than it carries.</summary>
    public const string ReasonTrailingBytes = "manifest-trailing-bytes";

    /// <summary>The widest type key the length byte may declare, contracts 5.3.</summary>
    public const int MaxTypeKeyBytes = 64;

    /// <summary>The widest BCP-47 language tag the length byte may declare.</summary>
    public const int MaxLanguageTagBytes = 35;

    const int HashBytes = 32;

    /// <summary>Writes the manifest file. Throws on a producer error, because an encoder is not a decoder.</summary>
    public static byte[] Encode(ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        byte[] file = new byte[MeasureFile(manifest)];
        Span<byte> destination = file;

        ContentPackFormat.ManifestMagic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ContentPackFormat.ManifestFormatVersion);
        destination[6] = (byte)manifest.Side;
        destination[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], manifest.VersionNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], manifest.FormatGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], manifest.MinimumServerBuild);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], manifest.MinimumClientBuild);
        WriteHash(destination[24..], manifest.RemapRuleChunkHash, nameof(manifest.RemapRuleChunkHash));

        int written = FixedHeaderBytes;
        written += ContentVarint.Write(destination[written..], (uint)manifest.Types.Count);
        for (int t = 0; t < manifest.Types.Count; t++)
        {
            ManifestTypeEntry type = manifest.Types[t];
            BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], type.TypeId);
            written += 2;
            destination[written++] = (byte)MeasureKey(type.TypeKey);
            written += Encoding.UTF8.GetBytes(type.TypeKey, destination[written..]);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[written..], (uint)type.ChunkSlots);
            written += 4;
            destination[written++] = (byte)type.Visibility;
            written += ContentVarint.Write(destination[written..], (uint)type.Chunks.Count);
            for (int c = 0; c < type.Chunks.Count; c++)
            {
                ManifestChunkEntry chunk = type.Chunks[c];
                written += ContentVarint.Write(destination[written..], chunk.ChunkIndex);
                written += ContentVarint.Write(destination[written..], chunk.UncompressedBytes);
                WriteHash(destination[written..], chunk.Hash, nameof(chunk.Hash));
                written += HashBytes;
            }
        }

        written += ContentVarint.Write(destination[written..], (uint)manifest.Languages.Count);
        for (int l = 0; l < manifest.Languages.Count; l++)
        {
            ManifestLanguageEntry language = manifest.Languages[l];
            destination[written++] = (byte)MeasureTag(language.Tag);
            written += Encoding.UTF8.GetBytes(language.Tag, destination[written..]);
            WriteHash(destination[written..], language.TextHash, nameof(language.TextHash));
            written += HashBytes;
        }

        return written == file.Length
            ? file
            : throw new InvalidOperationException(FormattableString.Invariant(
                $"The manifest encoder measured {file.Length} bytes and wrote {written}."));
    }

    /// <summary>Decodes a manifest file with no registry to check the slot counts against.</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> file,
        ContentManifestSide expectedSide,
        [MaybeNullWhen(false)] out ContentManifest manifest,
        out string? reason)
        => TryDecode(file, expectedSide, registry: null, out manifest, out reason);

    /// <summary>
    /// Decodes a manifest file, refusing anything the reader cannot trust. Total: false plus a stable reason
    /// token, never a throw.
    /// </summary>
    /// <param name="file">The whole file, which is refused when bytes remain after the last language.</param>
    /// <param name="expectedSide">The side the caller asked for. The other side is <see cref="ReasonWrongSide"/>.</param>
    /// <param name="registry">
    /// The local registry, or null. When it is supplied, a type it knows whose slot count disagrees with the
    /// manifest's is <see cref="ReasonChunkSlots"/>. A type it does NOT know is left to the loader, so this
    /// stays a format reader rather than a boot policy.
    /// </param>
    /// <param name="manifest">The decoded manifest, or null.</param>
    /// <param name="reason">The refusal token, or null on success.</param>
    public static bool TryDecode(
        ReadOnlySpan<byte> file,
        ContentManifestSide expectedSide,
        ContentTypeRegistry? registry,
        [MaybeNullWhen(false)] out ContentManifest manifest,
        out string? reason)
    {
        manifest = null;
        if (file.Length < FixedHeaderBytes) { reason = ReasonTruncated; return false; }
        if (!file[..ContentPackFormat.MagicBytes].SequenceEqual(ContentPackFormat.ManifestMagic)) { reason = ReasonMagic; return false; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[4..]) != ContentPackFormat.ManifestFormatVersion) { reason = ReasonFormatVersion; return false; }
        if (file[6] != (byte)expectedSide) { reason = ReasonWrongSide; return false; }
        if (file[7] != 0) { reason = ReasonReservedSet; return false; }

        uint versionNumber = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint formatGeneration = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        uint minimumServerBuild = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        uint minimumClientBuild = BinaryPrimitives.ReadUInt32LittleEndian(file[20..]);
        if (formatGeneration > (uint)ContentPackFormat.Generation) { reason = ReasonFormatGeneration; return false; }
        string remapRuleChunkHash = Convert.ToHexStringLower(file.Slice(24, HashBytes));

        int cursor = FixedHeaderBytes;
        if (!ContentVarint.TryRead(file, ref cursor, out uint typeCount, out reason)) { return false; }

        var types = new List<ManifestTypeEntry>((int)Math.Min(typeCount, 64));
        int previousTypeId = -1;
        for (uint t = 0; t < typeCount; t++)
        {
            if (!TryReadType(file, ref cursor, expectedSide, registry, ref previousTypeId, out ManifestTypeEntry? type, out reason))
            {
                return false;
            }

            types.Add(type);
        }

        if (!ContentVarint.TryRead(file, ref cursor, out uint languageCount, out reason)) { return false; }

        var languages = new List<ManifestLanguageEntry>((int)Math.Min(languageCount, 64));
        string? previousTag = null;
        for (uint l = 0; l < languageCount; l++)
        {
            if (cursor >= file.Length) { reason = ReasonTruncated; return false; }
            int tagLength = file[cursor++];
            if (tagLength is < 1 or > MaxLanguageTagBytes || cursor + tagLength > file.Length) { reason = ReasonLanguageTag; return false; }
            if (!TryUtf8(file.Slice(cursor, tagLength), out string? tag)) { reason = ReasonLanguageTag; return false; }
            cursor += tagLength;
            if (previousTag is not null && string.CompareOrdinal(previousTag, tag) >= 0) { reason = ReasonLanguageOrder; return false; }
            previousTag = tag;
            if (cursor + HashBytes > file.Length) { reason = ReasonTruncated; return false; }
            languages.Add(new ManifestLanguageEntry(tag, Convert.ToHexStringLower(file.Slice(cursor, HashBytes))));
            cursor += HashBytes;
        }

        if (cursor != file.Length) { reason = ReasonTrailingBytes; return false; }

        manifest = new ContentManifest
        {
            Side = expectedSide,
            VersionNumber = versionNumber,
            FormatGeneration = formatGeneration,
            MinimumServerBuild = minimumServerBuild,
            MinimumClientBuild = minimumClientBuild,
            RemapRuleChunkHash = remapRuleChunkHash,
            Types = types,
            Languages = languages,
        };
        reason = null;
        return true;
    }

    static bool TryReadType(
        ReadOnlySpan<byte> file,
        ref int cursor,
        ContentManifestSide expectedSide,
        ContentTypeRegistry? registry,
        ref int previousTypeId,
        [MaybeNullWhen(false)] out ManifestTypeEntry type,
        out string? reason)
    {
        type = null;
        if (cursor + 3 > file.Length) { reason = ReasonTruncated; return false; }
        ushort typeId = BinaryPrimitives.ReadUInt16LittleEndian(file[cursor..]);
        cursor += 2;
        if (typeId <= previousTypeId) { reason = ReasonTypeOrder; return false; }
        previousTypeId = typeId;

        int keyLength = file[cursor++];
        if (keyLength is < 1 or > MaxTypeKeyBytes || cursor + keyLength > file.Length) { reason = ReasonTypeKey; return false; }
        if (!TryUtf8(file.Slice(cursor, keyLength), out string? typeKey)) { reason = ReasonTypeKey; return false; }
        cursor += keyLength;

        if (cursor + 5 > file.Length) { reason = ReasonTruncated; return false; }
        uint declaredSlots = BinaryPrimitives.ReadUInt32LittleEndian(file[cursor..]);
        cursor += 4;
        if (declaredSlots < ContentTypeRegistry.MinChunkSlots
            || declaredSlots > ContentTypeRegistry.MaxChunkSlots
            || (declaredSlots & (declaredSlots - 1)) != 0)
        {
            reason = ReasonChunkSlots;
            return false;
        }

        int chunkSlots = (int)declaredSlots;
        if (registry is not null
            && registry.TryGet(new ContentTypeId(typeId), out ContentTypeRegistration? registration)
            && registration.ChunkSlots != chunkSlots)
        {
            reason = ReasonChunkSlots;
            return false;
        }

        byte visibility = file[cursor++];
        if (visibility > (byte)ContentVisibility.ServerOnly) { reason = ReasonVisibility; return false; }
        if (expectedSide == ContentManifestSide.Client && visibility == (byte)ContentVisibility.ServerOnly)
        {
            // Contracts 11.3: the client manifest OMITS every server-only chunk, so a client file naming one
            // is refused rather than filtered on the way in.
            reason = ReasonVisibility;
            return false;
        }

        if (!ContentVarint.TryRead(file, ref cursor, out uint chunkCount, out reason)) { return false; }

        var chunks = new List<ManifestChunkEntry>((int)Math.Min(chunkCount, 256));
        long previousIndex = -1;
        for (uint c = 0; c < chunkCount; c++)
        {
            if (!ContentVarint.TryRead(file, ref cursor, out uint chunkIndex, out reason)) { return false; }
            if (chunkIndex <= previousIndex) { reason = ReasonChunkOrder; return false; }
            previousIndex = chunkIndex;
            if (!ContentVarint.TryRead(file, ref cursor, out uint uncompressedBytes, out reason)) { return false; }
            if (cursor + HashBytes > file.Length) { reason = ReasonTruncated; return false; }
            chunks.Add(new ManifestChunkEntry(chunkIndex, uncompressedBytes, Convert.ToHexStringLower(file.Slice(cursor, HashBytes))));
            cursor += HashBytes;
        }

        type = new ManifestTypeEntry(typeId, typeKey, chunkSlots, (ContentVisibility)visibility, chunks);
        reason = null;
        return true;
    }

    /// <summary>
    /// UTF-16 from UTF-8 without throwing and without silently substituting. A decoder that replaced an
    /// invalid sequence would re-encode to different bytes, which is exactly the normalisation that stops a
    /// canonical format being canonical.
    /// </summary>
    static bool TryUtf8(ReadOnlySpan<byte> bytes, [MaybeNullWhen(false)] out string text)
    {
        Span<char> buffer = bytes.Length <= 128 ? stackalloc char[128] : new char[bytes.Length];
        OperationStatus status = Utf8.ToUtf16(bytes, buffer, out int read, out int written, replaceInvalidSequences: false);
        if (status != OperationStatus.Done || read != bytes.Length)
        {
            text = null;
            return false;
        }

        text = new string(buffer[..written]);
        return true;
    }

    static int MeasureFile(ContentManifest manifest)
    {
        int size = FixedHeaderBytes + ContentVarint.Size((uint)manifest.Types.Count);
        for (int t = 0; t < manifest.Types.Count; t++)
        {
            ManifestTypeEntry type = manifest.Types[t];
            size += 2 + 1 + MeasureKey(type.TypeKey) + 4 + 1 + ContentVarint.Size((uint)type.Chunks.Count);
            for (int c = 0; c < type.Chunks.Count; c++)
            {
                size += ContentVarint.Size(type.Chunks[c].ChunkIndex)
                    + ContentVarint.Size(type.Chunks[c].UncompressedBytes)
                    + HashBytes;
            }
        }

        size += ContentVarint.Size((uint)manifest.Languages.Count);
        for (int l = 0; l < manifest.Languages.Count; l++)
        {
            size += 1 + MeasureTag(manifest.Languages[l].Tag) + HashBytes;
        }

        return size;
    }

    static int MeasureKey(string typeKey)
    {
        ArgumentNullException.ThrowIfNull(typeKey);
        int bytes = Encoding.UTF8.GetByteCount(typeKey);
        return bytes is >= 1 and <= MaxTypeKeyBytes
            ? bytes
            : throw new ArgumentException(FormattableString.Invariant(
                $"A manifest type key is 1 to {MaxTypeKeyBytes} UTF-8 bytes, and '{typeKey}' is {bytes}."), nameof(typeKey));
    }

    static int MeasureTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        int bytes = Encoding.UTF8.GetByteCount(tag);
        return bytes is >= 1 and <= MaxLanguageTagBytes
            ? bytes
            : throw new ArgumentException(FormattableString.Invariant(
                $"A manifest language tag is 1 to {MaxLanguageTagBytes} UTF-8 bytes, and '{tag}' is {bytes}."), nameof(tag));
    }

    static void WriteHash(Span<byte> destination, string lowerHex, string field)
    {
        ArgumentNullException.ThrowIfNull(lowerHex);
        if (lowerHex.Length != HashBytes * 2)
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"A manifest carries every hash as {HashBytes} raw bytes, so {field} must be {HashBytes * 2} hex characters and is {lowerHex.Length}."));
        }

        OperationStatus status = Convert.FromHexString(lowerHex, destination[..HashBytes], out _, out int decoded);
        if (status != OperationStatus.Done || decoded != HashBytes)
        {
            throw new ArgumentException(FormattableString.Invariant($"{field} is not hexadecimal."));
        }
    }
}
