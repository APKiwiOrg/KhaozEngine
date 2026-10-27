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
    internal MapRenderAssetLimits? Remaining => limits is null ? null : limits with
    { MaxDecodedBytes = limits.MaxDecodedBytes - _bytes, MaxParts = limits.MaxParts - _parts };
    internal string Key(string key) => limits is null ? key : MapPlacementDigest.Hash(w =>
    {
        w.WriteStartArray(); w.WriteStringValue(key); w.WriteNumberValue(limits.MaxDecodedBytes); w.WriteNumberValue(limits.MaxParts);
        w.WriteNumberValue(limits.MaxLods); w.WriteEndArray();
    });
    internal MapRenderAssetRefusal Refuse(MapSceneCapacity capacity) => MapRenderDecodeCapacity.Refuse(limits is null ? capacity : capacity.LimitName switch
    {
        nameof(MapRenderAssetLimits.MaxDecodedBytes) => new(capacity.LimitName, _bytes + capacity.Requested, limits.MaxDecodedBytes),
        nameof(MapRenderAssetLimits.MaxParts) => new(capacity.LimitName, _parts + capacity.Requested, limits.MaxParts),
        _ => capacity,
    });
    internal void Record(MapRenderAssets assets)
    {
        if (limits is null) return;
        long parts = assets.ById.Values.Sum(a => a.Lods.Sum(l => (long)l.BorrowParts.Count));
        if (DemandDiagnostics.Check(this, "MapRenderAssetAllowance.MaxDecodedBytes.1", "MaxDecodedBytes", _bytes, assets.RetainedBytes, limits.MaxDecodedBytes, assets.RetainedBytes > limits.MaxDecodedBytes - _bytes, CapacityDemandLifetime.Inventory))
            throw MapRenderDecodeCapacity.Refuse(nameof(MapRenderAssetLimits.MaxDecodedBytes), (decimal)_bytes + assets.RetainedBytes, limits.MaxDecodedBytes);
        if (DemandDiagnostics.Check(this, "MapRenderAssetAllowance.MaxParts.1", "MaxParts", _parts, parts, limits.MaxParts, parts > limits.MaxParts - _parts, CapacityDemandLifetime.Inventory))
            throw MapRenderDecodeCapacity.Refuse(nameof(MapRenderAssetLimits.MaxParts), (long)_parts + parts, limits.MaxParts);
        _bytes += assets.RetainedBytes; _parts += (int)parts;
    }
}
