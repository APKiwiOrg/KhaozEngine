using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Aggregate retention admission, checked before allocating a sample, key or growing a retained list.
/// Charges include conservative object, index and list capacity overhead, not measured GC allocations.</summary>
public sealed class MapFoliageRetentionBudget
{
    readonly long _maxSamples, _maxBytes;
    internal long Samples { get; private set; }
    internal long Bytes { get; private set; }

    public MapFoliageRetentionBudget(long maxSamples, long maxBytes)
    {
        if (maxSamples < 0 || maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxSamples));
        _maxSamples = maxSamples; _maxBytes = maxBytes;
    }

    public static long ChargeSample(string layerId, string surfaceId, string assetId, string policyId, IReadOnlyList<string> materials)
    {
        long text = checked((long)Encoding.UTF8.GetByteCount(layerId) + Encoding.UTF8.GetByteCount(surfaceId) +
            Encoding.UTF8.GetByteCount(assetId) + Encoding.UTF8.GetByteCount(policyId));
        foreach (string id in materials) text = checked(text + Encoding.UTF8.GetByteCount(id));
        return checked(512 + 8L * materials.Count + 2 * text);
    }

    public bool TryRetainBatch()
    {
        if (DemandDiagnostics.Check(this, "MapFoliageRetentionBudget.MaxQueryRetainedBytes.1", "MaxQueryRetainedBytes", Bytes, 256, _maxBytes, _maxBytes - Bytes < 256, CapacityDemandLifetime.Inventory)) return false;
        Bytes += 256;
        return true;
    }

    public bool TryRetain(long sampleCharge)
    {
        if (sampleCharge < 0) throw new ArgumentOutOfRangeException(nameof(sampleCharge));
        if (DemandDiagnostics.Check(this, "MapFoliageRetentionBudget.MaxQuerySamples.1", "MaxQuerySamples", Samples, 1, _maxSamples, Samples >= _maxSamples, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapFoliageRetentionBudget.MaxQueryRetainedBytes.2", "MaxQueryRetainedBytes", Bytes, sampleCharge, _maxBytes, sampleCharge > _maxBytes - Bytes, CapacityDemandLifetime.Inventory)) return false;
        Bytes += sampleCharge; Samples++;
        return true;
    }
}
