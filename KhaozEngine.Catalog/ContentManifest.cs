using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// Which of a version's two manifests a file is, spec 7.4. It is in the FILE and in the hash sub-domain
/// both, so a client manifest and a server manifest of one version can never be confused for each other in
/// either direction.
/// </summary>
public enum ContentManifestSide
{
    /// <summary>The manifest a client is served, carrying the client-visible chunks only.</summary>
    Client = 0,

    /// <summary>The manifest the server boots from, carrying every chunk.</summary>
    Server = 1,
}

/// <summary>One chunk of one content type, as the manifest names it.</summary>
/// <param name="ChunkIndex">The chunk's index within its type, so its id range is <c>index * slots</c>.</param>
/// <param name="UncompressedBytes">
/// The chunk's uncompressed length. It is in the FILE and NOT in the canonical text (spec 7.4), because the
/// loader sizes a type's concatenated body buffer from the sum of it while holding a manifest and nothing
/// else. <see cref="ContentManifest.TryMatchChunkHeader"/> is what binds it to reality.
/// </param>
/// <param name="Hash">The chunk's content address, lower hex, raw 32 bytes in the file.</param>
public sealed record ManifestChunkEntry(uint ChunkIndex, uint UncompressedBytes, string Hash);

/// <summary>One content type, as the manifest names it.</summary>
/// <param name="TypeId">The stable numeric type id. Types appear ascending by it.</param>
/// <param name="TypeKey">The stable string type key, 1 to 64 UTF-8 bytes.</param>
/// <param name="ChunkSlots">
/// Id slots per chunk. It is carried per type so a reader validates a chunk's declared range against the
/// MANIFEST rather than against its own registry, which matters for a client that loaded a pack produced by
/// a server whose registry it cannot see.
/// </param>
/// <param name="Visibility">Whether a client ever holds this type's chunks.</param>
/// <param name="Chunks">The type's chunks, ascending by index.</param>
public sealed record ManifestTypeEntry(
    ushort TypeId,
    string TypeKey,
    int ChunkSlots,
    ContentVisibility Visibility,
    IReadOnlyList<ManifestChunkEntry> Chunks);

/// <summary>One language's text chunk, as the manifest names it.</summary>
/// <param name="Tag">The BCP-47 language tag, 1 to 35 UTF-8 bytes. Languages appear ascending ordinal by it.</param>
/// <param name="TextHash">The text chunk's content address, lower hex.</param>
public sealed record ManifestLanguageEntry(string Tag, string TextHash);

/// <summary>
/// One side's manifest: the version identity, the two consumer build ordinals, the engine's own format
/// generation, every content type with its chunks, and every language (spec 6.8, 7.4).
/// <para>
/// A version has TWO of these, a server manifest over every chunk and a client manifest over the
/// client-visible chunks only (contracts 7.3, 11.3), and each takes its own hash sub-domain so a head
/// gating on one can never accidentally agree with a head gating on the other.
/// </para>
/// <para>
/// The hash is over the canonical TEXT of <see cref="ContentManifestText"/> and never over the file bytes,
/// which is why <see cref="ContentHash.OfBytesForKind"/> has no answer for a <c>KECM</c>.
/// </para>
/// </summary>
public sealed record ContentManifest
{
    /// <summary>
    /// A chunk whose own header disagrees with what the manifest says about it, spec 7.4. The same token as
    /// a chunk whose declared slot range disagrees, because both mean the reader cannot trust which ids the
    /// bytes in front of it describe.
    /// </summary>
    public const string ReasonChunkRangeMismatch = "chunk-range-mismatch";

    /// <summary>Which side this manifest is, in the file and in the hash sub-domain both.</summary>
    public required ContentManifestSide Side { get; init; }

    /// <summary>The content version number, which is also the durable page stamp (contracts 7.2).</summary>
    public required uint VersionNumber { get; init; }

    /// <summary>
    /// The ENGINE's own format generation at publish time, <see cref="ContentPackFormat.Generation"/>. It is
    /// never supplied by a consumer, and a reader whose own generation is below it refuses.
    /// </summary>
    public required uint FormatGeneration { get; init; }

    /// <summary>The consumer-supplied build ordinal a server must be at or above to load this version.</summary>
    public required uint MinimumServerBuild { get; init; }

    /// <summary>The consumer-supplied build ordinal a client must be at or above to join on this version.</summary>
    public required uint MinimumClientBuild { get; init; }

    /// <summary>The <c>KECR</c> rule chunk's content address, lower hex. Identical in both sides.</summary>
    public required string RemapRuleChunkHash { get; init; }

    /// <summary>Every content type this side carries, ascending by type id in the file.</summary>
    public required IReadOnlyList<ManifestTypeEntry> Types { get; init; }

    /// <summary>Every language this version ships text for, ascending ordinal by tag in the file.</summary>
    public required IReadOnlyList<ManifestLanguageEntry> Languages { get; init; }

    /// <summary>The digest sub-domain this side is hashed under, which is never the other side's.</summary>
    public string SubDomain =>
        Side == ContentManifestSide.Server ? ContentHash.ServerManifestDomain : ContentHash.ClientManifestDomain;

    /// <summary>Every chunk this manifest names, across every type.</summary>
    public int TotalChunkCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Types.Count; i++)
            {
                total += Types[i].Chunks.Count;
            }

            return total;
        }
    }

    /// <summary>
    /// Checks a chunk file's own header against what this manifest says about that chunk, which is the ONE
    /// refusal binding the un-digested <see cref="ManifestChunkEntry.UncompressedBytes"/> to reality (spec
    /// 7.4). A wrong size in a manifest therefore costs one refusal at the chunk that disagrees with it,
    /// never a silent short buffer.
    /// <para>
    /// A chunk this manifest never named takes the same refusal, because a reader that accepted it would be
    /// loading rows no manifest hash covers.
    /// </para>
    /// </summary>
    public bool TryMatchChunkHeader(ContentTypeId type, uint chunkIndex, uint uncompressedBytes, out string? reason)
    {
        for (int t = 0; t < Types.Count; t++)
        {
            if (Types[t].TypeId != type.Value)
            {
                continue;
            }

            IReadOnlyList<ManifestChunkEntry> chunks = Types[t].Chunks;
            for (int c = 0; c < chunks.Count; c++)
            {
                if (chunks[c].ChunkIndex != chunkIndex)
                {
                    continue;
                }

                if (chunks[c].UncompressedBytes != uncompressedBytes)
                {
                    reason = ReasonChunkRangeMismatch;
                    return false;
                }

                reason = null;
                return true;
            }
        }

        reason = ReasonChunkRangeMismatch;
        return false;
    }
}
