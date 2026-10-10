using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>What <see cref="INativePlacementBounds"/> knows about a set of placements. <see cref="Bounds"/> is the union
/// of the geometry it could compute, or null when there is none. <see cref="Unknown"/> is true when the geometry of at
/// least one placement could not be computed, so <see cref="Bounds"/> does not limit the set.</summary>
public readonly record struct NativePlacementExtent(MapBox3? Bounds, bool Unknown);

/// <summary>World bounds of native placements, which a host that resolves placement geometry binds next to the asset
/// closure through <see cref="EditorHistory.BindNativeAssets(MapDocument, MapAssetClosure, INativePlacementBounds)"/>.
/// The editor itself holds no physics, so without a binding a placement edit reports
/// <see cref="MapNativeInvalidation.Unbounded"/>.</summary>
public interface INativePlacementBounds
{
    /// <summary>The union, in world metres, of the collider bounds and interaction envelope bounds of every placement
    /// of <paramref name="document"/> named by <paramref name="placementIds"/>, resolved against
    /// <paramref name="assets"/> and <paramref name="registry"/>. A placement with neither a collider nor a selection
    /// volume contributes nothing. A placement whose geometry cannot be computed, for example because its support does
    /// not resolve or its asset shapes are refused, marks the extent <see cref="NativePlacementExtent.Unknown"/>
    /// instead of throwing, so an edit is never refused for its bounds. Programming errors such as an ID the document
    /// does not hold still throw.</summary>
    NativePlacementExtent Bounds(MapDocument document, MapAssetClosure assets, MapDocRegistry registry,
        IReadOnlyList<string> placementIds);
}

/// <summary>The effects of one native placement edit, from the placements it changed.</summary>
internal static class NativePlacementEffects
{
    const MapNativeInvalidation Spatial = MapNativeInvalidation.Physics | MapNativeInvalidation.Nav |
        MapNativeInvalidation.Residency;

    /// <summary>Diffs placements by ID between <paramref name="before"/> and <paramref name="after"/>: added, removed,
    /// and any change to position, yaw, scale, asset, support binding or numeric ID, the inputs of a placement's
    /// geometry and digest. With no such change the edit reports <see cref="MapNativeInvalidation.Placements"/> only.
    /// With <paramref name="bounds"/> and <paramref name="assets"/> bound it reports the changed placements' bounds
    /// before as the old bounds and after as the new bounds, with Physics, Nav and Residency unless neither side has
    /// geometry. Without them, or when either side is <see cref="NativePlacementExtent.Unknown"/>, it reports Physics,
    /// Nav, Residency and <see cref="MapNativeInvalidation.Unbounded"/> with no bounds, since the change cannot be
    /// sized.</summary>
    internal static MapNativeEditEffects Describe(MapDocument before, MapDocument after, MapAssetClosure? assets,
        MapDocRegistry registry, INativePlacementBounds? bounds)
    {
        Dictionary<string, MapPlacement> old = ById(before), current = ById(after);
        string[] changed = old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(id => !old.TryGetValue(id, out MapPlacement? a) || !current.TryGetValue(id, out MapPlacement? b) ||
                !SameGeometry(a, b))
            .ToArray();
        if (changed.Length == 0) return Effects(null, null, MapNativeInvalidation.Placements);
        if (assets is null || bounds is null) return Effects(null, null, FullReset);

        NativePlacementExtent oldExtent = Extent(before, old, changed, assets, registry, bounds);
        NativePlacementExtent newExtent = Extent(after, current, changed, assets, registry, bounds);
        if (oldExtent.Unknown || newExtent.Unknown) return Effects(null, null, FullReset);
        if (oldExtent.Bounds is null && newExtent.Bounds is null) return Effects(null, null, MapNativeInvalidation.Placements);
        return Effects(oldExtent.Bounds, newExtent.Bounds, MapNativeInvalidation.Placements | Spatial);
    }

    // What an edit of unknown extent invalidates: everything, as a full reset.
    const MapNativeInvalidation FullReset = MapNativeInvalidation.Placements | Spatial | MapNativeInvalidation.Unbounded;

    static NativePlacementExtent Extent(MapDocument document, Dictionary<string, MapPlacement> placements, string[] changed,
        MapAssetClosure assets, MapDocRegistry registry, INativePlacementBounds bounds)
    {
        string[] present = changed.Where(placements.ContainsKey).ToArray();
        return present.Length == 0 ? default : bounds.Bounds(document, assets, registry, Array.AsReadOnly(present));
    }

    static Dictionary<string, MapPlacement> ById(MapDocument document)
    {
        var placements = new Dictionary<string, MapPlacement>(StringComparer.Ordinal);
        foreach (MapPlacement placement in document.Placements) placements.TryAdd(placement.Id, placement);
        return placements;
    }

    // Floats compare by bits, as the placement digest hashes them, so a sign-of-zero change still counts.
    static bool SameGeometry(MapPlacement a, MapPlacement b) =>
        string.Equals(a.AssetId, b.AssetId, StringComparison.Ordinal) && a.NumericId == b.NumericId &&
        Same(a.X, b.X) && Same(a.Z, b.Z) && Same(a.Yaw, b.Yaw) && Same(a.Scale, b.Scale) &&
        (a.Y is { } ay ? b.Y is { } by && Same(ay, by) : b.Y is null) &&
        Equals(a.SupportBinding, b.SupportBinding);

    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    static MapNativeEditEffects Effects(MapBox3? oldBounds, MapBox3? newBounds, MapNativeInvalidation invalidates) =>
        new(oldBounds, newBounds, Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<MapDigestChange>(), invalidates);
}
