using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Catalog;

/// <summary>
/// The canonical TEXT a manifest's two digests are taken over, spec 6.8 and contracts 7.3. It is NOT the
/// file layout of <see cref="ContentManifestCodec"/> and the two are deliberately kept apart: the file
/// carries a per-chunk <c>uncompressedBytes</c> the digest text does not, because contracts 7.3 fixes this
/// text down to the field order and the spec refines the file rather than contradicting the digest.
/// <para>
/// Every number goes through the invariant culture, because <c>StringBuilder.Append(int)</c> formats with
/// the CURRENT one and a negative number would digest differently under a culture with its own minus sign.
/// Every string is length prefixed, so a delimiter inside an authored key cannot make two different
/// manifests digest the same.
/// </para>
/// <para>
/// Collections are SORTED here rather than assumed sorted, which is contracts 7.3's rule and what makes the
/// digest independent of the order a publisher happened to walk its registry in (contracts 4.3). The FILE
/// has its own ordering rule and <see cref="ContentManifestCodec"/> enforces that one on the way in.
/// </para>
/// </summary>
public static class ContentManifestText
{
    /// <summary>Builds the canonical text, opening with this side's sub-domain and the scheme version.</summary>
    public static string Canonical(ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var builder = new StringBuilder(256 + (manifest.TotalChunkCount * 72));
        builder.Append(ContentHash.Domain(manifest.SubDomain));
        ContentHash.AppendNumber(builder, manifest.VersionNumber);
        builder.Append('\n');
        ContentHash.AppendNumber(builder, manifest.FormatGeneration);
        builder.Append('\n');
        ContentHash.AppendNumber(builder, manifest.MinimumServerBuild);
        builder.Append('\n');
        ContentHash.AppendNumber(builder, manifest.MinimumClientBuild);
        builder.Append('\n');

        IReadOnlyList<ManifestTypeEntry> types = Sorted(manifest.Types, static (a, b) => a.TypeId.CompareTo(b.TypeId));
        for (int t = 0; t < types.Count; t++)
        {
            ManifestTypeEntry type = types[t];
            ContentHash.AppendNumber(builder, type.TypeId);
            builder.Append(' ');
            ContentHash.AppendText(builder, type.TypeKey);
            builder.Append(' ');
            ContentHash.AppendNumber(builder, type.Chunks.Count);
            builder.Append('\n');

            IReadOnlyList<ManifestChunkEntry> chunks =
                Sorted(type.Chunks, static (a, b) => a.ChunkIndex.CompareTo(b.ChunkIndex));
            for (int c = 0; c < chunks.Count; c++)
            {
                ContentHash.AppendNumber(builder, chunks[c].ChunkIndex);
                builder.Append(' ');
                builder.Append(chunks[c].Hash);
                builder.Append('\n');
            }
        }

        ContentHash.AppendText(builder, manifest.RemapRuleChunkHash);
        builder.Append('\n');

        IReadOnlyList<ManifestLanguageEntry> languages =
            Sorted(manifest.Languages, static (a, b) => string.CompareOrdinal(a.Tag, b.Tag));
        for (int l = 0; l < languages.Count; l++)
        {
            ContentHash.AppendText(builder, languages[l].Tag);
            builder.Append(' ');
            builder.Append(languages[l].TextHash);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// This manifest's hash, under this side's sub-domain. The two sides of one version are never equal,
    /// even when their content is identical, because the sub-domain alone separates them.
    /// </summary>
    public static string Hash(ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string canonical = Canonical(manifest);
        return manifest.Side == ContentManifestSide.Server
            ? ContentHash.OfServerManifest(canonical)
            : ContentHash.OfClientManifest(canonical);
    }

    /// <summary>
    /// The input in order, copying and sorting only when it is not already in order, which is the ordinary
    /// case because a publish builds both manifests from an ascending walk.
    /// </summary>
    static IReadOnlyList<T> Sorted<T>(IReadOnlyList<T> source, Comparison<T> order)
    {
        for (int i = 1; i < source.Count; i++)
        {
            if (order(source[i - 1], source[i]) <= 0)
            {
                continue;
            }

            var copy = new T[source.Count];
            for (int j = 0; j < copy.Length; j++)
            {
                copy[j] = source[j];
            }

            Array.Sort(copy, order);
            return copy;
        }

        return source;
    }
}
