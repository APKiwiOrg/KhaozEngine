using System;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>A terrain edit as one complete document transaction, and the one home of the terrain transaction the editor
/// runs. <see cref="MapTerrainEdits.Prepare"/> remains the low-level surface operation, which neither validates the
/// document nor reports what the surfaces support. This joins its surface candidate to a detached document copy,
/// refreshes attachment descriptors, re-seats Y-null resolver-2 placements, names the water bodies the edit touches,
/// validates the complete candidate and finalizes it, so the result carries its dependent foliage effects.</summary>
public static class MapDocumentTerrainEdits
{
    public static MapPrefabEditResult Prepare(MapDocument document, MapAssetClosure assets, MapTerrainEdit edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(edit);
        MapDocumentEditFinalizer.Preflight(document, assets);
        MapDocument candidate = MapPrefabEditPreparation.Copy(document);
        var (writes, effects, _) = Apply(candidate, assets, edit);
        return MapDocumentEditFinalizer.Finalize(document, new(candidate, writes, Name(candidate, effects)), assets);
    }

    /// <summary>Applies the terrain transaction to a detached <paramref name="candidate"/> in place and returns its write
    /// set, its surface effects before water naming and the surface result. Without <paramref name="assets"/>, as on the
    /// editor's local-only path, placements are not re-seated.</summary>
    internal static (MapNativeWriteSet WriteSet, MapNativeEditEffects Effects, MapTerrainEditResult Surfaces) Apply(MapDocument candidate,
        MapAssetClosure? assets, MapTerrainEdit edit)
    {
        string? prefabs = candidate.Prefabs?.Digest();
        var transforms = candidate.NativeResourceTransforms?.ToArray() ?? [];
        MapTerrainEditResult prepared = MapTerrainEdits.Prepare(candidate.Surfaces, edit);
        candidate.Surfaces = prepared.Candidate;
        MapPrefabAttachmentDescriptors.Refresh(candidate);
        MapNativeWriteSet reseat = assets is null ? MapPlacementSeats.Empty : MapPlacementSeats.Reseat(candidate, assets);
        // Refreshed descriptors and re-seated poses are written exactly where they changed.
        MapNativeWriteSet writes = MapNativeWriteSet.Union(prepared.WriteSet, reseat) with
        {
            Prefabs = reseat.Prefabs || prefabs != candidate.Prefabs?.Digest(),
            NativeResourceTransforms = reseat.NativeResourceTransforms || !transforms.SequenceEqual(candidate.NativeResourceTransforms ?? []),
        };
        return (writes, prepared.Effects, prepared);
    }

    /// <summary>The effects with the water bodies whose covered spaces the edit touched, and the Medium flag.</summary>
    internal static MapNativeEditEffects Name(MapDocument candidate, MapNativeEditEffects effects) =>
        MapWaterEdits.NameAffectedBodies(candidate.Water, effects, candidate.Surfaces);
}
