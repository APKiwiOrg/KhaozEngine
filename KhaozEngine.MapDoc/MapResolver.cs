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
        var playable = MapResolvedBounds.Of(document.PlayableBounds!);
        var storage = MapResolvedBounds.Of(document.Bounds);
        // Snapshot every caller-owned value before invoking the consumer's support callback.
        var authored = document.Placements.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new { p.Id, p.Kind, AssetId = p.AssetId!, p.NumericId, p.X, p.Y, p.Z, p.Yaw, p.Scale, Tags = p.Tags.ToArray() }).ToArray();
        var placements = authored.Select(p =>
        {
            float y = p.Y ?? supportHeight(p.X, p.Z);
            if (!float.IsFinite(y)) throw new MapDocumentException($"Placement '{p.Id}' has nonfinite support height.");
            return new MapResolvedPlacement(p.Id, p.Kind, p.AssetId, p.NumericId,
                new MapTransform(new Vector3(p.X, y, p.Z), p.Yaw, p.Scale), p.Tags);
        }).ToArray();
        return new MapResolvedDocument(placements, assets, playable, storage, hash);
    }
}
