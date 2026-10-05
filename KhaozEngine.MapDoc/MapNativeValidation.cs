using System.Collections.Generic;

namespace KhaozEngine.MapDoc;

/// <summary>Native metadata checks shared by loading, editing and saving.</summary>
internal static class MapNativeValidation
{
    internal static void Validate(MapDocument doc, List<string> errors)
    {
        ValidateNumericIds(doc, errors);
        if (doc.ResolverIdentity is { } identity && (identity.PayloadVersion <= 0 || identity.ResolverVersion <= 0))
            errors.Add("resolverIdentity payloadVersion and resolverVersion must be positive.");
        if (doc.NativeAssets is null)
            errors.Add("nativeAssets must not be null.");
        else
        {
            foreach (MapAssetRef asset in doc.NativeAssets)
            {
                if (asset is null)
                {
                    errors.Add("nativeAssets must not contain null entries.");
                    continue;
                }
                if (string.IsNullOrEmpty(asset.Id) || string.IsNullOrEmpty(asset.Path) || string.IsNullOrEmpty(asset.Sha256))
                    errors.Add("native asset id, path and sha256 must be non-empty.");
                if (asset.PayloadVersion <= 0)
                    errors.Add($"native asset '{asset.Id}': payloadVersion must be positive.");
            }
        }
        if (doc.PlayableBounds is not { } bounds) return;
        if (!float.IsFinite(bounds.MinX) || !float.IsFinite(bounds.MinZ) ||
            !float.IsFinite(bounds.MaxX) || !float.IsFinite(bounds.MaxZ) ||
            !(bounds.MaxX > bounds.MinX) || !(bounds.MaxZ > bounds.MinZ))
            errors.Add("playableBounds must be finite with MaxX > MinX and MaxZ > MinZ.");
        if (!(bounds.MinX >= doc.Bounds.MinX) || !(bounds.MinZ >= doc.Bounds.MinZ) ||
            !(bounds.MaxX <= doc.Bounds.MaxX) || !(bounds.MaxZ <= doc.Bounds.MaxZ))
            errors.Add("playableBounds must be contained in bounds.");
    }

    // Reservation accepts newly imported placements above the old mark, but still checks their IDs.
    internal static long ValidateNumericIds(MapDocument doc, List<string> errors, bool requireHighWater = true)
    {
        long maximum = doc.NumericIdHighWaterMark;
        if (maximum < 0)
            errors.Add("numericIdHighWaterMark must be nonnegative.");
        var seen = new HashSet<long>();
        foreach (MapPlacement placement in doc.Placements)
        {
            if (placement.NumericId is not { } id) continue;
            if (id <= 0)
                errors.Add($"placement '{placement.Id}': numericId must be positive when present.");
            if (!seen.Add(id))
                errors.Add($"placement '{placement.Id}': duplicate numericId '{id}'.");
            if (requireHighWater && id > doc.NumericIdHighWaterMark)
                errors.Add($"placement '{placement.Id}': numericId exceeds numericIdHighWaterMark.");
            maximum = System.Math.Max(maximum, id);
        }
        return maximum;
    }
}
