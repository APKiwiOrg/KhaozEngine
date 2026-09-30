using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>Reads the complete object closure of two manifests without consulting a mutable version pointer.</summary>
public static class ContentPackClosure
{
    /// <summary>
    /// Lists both manifest hashes and every chunk, rule and text hash either manifest names, without duplicates.
    /// Verifies fetched bytes against each expected hash before decoding. Returns an empty list when either
    /// manifest is absent, has a mismatched digest or cannot be decoded, never a partial closure.
    /// </summary>
    /// <param name="store">The content-addressed store holding the manifests.</param>
    /// <param name="serverManifestHash">The immutable server manifest address to read.</param>
    /// <param name="clientManifestHash">The immutable client manifest address to read.</param>
    /// <param name="cancellationToken">Cancels the manifest reads.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<IReadOnlyList<string>> ReadAsync(
        IPackStore store,
        string serverManifestHash,
        string clientManifestHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serverManifestHash);
        ArgumentNullException.ThrowIfNull(clientManifestHash);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new List<string>();
        Add(seen, hashes, serverManifestHash);
        Add(seen, hashes, clientManifestHash);

        bool read = await TryAddManifestAsync(
            store, serverManifestHash, ContentManifestSide.Server, seen, hashes, cancellationToken)
            .ConfigureAwait(false);
        if (read)
        {
            read = await TryAddManifestAsync(
                store, clientManifestHash, ContentManifestSide.Client, seen, hashes, cancellationToken)
                .ConfigureAwait(false);
        }

        // A partial closure would authorize a sweep to delete the unreadable manifest's live objects.
        return read ? hashes : [];
    }

    static async Task<bool> TryAddManifestAsync(
        IPackStore store,
        string hash,
        ContentManifestSide side,
        HashSet<string> seen,
        List<string> hashes,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? file = await store.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (file is null
            || !ContentPackReader.TryVerify(file.Value.Span, hash, out _)
            || !ContentManifestCodec.TryDecode(file.Value.Span, side, out ContentManifest? manifest, out _))
        {
            return false;
        }

        Add(seen, hashes, manifest.RemapRuleChunkHash);
        for (int t = 0; t < manifest.Types.Count; t++)
        {
            IReadOnlyList<ManifestChunkEntry> chunks = manifest.Types[t].Chunks;
            for (int c = 0; c < chunks.Count; c++)
            {
                Add(seen, hashes, chunks[c].Hash);
            }
        }

        for (int l = 0; l < manifest.Languages.Count; l++)
        {
            Add(seen, hashes, manifest.Languages[l].TextHash);
        }

        return true;
    }

    static void Add(HashSet<string> seen, List<string> hashes, string hash)
    {
        if (seen.Add(hash))
        {
            hashes.Add(hash);
        }
    }
}
