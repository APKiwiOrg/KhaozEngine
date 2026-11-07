namespace KhaozEngine.ItemInstances;

/// <summary>The boot-time rule that refused generation table construction.</summary>
public enum GenerationTableRefusal
{
    /// <summary>A base carries too many authored tag positions (catalog finding KEC0114).</summary>
    BaseTagCountExceeded,
    /// <summary>A candidate bucket has more entries than its 16-bit overlap indices can address.</summary>
    BucketEntryCountExceeded,
    /// <summary>A candidate bucket's cumulative weight exceeds an int (catalog finding KEC0113).</summary>
    BucketWeightExceeded,
    /// <summary>The worst-case excluded run count exceeds the generator scratch ceiling.</summary>
    ExcludedRunCountExceeded,
    /// <summary>A mod id cannot fit above the packed key's tier ordinal bits.</summary>
    PackedModIdExceeded,
    /// <summary>A signature's live union weight exceeds the bounded draw's int ceiling.</summary>
    PoolWeightExceeded,
    /// <summary>A reachable rarity has an uncovered name position (catalog finding KEC0109).</summary>
    RareNamePositionUncovered,
    /// <summary>A tier ordinal cannot fit the packed key's four bits (catalog finding KEC0102).</summary>
    TierOrdinalExceeded,
}
