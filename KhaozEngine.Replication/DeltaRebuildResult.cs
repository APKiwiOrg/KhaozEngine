namespace KhaozEngine.Replication;

/// <summary>Outcome of offering one format 2 packet to a receiver.</summary>
public enum DeltaRebuildResult
{
    /// <summary>The packet reconstructed, was retained and was published.</summary>
    Accepted,

    /// <summary>The packet is not newer than the latest accepted id in its epoch, or belongs to a retired epoch. Its
    /// body was not decoded and nothing changed.</summary>
    DuplicateOrStale,

    /// <summary>The named baseline is not retained. Nothing changed. The caller may request a keyframe repair.</summary>
    MissingBaseline,

    /// <summary>The packet was rejected. The receiver's failure reason names why. Nothing was published.</summary>
    Invalid,
}
