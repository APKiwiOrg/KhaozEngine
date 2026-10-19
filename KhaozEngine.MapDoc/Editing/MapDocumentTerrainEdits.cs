using System;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>A terrain edit as one complete document transaction. <see cref="MapTerrainEdits.Prepare"/> remains the
/// low-level surface operation, which neither validates the document nor reports what the surfaces support. This
/// wrapper joins its surface candidate to a detached copy of the document, refreshes attachment descriptors, validates
/// the complete candidate and finalizes it, so the result carries its dependent foliage rows.</summary>
public static class MapDocumentTerrainEdits
{
    public static MapPrefabEditResult Prepare(MapDocument document, MapAssetClosure assets, MapTerrainEdit edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(edit);
        MapTerrainEditResult surfaces = MapTerrainEdits.Prepare(document.Surfaces, edit);
        MapDocument candidate = MapPrefabEditPreparation.Copy(document);
        candidate.Surfaces = surfaces.Candidate;
        MapPrefabAttachmentDescriptors.Refresh(candidate);
        // Refreshed attachment descriptors are written exactly where they changed, as the editor's terrain command writes them.
        MapNativeWriteSet writes = surfaces.WriteSet with
        {
            Prefabs = surfaces.WriteSet.Prefabs || document.Prefabs?.Digest() != candidate.Prefabs?.Digest(),
            NativeResourceTransforms = surfaces.WriteSet.NativeResourceTransforms ||
                !(document.NativeResourceTransforms ?? []).SequenceEqual(candidate.NativeResourceTransforms ?? []),
        };
        return MapDocumentEditFinalizer.Finalize(document, new(candidate, writes, surfaces.Effects), assets);
    }
}
