using System.Collections.Generic;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Storage;

public sealed partial class MapStoredSurfaceSource
{
    /// <summary>The manifest-level checks the loaded document runs, through the same validation code. Prefab
    /// reservations against standalone content follow in <see cref="IdentityContentDigest"/>.</summary>
    internal void RequireIdentityManifest(MapAssetClosure assets)
    {
        _globals.Foliage = MapFoliageStorage.ReadComplete(_globals.Foliage, _pinnedTiles.Foliage, _directory);
        MapBoundDocumentValidation.ValidateLocal(_globals);
        MapBoundDocumentValidation.RequireRoots(_nativeAssets, assets);
        MapPrefabValidation.RequireDocument(_globals, assets);
        MapBoundDocumentValidation.RequireWaterMaterials(_globals.Water, assets);
        var foliageErrors = MapFoliageValidation.ValidateBound(_globals, assets);
        if (foliageErrors.Count > 0) throw new MapDocumentException(string.Join("\n", foliageErrors));
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
        // Payload already recomputed the semantic digest from the verified bytes and refused a mismatch.
        try { return read.SemanticSha256!; }
        finally { work?.ReleasePayload(); }
    }

    static void RequireIdentityStorage(MapPatchStatus status, string address)
    {
        if (status != MapPatchStatus.Present)
            throw new MapDocumentException($"{status} pinned surface {address}.");
    }
}
