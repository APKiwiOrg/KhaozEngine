namespace KhaozEngine.Replication;

/// <summary>Typed reason a format 2 build, retention or reconstruction step failed.</summary>
public enum DeltaRebuildFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>The packet bytes are malformed, or name an unknown built-in component.</summary>
    MalformedPacket,

    /// <summary>A single projection or keyframe can never fit the configured limits.</summary>
    CapacityExceeded,

    /// <summary>The projection would fit alone, but pinned projections leave no room for it. A keyframe repair
    /// releases the pins.</summary>
    RetentionPressure,

    /// <summary>A sequence or baseline is half the sequence range away, so its order is unknown. A new epoch is
    /// required.</summary>
    SequenceAmbiguous,
}
