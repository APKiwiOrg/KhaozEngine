using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc;

/// <summary>Render-free native validity, independent of builder options and authored hashing.</summary>
public static class MapBoundDocumentValidation
{
    /// <summary>Validates a complete document against an explicitly supplied verified asset closure.</summary>
    public static void Validate(MapDocument document, MapAssetClosure assets, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ValidateLocal(document, registry);
        var roots = new HashSet<MapAssetRef>(document.NativeAssets);
        if (roots.Count != document.NativeAssets.Count || roots.Count != assets.Roots.Count || !roots.SetEquals(assets.Roots))
            throw new MapDocumentException("Native document roots do not match the verified asset closure.");
        foreach (MapPlacement placement in document.Placements) assets.GetAsset(placement.AssetId!);
    }

    /// <summary>Checks native document-local invariants only. Asset membership requires Validate with a closure.</summary>
    public static void ValidateLocal(MapDocument document, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Tiles is { IsPartial: true }) throw new MapDocumentException("Native document validation requires every tile to be loaded.");
        if (document.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 1 or 2 })
            throw new MapDocumentException("Native document requires payload version 1 and resolver version 1 or 2.");
        if (document.PlayableBounds is null) throw new MapDocumentException("Native document requires playable bounds.");
        var errors = MapDocumentValidator.Validate(document, registry ?? MapDocRegistry.CreateDefault());
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
        if (!float.IsFinite(document.Bounds.MinX) || !float.IsFinite(document.Bounds.MinZ) ||
            !float.IsFinite(document.Bounds.MaxX) || !float.IsFinite(document.Bounds.MaxZ))
            throw new MapDocumentException("Native storage bounds must be finite.");
        foreach (MapPlacement p in document.Placements)
        {
            if (string.IsNullOrWhiteSpace(p.AssetId)) throw new MapDocumentException($"Placement '{p.Id}' requires a native asset ID.");
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Z) || (p.Y is { } y && !float.IsFinite(y)) ||
                !float.IsFinite(p.Yaw) || !float.IsFinite(p.Scale) || p.Scale <= 0 || p.Tags is null || p.Tags.Any(t => t is null))
                throw new MapDocumentException($"Placement '{p.Id}' has invalid transform or tags.");
        }
    }
}
