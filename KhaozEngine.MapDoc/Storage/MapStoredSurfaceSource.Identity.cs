using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

public sealed partial class MapStoredSurfaceSource
{
    /// <summary>The manifest-level checks the loaded document runs, through the same validation code. Prefab
    /// reservations against standalone content follow in <see cref="IdentityContentDigest"/>.</summary>
    internal void RequireIdentityManifest(MapAssetClosure assets)
    {
        MapDocument complete = MapTiledFile.GlobalsOnly(_globals);
        complete.Foliage = MapFoliageStorage.ReadComplete(_globals.Foliage, _pinnedTiles.Foliage, _directory);
        MapBoundDocumentValidation.ValidateLocal(complete);
        MapBoundDocumentValidation.RequireRoots(_nativeAssets, assets);
        MapPrefabValidation.RequireDocument(complete, assets);
        MapBoundDocumentValidation.RequireWaterMaterials(complete.Water, assets);
        var foliageFindings = MapFoliageValidation.ValidateBoundFindings(complete, assets);
        if (foliageFindings.Count > 0) throw MapDocumentRefusalFactory.FromFinding(foliageFindings[0],
            string.Join("\n", foliageFindings.Select(f => f.Message)), findings: foliageFindings);
    }

    internal IEnumerable<MapSurfaceIndexEntry> EnumeratePinnedEntries(MapWholeIdentityWork? work)
    {
        // Decode the original closure before yielding canonical keys. Metadata caches hold no payloads.
        foreach (MapDirectoryPageRef directory in Index.Directory)
        {
            RequireIdentityStorage(Directory(directory, new MapPageBudget(1)), "directory", directory.Sha256);
            foreach (MapIndexPageRef page in Index.DirectoryPages[directory.Sha256])
                RequireIdentityStorage(Page(directory, page, new MapPageBudget(1)), "index", page.Sha256);
        }
        foreach (MapSurfaceIndexEntry entry in Index.ByKey.Values) yield return entry;
    }

    internal string IdentityPatchDigest(MapSurfaceIndexEntry entry, MapWholeIdentityWork? work)
    {
        work?.BeforePayloadRead();
        MapPatchRead read = Payload(entry);
        RequireIdentityStorage(read.Status, "payload", entry.PayloadSha256, entry.Key, read.Cause);
        work?.HoldPayload();
        // Payload already recomputed the semantic digest from the verified bytes and refused a mismatch.
        try { return read.SemanticSha256!; }
        finally { work?.ReleasePayload(); }
    }

    static void RequireIdentityStorage(MapPatchStatus status, string kind, string digest, MapPatchKey? patch = null,
        System.Exception? cause = null)
    {
        if (status != MapPatchStatus.Present)
        {
            string message = $"{status} pinned surface {kind} {(patch is { } key ? key.ToString() : digest)}.";
            throw cause is MapDocumentException refusal ? refusal.WithSourceName(null, message, patchStatus: status, expectedHash: digest)
                : new(MapDocumentRefusal.PinnedContentUnavailable, message, cause!)
                    { PatchStatus = status, Patch = patch, ExpectedHash = digest, SubjectKind = kind };
        }
    }
}
