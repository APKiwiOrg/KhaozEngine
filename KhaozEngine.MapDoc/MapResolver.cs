using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc;

/// <summary>Resolves native authored placements without a renderer or an asset normalization pass.</summary>
public static class MapResolver
{
    public static MapResolvedDocument Resolve(MapDocument document, MapAssetClosure assets,
        Func<float, float, float> supportHeight, MapResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(supportHeight);
        string hash = MapAuthoredIdentity.Compute(document, assets, options);
        return Resolve(document, assets, (_, x, z) => supportHeight(x, z), hash);
    }

    internal static MapResolvedDocument Resolve(MapDocument document, MapAssetClosure assets,
        Func<string, float, float, float> supportHeight, string hash)
    {
        var playable = MapResolvedBounds.Of(document.PlayableBounds!);
        var storage = MapResolvedBounds.Of(document.Bounds);
        // Snapshot every caller-owned value before invoking the consumer's support callback.
        var authored = document.Placements.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new { p.Id, p.Kind, AssetId = p.AssetId!, p.NumericId, p.X, p.Y, p.Z, p.Yaw, p.Scale, Tags = p.Tags.ToArray() }).ToArray();
        var placements = authored.Select(p =>
            Place(p.Id, p.Kind, p.AssetId, p.NumericId, p.X, p.Y, p.Z, p.Yaw, p.Scale, p.Tags, supportHeight)).ToArray();
        return new MapResolvedDocument(placements, assets, playable, storage, hash);
    }

    /// <summary>Resolves one placement exactly as <see cref="Resolve(MapDocument, MapAssetClosure, Func{string, float, float, float}, string)"/>
    /// resolves it. Every caller-owned value is copied before the support callback runs.</summary>
    internal static MapResolvedPlacement ResolveOne(MapPlacement placement, Func<string, float, float, float> supportHeight) =>
        Place(placement.Id, placement.Kind, placement.AssetId!, placement.NumericId, placement.X, placement.Y, placement.Z,
            placement.Yaw, placement.Scale, placement.Tags.ToArray(), supportHeight);

    static MapResolvedPlacement Place(string id, string kind, string assetId, long? numericId, float x, float? authoredY,
        float z, float yaw, float scale, string[] tags, Func<string, float, float, float> supportHeight)
    {
        float y = authoredY ?? supportHeight(id, x, z);
        if (!float.IsFinite(y)) throw new MapDocumentException($"Placement '{id}' has nonfinite support height.");
        return new MapResolvedPlacement(id, kind, assetId, numericId, new MapTransform(new Vector3(x, y, z), yaw, scale), tags);
    }
}
