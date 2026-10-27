using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System.Linq;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>One selected set's explicit asset ceiling, including pieces reused from the decoded cache.</summary>
internal sealed class MapRenderAssetAllowance(MapRenderAssetLimits? limits)
{
    long _bytes;
    int _parts;
    internal MapRenderAssetLimits? Remaining
    {
        get
        {
            if (limits is null) return null;
            var remaining = limits with { MaxDecodedBytes = limits.MaxDecodedBytes - _bytes, MaxParts = limits.MaxParts - _parts };
            if (DemandDiagnostics.IsObserved)
            {
                // The selected set's admitted pieces are the consumed baseline of each later piece's decode gate.
                DemandDiagnostics.Derive(remaining, nameof(MapRenderAssetLimits.MaxDecodedBytes),
                    [new(nameof(MapRenderAssetLimits.MaxDecodedBytes), _bytes, limits.MaxDecodedBytes, this)]);
                DemandDiagnostics.Derive(remaining, nameof(MapRenderAssetLimits.MaxParts),
                    [new(nameof(MapRenderAssetLimits.MaxParts), _parts, limits.MaxParts, this)]);
            }
            return remaining;
        }
    }
    internal string Key(string key) => limits is null ? key : MapPlacementDigest.Hash(w =>
    {
        w.WriteStartArray(); w.WriteStringValue(key); w.WriteNumberValue(limits.MaxDecodedBytes); w.WriteNumberValue(limits.MaxParts);
        w.WriteNumberValue(limits.MaxLods); w.WriteEndArray();
    });
    // The decode gate already published this refusal against the selected set's origin. Only the reported totals translate.
    internal MapRenderAssetRefusal Refuse(MapSceneCapacity capacity)
    {
        if (limits is null || capacity.LimitName is not (nameof(MapRenderAssetLimits.MaxDecodedBytes) or nameof(MapRenderAssetLimits.MaxParts)))
            return MapRenderDecodeCapacity.Refuse(capacity);
        decimal current = capacity.LimitName == nameof(MapRenderAssetLimits.MaxDecodedBytes) ? _bytes : _parts;
        long allowance = capacity.LimitName == nameof(MapRenderAssetLimits.MaxDecodedBytes) ? limits.MaxDecodedBytes : limits.MaxParts;
        return MapRenderDecodeCapacity.Refuse(new(capacity.LimitName, current + capacity.Requested, allowance));
    }
    internal void Record(MapRenderAssets assets)
    {
        if (limits is null) return;
        long parts = assets.ById.Values.Sum(a => a.Lods.Sum(l => (long)l.BorrowParts.Count));
        if (DemandDiagnostics.Check(this, "MapRenderAssetAllowance.MaxDecodedBytes.1", "MaxDecodedBytes", _bytes, assets.RetainedBytes, limits.MaxDecodedBytes, assets.RetainedBytes > limits.MaxDecodedBytes - _bytes, CapacityDemandLifetime.Inventory, allowanceSource: limits))
            throw MapRenderDecodeCapacity.Refuse(nameof(MapRenderAssetLimits.MaxDecodedBytes), (decimal)_bytes + assets.RetainedBytes, limits.MaxDecodedBytes);
        if (DemandDiagnostics.Check(this, "MapRenderAssetAllowance.MaxParts.1", "MaxParts", _parts, parts, limits.MaxParts, parts > limits.MaxParts - _parts, CapacityDemandLifetime.Inventory, allowanceSource: limits))
            throw MapRenderDecodeCapacity.Refuse(nameof(MapRenderAssetLimits.MaxParts), (long)_parts + parts, limits.MaxParts);
        _bytes += assets.RetainedBytes; _parts += (int)parts;
    }
}
