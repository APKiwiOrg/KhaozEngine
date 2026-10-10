using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Sizes native placement edits under the session resolve options: <see cref="NativeDocumentService.SessionOptions"/>
/// with analytic <see cref="MapRuntime.BuildField"/> support for resolver 1, and
/// <see cref="NativeDocumentService.SessionOptionsV2"/> with authored bindings for resolver 2. It resolves only the
/// listed placements whose assets declare a collider or a selection volume, and bounds each with
/// <see cref="MapPlacementShapes.Bounds"/>. A placement whose asset declares neither contributes nothing and is never
/// resolved. A shaped placement whose support cannot resolve refuses as resolution does. Stateless.</summary>
public sealed class NativePlacementBoundsProvider : INativePlacementBounds
{
    /// <summary>The shared instance.</summary>
    public static NativePlacementBoundsProvider Instance { get; } = new();

    NativePlacementBoundsProvider()
    {
    }

    /// <inheritdoc/>
    public MapBox3? Bounds(MapDocument document, MapAssetClosure assets, MapDocRegistry registry, IReadOnlyList<string> placementIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(placementIds);
        if (placementIds.Count == 0) return null;
        var authored = new Dictionary<string, MapPlacement>(StringComparer.Ordinal);
        foreach (MapPlacement placement in document.Placements) authored.TryAdd(placement.Id, placement);
        // Only placements with a collider or a selection volume have bounds, so only they are resolved. A placement
        // without either never reaches support resolution.
        var shapes = new Dictionary<string, MapAssetShapes>(StringComparer.Ordinal);
        var shaped = new List<string>(placementIds.Count);
        foreach (string id in placementIds)
        {
            string assetId = (authored.TryGetValue(id, out MapPlacement? placement) ? placement.AssetId : null)
                ?? throw new MapDocumentException($"No placement with id '{id}' and an asset.");
            MapResolvedAsset declared = assets.GetAsset(assetId);
            if (declared.CollisionResourceId is null && declared.SelectionResourceId is null) continue;
            if (!shapes.ContainsKey(assetId)) shapes.Add(assetId, MapAssetShapes.Read(assets, assetId));
            shaped.Add(id);
        }
        if (shaped.Count == 0) return null;

        MapResolveOptions options = NativeDocumentService.SessionOptionsFor(document.ResolverIdentity);
        // The analytic field is built only when a resolver-1 placement takes its height from support.
        TerrainField? field = null;
        IReadOnlyList<MapResolvedPlacement> resolved = MapNativeResolution.ResolvePlacements(document, assets, options,
            shaped, (x, z) => (field ??= MapRuntime.BuildField(document, registry)).SampleHeight(x, z));
        MapBox3? union = null;
        foreach (MapResolvedPlacement placement in resolved)
        {
            if (MapPlacementShapes.Bounds(placement, shapes[placement.AssetId]) is not { } box) continue;
            union = union is not { } u ? box : new MapBox3(Math.Min(u.MinX, box.MinX), Math.Min(u.MinY, box.MinY),
                Math.Min(u.MinZ, box.MinZ), Math.Max(u.MaxX, box.MaxX), Math.Max(u.MaxY, box.MaxY), Math.Max(u.MaxZ, box.MaxZ));
        }
        return union;
    }
}
