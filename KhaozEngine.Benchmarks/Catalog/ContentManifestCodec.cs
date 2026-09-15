using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The <c>KECM</c> manifest file of spec section 7.4. Hashes are RAW 32 bytes in the file and lower hex
/// only where a hash appears as text, which is contracts 15's rule and which halves the manifest.
/// </summary>
public static class ContentManifestCodec
{
    public static readonly byte[] Magic = "KECM"u8.ToArray();

    public const int FixedHeaderBytes = 56;

    public static byte[] Encode(ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var buffer = new List<byte>(FixedHeaderBytes + manifest.TotalChunkCount() * 40 + 256);
        Span<byte> scratch = stackalloc byte[8];

        buffer.AddRange(Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(scratch, ContentPackFormat.ManifestFormatVersion);
        buffer.AddRange(scratch[..2]);
        buffer.Add(manifest.Side);
        buffer.Add(0);
        AddUInt32(buffer, scratch, (uint)manifest.VersionNumber);
        AddUInt32(buffer, scratch, (uint)manifest.FormatGeneration);
        AddUInt32(buffer, scratch, (uint)manifest.MinimumServerBuild);
        AddUInt32(buffer, scratch, (uint)manifest.MinimumClientBuild);
        buffer.AddRange(Convert.FromHexString(manifest.RemapRuleChunkHash));
        AddVarint(buffer, (uint)manifest.Types.Count);

        foreach (ManifestTypeEntry type in manifest.Types)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(scratch, type.TypeId);
            buffer.AddRange(scratch[..2]);
            byte[] key = Encoding.UTF8.GetBytes(type.TypeKey);
            buffer.Add((byte)key.Length);
            buffer.AddRange(key);
            AddUInt32(buffer, scratch, (uint)type.ChunkSlots);
            buffer.Add(type.Visibility);
            AddVarint(buffer, (uint)type.Chunks.Count);
            foreach (ManifestChunkEntry chunk in type.Chunks)
            {
                AddVarint(buffer, (uint)chunk.ChunkIndex);
                AddVarint(buffer, (uint)chunk.UncompressedBytes);
                buffer.AddRange(Convert.FromHexString(chunk.Hash));
            }
        }

        AddVarint(buffer, (uint)manifest.Languages.Count);
        foreach (ManifestLanguageEntry language in manifest.Languages)
        {
            byte[] tag = Encoding.UTF8.GetBytes(language.Tag);
            buffer.Add((byte)tag.Length);
            buffer.AddRange(tag);
            buffer.AddRange(Convert.FromHexString(language.TextHash));
        }
        return buffer.ToArray();
    }

    /// <summary>Decodes a manifest file. Total: a reason token, never a throw.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> file, out ContentManifest? manifest, out string reason)
    {
        manifest = null;
        if (file.Length < FixedHeaderBytes) { reason = "manifest-truncated"; return false; }
        if (!file[..4].SequenceEqual(Magic)) { reason = "manifest-magic"; return false; }
        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (formatVersion != ContentPackFormat.ManifestFormatVersion) { reason = "manifest-format-version"; return false; }
        byte side = file[6];
        if (side > 1) { reason = "manifest-wrong-side"; return false; }
        if (file[7] != 0) { reason = "chunk-reserved-set"; return false; }
        int versionNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        int generation = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        int minimumServer = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        int minimumClient = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[20..]);
        string ruleHash = Convert.ToHexStringLower(file.Slice(24, 32));

        int cursor = FixedHeaderBytes;
        if (!ContentVarint.TryRead(file, ref cursor, out uint typeCount)) { reason = "manifest-type-count"; return false; }
        var types = new List<ManifestTypeEntry>((int)typeCount);
        for (uint t = 0; t < typeCount; t++)
        {
            if (cursor + 3 > file.Length) { reason = "manifest-truncated"; return false; }
            ushort typeId = BinaryPrimitives.ReadUInt16LittleEndian(file[cursor..]);
            cursor += 2;
            int keyLength = file[cursor++];
            if (keyLength is < 1 or > 64 || cursor + keyLength > file.Length) { reason = "manifest-type-key"; return false; }
            string typeKey = Encoding.UTF8.GetString(file.Slice(cursor, keyLength));
            cursor += keyLength;
            if (cursor + 5 > file.Length) { reason = "manifest-truncated"; return false; }
            int chunkSlots = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[cursor..]);
            cursor += 4;
            byte visibility = file[cursor++];
            if (!ContentVarint.TryRead(file, ref cursor, out uint chunkCount)) { reason = "manifest-chunk-count"; return false; }
            var chunks = new List<ManifestChunkEntry>((int)chunkCount);
            for (uint c = 0; c < chunkCount; c++)
            {
                if (!ContentVarint.TryRead(file, ref cursor, out uint index)) { reason = "manifest-chunk-index"; return false; }
                if (!ContentVarint.TryRead(file, ref cursor, out uint uncompressed)) { reason = "manifest-chunk-size"; return false; }
                if (cursor + 32 > file.Length) { reason = "manifest-truncated"; return false; }
                chunks.Add(new ManifestChunkEntry((int)index, (int)uncompressed, Convert.ToHexStringLower(file.Slice(cursor, 32))));
                cursor += 32;
            }
            types.Add(new ManifestTypeEntry(typeId, typeKey, chunkSlots, visibility, chunks));
        }

        if (!ContentVarint.TryRead(file, ref cursor, out uint languageCount)) { reason = "manifest-language-count"; return false; }
        var languages = new List<ManifestLanguageEntry>((int)languageCount);
        for (uint l = 0; l < languageCount; l++)
        {
            if (cursor >= file.Length) { reason = "manifest-truncated"; return false; }
            int tagLength = file[cursor++];
            if (tagLength is < 1 or > 35 || cursor + tagLength + 32 > file.Length) { reason = "manifest-language-tag"; return false; }
            string tag = Encoding.UTF8.GetString(file.Slice(cursor, tagLength));
            cursor += tagLength;
            languages.Add(new ManifestLanguageEntry(tag, Convert.ToHexStringLower(file.Slice(cursor, 32))));
            cursor += 32;
        }
        if (cursor != file.Length) { reason = "manifest-trailing-bytes"; return false; }

        manifest = new ContentManifest
        {
            Side = side,
            VersionNumber = versionNumber,
            FormatGeneration = generation,
            MinimumServerBuild = minimumServer,
            MinimumClientBuild = minimumClient,
            RemapRuleChunkHash = ruleHash,
            Types = types,
            Languages = languages,
        };
        reason = string.Empty;
        return true;
    }

    private static void AddUInt32(List<byte> buffer, Span<byte> scratch, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        buffer.AddRange(scratch[..4]);
    }

    private static void AddVarint(List<byte> buffer, uint value)
    {
        Span<byte> scratch = stackalloc byte[5];
        int written = ContentVarint.Write(scratch, value);
        for (int i = 0; i < written; i++) buffer.Add(scratch[i]);
    }
}
