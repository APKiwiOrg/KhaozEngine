using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Step 8 of spec 6.1: TWO manifests per version, the server one over every chunk and the client one over
/// the client-visible chunks only (contracts 7.3, 11.3).
/// <para>
/// <b>The type list is the REGISTRY's, and the chunk list is the version's.</b> Every registered type is
/// named, carrying no chunks when it authored no rows, because boot refuses a version whose manifest does not
/// name a type this build registers (spec 9.6, the step 6 row). Building the type list out of the chunks
/// instead made an empty registered type indistinguishable from a type this pack predates, and the six engine
/// types are always registered while almost no game authors rows for all six, so <c>base_socket</c> alone made
/// a real pack unbootable. The manifest hash therefore covers the REGISTRATION SET, which is what gives that
/// refusal its meaning.
/// </para>
/// <para>
/// <b>Each type's chunks are read off the version's chunk rows, taking ONE side.</b> The server manifest takes
/// the server row where it exists and the client row otherwise, and the client manifest takes the client row
/// and names no chunk for a type that has none. Neither recomputes a CHUNK's side from the type's default
/// visibility, for the same reason the carry forward does not. The one place the default visibility decides
/// anything is which types the CLIENT manifest names at all: a <c>ServerOnly</c> type is omitted outright
/// (contracts 11.3), which is a different rule from the empty type above and is why the client side cannot
/// simply mirror the server's list.
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
    /// <summary>Builds one side's manifest over the registered types and the version's chunk rows.</summary>
    /// <param name="side">Which manifest this is, which is also its hash sub-domain.</param>
    /// <param name="registry">The registry the named types, their keys and their slot counts come from.</param>
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

        RequireEveryChunkTypeRegistered(registry, byType);

        // The registry is sorted ascending by type id always (contracts 4.3), so this walk emits the ascending
        // order the file demands without sorting anything, whatever order the publisher registered in.
        IReadOnlyList<ContentTypeRegistration> registered = registry.ByTypeId;
        var types = new List<ManifestTypeEntry>(registered.Count);
        for (int i = 0; i < registered.Count; i++)
        {
            ContentTypeRegistration registration = registered[i];
            if (!Names(side, registration.DefaultVisibility))
            {
                continue;
            }

            _ = byType.TryGetValue(registration.Type.Value, out SortedDictionary<int, ContentChunkRecord>? held);
            var entries = new List<ManifestChunkEntry>(held?.Count ?? 0);
            if (held is not null)
            {
                foreach (KeyValuePair<int, ContentChunkRecord> chunk in held)
                {
                    entries.Add(new ManifestChunkEntry(
                        (uint)chunk.Key, (uint)chunk.Value.UncompressedBytes, chunk.Value.Hash));
                }
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

    /// <summary>
    /// Whether this side's manifest names a registered type at all. The CLIENT manifest omits a
    /// <c>ServerOnly</c> TYPE outright (contracts 11.3), which is the one thing it drops that the server keeps.
    /// </summary>
    static bool Names(ContentManifestSide side, ContentVisibility typeVisibility)
        => side == ContentManifestSide.Server || typeVisibility != ContentVisibility.ServerOnly;

    /// <summary>
    /// Refuses a chunk row whose type the registry does not declare, BEFORE the registry walk emits the type
    /// list. The walk alone would drop such a chunk silently, publishing a version carrying bytes no manifest
    /// names, which is the one failure this check exists to make loud.
    /// </summary>
    static void RequireEveryChunkTypeRegistered(
        ContentTypeRegistry registry,
        SortedDictionary<ushort, SortedDictionary<int, ContentChunkRecord>> byType)
    {
        foreach (ushort typeId in byType.Keys)
        {
            if (registry.TryGet(new ContentTypeId(typeId), out _))
            {
                continue;
            }

            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {typeId} holds a chunk in this version and is not registered, so the manifest cannot name its key or its slot count."),
                new ContentTypeId(typeId),
                0,
                ContentAuthoringException.UnknownTypeReason);
        }
    }
}
