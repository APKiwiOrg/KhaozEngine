using System;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Counts the pair checks that domain work spends against a fixed limit, so simplicity, overlap and
/// triangulation stay bounded and an oversized domain refuses deterministically. One budget serves one validation
/// call, one compile or one overlap test.</summary>
internal sealed class MapWaterWorkBudget
{
    public MapWaterWorkBudget(long limit = MapWaterDomain.MaxPairChecks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        Limit = limit;
    }

    public long Limit { get; }
    public long Charged { get; private set; }
    public bool Exhausted => Charged > Limit;

    /// <summary>Charges <paramref name="checks"/> pair checks and throws <see cref="MapDocumentException"/> once the
    /// total passes the limit.</summary>
    public void Charge(long checks)
    {
        Charged += checks;
        if (Charged > Limit) throw new MapDocumentException($"domain work exceeds {Limit} pair checks");
    }
}
