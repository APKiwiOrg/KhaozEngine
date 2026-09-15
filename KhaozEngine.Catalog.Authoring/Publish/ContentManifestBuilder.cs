using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Step 8 of spec 6.1: TWO manifests per version, the server one over every chunk and the client one over
/// the client-visible chunks only (contracts 7.3, 11.3).
/// <para>
/// <b>Each is built by reading the version's chunk rows and taking ONE side.</b> The server manifest takes
/// the server row where it exists and the client row otherwise, and the client manifest takes the client row
/// and names nothing for a type that has none. Neither recomputes a side from the type's default visibility,
/// for the same reason the carry forward does not.
/// </para>
/// <para>
/// <b>The minimum builds and the format generation are INPUTS to the hash, not stamps beside it.</b> A
/// publisher who raises <c>MinimumClientBuild</c> without touching a row publishes a version with a
/// different manifest hash and identical chunk hashes, so a client re-reads one small manifest and downloads
/// nothing. That is the correct behaviour and it falls out of putting the three numbers inside the digest.
/// </para>
/// </summary>
static class ContentManifestBuilder
{
    /// <summary>Builds one side's manifest from the version's chunk rows.</summary>
    /// <param name="side">Which manifest this is, which is also its hash sub-domain.</param>
    /// <param name="registry">The registry the type keys and slot counts come from.</param>
    /// <param name="chunks">Every chunk row this version holds, at every side.</param>
    /// <param name="languages">Every language this version ships text for.</param>
    /// <param name="versionNumber">The version number.</param>
    /// <param name="minimumServerBuild">The consumer-supplied minimum server build.</param>
    /// <param name="minimumClientBuild">The consumer-supplied minimum client build.</param>
    /// <param name="remapRuleChunkHash">The rule chunk's content address, identical in both sides.</param>
    /// <exception cref="ContentAuthoringException">A chunk row names a type the registry does not declare.</exception>
    public static ContentManifest Build(
        ContentManifestSide side,
        ContentTypeRegistry registry,
        IReadOnlyList<ContentChunkRecord> chunks,
        IReadOnlyList<ManifestLanguageEntry> languages,
        int versionNumber,
        int minimumServerBuild,
        int minimumClientBuild,
        string remapRuleChunkHash)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(remapRuleChunkHash);

        var byType = new SortedDictionary<ushort, SortedDictionary<int, ContentChunkRecord>>();
        for (int i = 0; i < chunks.Count; i++)
        {
            ContentChunkRecord chunk = chunks[i];
            if (!Takes(side, chunk.Side))
            {
                continue;
            }

            if (!byType.TryGetValue(chunk.Type.Value, out SortedDictionary<int, ContentChunkRecord>? held))
            {
                held = [];
                byType.Add(chunk.Type.Value, held);
            }

            // The server side prefers the server row where a chunk has both, which is the one case two rows
            // of one chunk reach here at all.
            if (!held.TryGetValue(chunk.ChunkIndex, out ContentChunkRecord? standing)
                || Beats(side, chunk.Side, standing.Side))
            {
                held[chunk.ChunkIndex] = chunk;
            }
        }

        var types = new List<ManifestTypeEntry>(byType.Count);
        foreach (KeyValuePair<ushort, SortedDictionary<int, ContentChunkRecord>> pair in byType)
        {
            ContentTypeRegistration registration = RequireType(registry, pair.Key);
            var entries = new List<ManifestChunkEntry>(pair.Value.Count);
            foreach (KeyValuePair<int, ContentChunkRecord> chunk in pair.Value)
            {
                entries.Add(new ManifestChunkEntry(
                    (uint)chunk.Key, (uint)chunk.Value.UncompressedBytes, chunk.Value.Hash));
            }

            types.Add(new ManifestTypeEntry(
                registration.Type.Value,
                registration.TypeKey,
                registration.ChunkSlots,
                registration.DefaultVisibility,
                entries));
        }

        var ordered = new List<ManifestLanguageEntry>(languages);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Tag, right.Tag));

        return new ContentManifest
        {
            Side = side,
            VersionNumber = (uint)versionNumber,
            FormatGeneration = (uint)ContentPackFormat.Generation,
            MinimumServerBuild = (uint)minimumServerBuild,
            MinimumClientBuild = (uint)minimumClientBuild,
            RemapRuleChunkHash = remapRuleChunkHash,
            Types = types,
            Languages = ordered,
        };
    }

    /// <summary>
    /// Whether a manifest side carries a chunk of that side at all. The CLIENT manifest omits every server
    /// chunk, which is what keeps a drop table off a client, and the server manifest carries both because a
    /// single-sided client chunk is the only row that chunk has.
    /// </summary>
    static bool Takes(ContentManifestSide side, ContentVisibility chunkSide)
        => side == ContentManifestSide.Server || chunkSide == ContentVisibility.Client;

    /// <summary>Whether a second row of one chunk replaces the one already taken for this side.</summary>
    static bool Beats(ContentManifestSide side, ContentVisibility candidate, ContentVisibility standing)
        => side == ContentManifestSide.Server
            && candidate == ContentVisibility.ServerOnly
            && standing == ContentVisibility.Client;

    static ContentTypeRegistration RequireType(ContentTypeRegistry registry, ushort typeId)
        => registry.TryGet(new ContentTypeId(typeId), out ContentTypeRegistration? registration)
            ? registration
            : throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {typeId} holds a chunk in this version and is not registered, so the manifest cannot name its key or its slot count."),
                new ContentTypeId(typeId),
                0,
                ContentAuthoringException.UnknownTypeReason);
}
