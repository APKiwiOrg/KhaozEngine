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
/// resolved. A placement whose asset shapes, support or geometry refuse with <see cref="MapDocumentException"/> marks the
/// extent unknown, so an edit is never refused for its bounds. Stateless.</summary>
public sealed class NativePlacementBoundsProvider : INativePlacementBounds
{
    /// <summary>The shared instance.</summary>
    public static NativePlacementBoundsProvider Instance { get; } = new();

    NativePlacementBoundsProvider()
    {
    }

    /// <inheritdoc/>
    public NativePlacementExtent Bounds(MapDocument document, MapAssetClosure assets, MapDocRegistry registry,
        IReadOnlyList<string> placementIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(placementIds);
        if (placementIds.Count == 0) return default;
        var authored = new Dictionary<string, MapPlacement>(StringComparer.Ordinal);
        foreach (MapPlacement placement in document.Placements) authored.TryAdd(placement.Id, placement);
        bool unknown = false;
        // Only placements with a collider or a selection volume have bounds, so only they are resolved. A placement
        // without either never reaches support resolution.
        var shapes = new Dictionary<string, (bool Refused, MapAssetShapes? Shapes)>(StringComparer.Ordinal);
        var shaped = new List<string>(placementIds.Count);
        foreach (string id in placementIds)
        {
            string assetId = (authored.TryGetValue(id, out MapPlacement? placement) ? placement.AssetId : null)
                ?? throw new ArgumentException($"No placement with id '{id}' and an asset.", nameof(placementIds));
            if (!shapes.TryGetValue(assetId, out (bool Refused, MapAssetShapes? Shapes) asset))
                shapes.Add(assetId, asset = ReadShapes(assets, assetId));
            if (asset.Refused) unknown = true;
            else if (asset.Shapes is not null) shaped.Add(id);
        }

        MapBox3? union = null;
        foreach (MapResolvedPlacement placement in Resolve(document, assets, registry, shaped, ref unknown))
        {
            try
            {
                if (MapPlacementShapes.Bounds(placement, shapes[placement.AssetId].Shapes!) is { } box)
                    union = union is { } u ? u.Union(box) : box;
            }
            catch (MapDocumentException)
            {
                unknown = true;
            }
        }
        return new NativePlacementExtent(union, unknown);
    }

    // The asset's shapes, no shapes when it declares neither a collider nor a selection volume, or refused when the
    // closure refuses them.
    static (bool Refused, MapAssetShapes? Shapes) ReadShapes(MapAssetClosure assets, string assetId)
    {
        try
        {
            MapResolvedAsset declared = assets.GetAsset(assetId);
            if (declared.CollisionResourceId is null && declared.SelectionResourceId is null) return (false, null);
            return (false, MapAssetShapes.Read(assets, assetId));
        }
        catch (MapDocumentException)
        {
            return (true, null);
        }
    }

    // Resolves the placements together, and one at a time only when that refuses, so one unresolvable placement
    // leaves the others bounded.
    static IReadOnlyList<MapResolvedPlacement> Resolve(MapDocument document, MapAssetClosure assets,
        MapDocRegistry registry, IReadOnlyList<string> ids, ref bool unknown)
    {
        if (ids.Count == 0) return Array.Empty<MapResolvedPlacement>();
        MapResolveOptions options = NativeDocumentService.SessionOptionsFor(document.ResolverIdentity);
        // The analytic field is built only when a resolver-1 placement takes its height from support.
        TerrainField? field = null;
        float Height(float x, float z) => (field ??= MapRuntime.BuildField(document, registry)).SampleHeight(x, z);
        try
        {
            return MapNativeResolution.ResolvePlacements(document, assets, options, ids, Height);
        }
        catch (MapDocumentException)
        {
        }
        var resolved = new List<MapResolvedPlacement>(ids.Count);
        foreach (string id in ids)
        {
            try
            {
                resolved.AddRange(MapNativeResolution.ResolvePlacements(document, assets, options, new[] { id }, Height));
            }
            catch (MapDocumentException)
            {
                unknown = true;
            }
        }
        return resolved;
    }
}
