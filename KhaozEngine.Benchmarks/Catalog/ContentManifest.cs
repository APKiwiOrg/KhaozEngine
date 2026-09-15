using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

public sealed record ManifestChunkEntry(int ChunkIndex, int UncompressedBytes, string Hash);

public sealed record ManifestTypeEntry(
    ushort TypeId,
    string TypeKey,
    int ChunkSlots,
    byte Visibility,
    IReadOnlyList<ManifestChunkEntry> Chunks);

public sealed record ManifestLanguageEntry(string Tag, string TextHash);

/// <summary>
/// One side's manifest: the version identity, the two consumer build ordinals, the engine's format
/// generation, every type sorted by type id with its chunks in index order, and every language. Its hash
/// is over the canonical TEXT of spec section 6.8, under its own side's sub-domain.
/// </summary>
public sealed class ContentManifest
{
    public required byte Side { get; init; }
    public required int VersionNumber { get; init; }
    public required int FormatGeneration { get; init; }
    public required int MinimumServerBuild { get; init; }
    public required int MinimumClientBuild { get; init; }
    public required string RemapRuleChunkHash { get; init; }
    public required IReadOnlyList<ManifestTypeEntry> Types { get; init; }
    public required IReadOnlyList<ManifestLanguageEntry> Languages { get; init; }

    public bool IsServer => Side == 1;

    public string SubDomain => IsServer ? ContentHash.ServerManifestDomain : ContentHash.ClientManifestDomain;

    /// <summary>The canonical manifest text of section 6.8, length prefixed and invariant formatted.</summary>
    public string CanonicalText()
    {
        var builder = new StringBuilder(1024);
        builder.Append(ContentHash.Domain(SubDomain));
        ContentHash.AppendNumber(builder, VersionNumber);
        builder.Append('\n');
        ContentHash.AppendNumber(builder, FormatGeneration);
        builder.Append('\n');
        ContentHash.AppendNumber(builder, MinimumServerBuild);
        builder.Append('\n');
        ContentHash.AppendNumber(builder, MinimumClientBuild);
        builder.Append('\n');
        foreach (ManifestTypeEntry type in Types)
        {
            ContentHash.AppendNumber(builder, type.TypeId);
            builder.Append(' ');
            ContentHash.AppendText(builder, type.TypeKey);
            builder.Append(' ');
            ContentHash.AppendNumber(builder, type.Chunks.Count);
            builder.Append('\n');
            foreach (ManifestChunkEntry chunk in type.Chunks)
            {
                ContentHash.AppendNumber(builder, chunk.ChunkIndex);
                builder.Append(' ');
                builder.Append(chunk.Hash);
                builder.Append('\n');
            }
        }
        ContentHash.AppendText(builder, RemapRuleChunkHash);
        builder.Append('\n');
        foreach (ManifestLanguageEntry language in Languages)
        {
            ContentHash.AppendText(builder, language.Tag);
            builder.Append(' ');
            builder.Append(language.TextHash);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    public string ComputeHash() => ContentHash.OfManifestText(CanonicalText());

    public int TotalChunkCount()
    {
        int total = 0;
        foreach (ManifestTypeEntry type in Types) total += type.Chunks.Count;
        return total;
    }

    /// <summary>Every hash this manifest names, in the order a fetch loop would walk them.</summary>
    public List<string> EveryChunkHash()
    {
        var hashes = new List<string>(TotalChunkCount() + Languages.Count);
        foreach (ManifestTypeEntry type in Types)
        {
            foreach (ManifestChunkEntry chunk in type.Chunks) hashes.Add(chunk.Hash);
        }
        foreach (ManifestLanguageEntry language in Languages) hashes.Add(language.TextHash);
        return hashes;
    }
}
