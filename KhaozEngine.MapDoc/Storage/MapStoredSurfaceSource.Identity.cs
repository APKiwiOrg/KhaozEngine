using System.Collections.Generic;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

public sealed partial class MapStoredSurfaceSource
{
    internal void RequireIdentityRoots(MapAssetClosure assets)
    {
        var roots = new HashSet<MapAssetRef>(_nativeAssets);
        if (roots.Count != _nativeAssets.Count || roots.Count != assets.Roots.Count || !roots.SetEquals(assets.Roots))
            throw new MapDocumentException("Native document roots do not match the verified asset closure.");
    }

    internal IEnumerable<MapSurfaceIndexEntry> EnumeratePinnedEntries(MapWholeIdentityWork? work)
    {
        // Decode the original closure before yielding canonical keys. Metadata caches hold no payloads.
        foreach (MapDirectoryPageRef directory in Index.Directory)
        {
            RequireIdentityStorage(Directory(directory, new MapPageBudget(1)), "directory " + directory.Sha256);
            foreach (MapIndexPageRef page in Index.DirectoryPages[directory.Sha256])
                RequireIdentityStorage(Page(directory, page, new MapPageBudget(1)), "index " + page.Sha256);
        }
        foreach (MapSurfaceIndexEntry entry in Index.ByKey.Values) yield return entry;
    }

    internal string IdentityPatchDigest(MapSurfaceIndexEntry entry, MapWholeIdentityWork? work)
    {
        work?.BeforePayloadRead();
        MapPatchRead read = Payload(entry);
        RequireIdentityStorage(read.Status, "payload " + entry.Key);
        work?.HoldPayload();
        try
        {
            string digest = MapSurfaceSemantics.PatchDigest(read.Patch!);
            if (digest != entry.SemanticSha256)
                throw new MapDocumentException($"Corrupt patch semantic digest for '{entry.Key}'.");
            return digest;
        }
        finally { work?.ReleasePayload(); }
    }

    static void RequireIdentityStorage(MapPatchStatus status, string address)
    {
        if (status != MapPatchStatus.Present)
            throw new MapDocumentException($"{status} pinned surface {address}.");
    }
}
