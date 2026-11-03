namespace KhaozEngine.Gpu;

/// <summary>Rules that refuse GpuTimestamp operations.</summary>
public enum GpuTimestampRefusal
{
    /// <summary>Refuses resolving or awaiting a batch that has not ended.</summary>
    BatchNotEnded,

    /// <summary>Refuses a timestamp write or tail arm before the batch begins.</summary>
    BatchNotRecording,

    /// <summary>Refuses arming any index except the batch final index.</summary>
    FinalIndexRequired,



    /// <summary>Refuses ending a batch whose timestamps are incomplete.</summary>
    IncompleteBatch,

    /// <summary>Refuses an index that repeats or skips the next timestamp.</summary>
    IndexOutOfSequence,

    /// <summary>Refuses manual write or end of the timestamp pool reserved for command-list End.</summary>
    ManualFinalizationWhileArmed,

    /// <summary>Refuses an output span that cannot hold the batch.</summary>
    OutputTooSmall,

    /// <summary>Refuses arming a second timestamp tail in one recording.</summary>
    TailAlreadyArmed,

    /// <summary>Refuses arming until all earlier timestamps are written and the final one is unwritten.</summary>
    TailPrefixIncomplete,

    /// <summary>Refuses using a timestamp device that is lost or disposed.</summary>
    TimestampDeviceUnavailable,

    /// <summary>Refuses timestamps when the device or adapter has no timestamp capability.</summary>
    TimestampUnsupported,

    /// <summary>Refuses beginning a batch while the preceding batch is unresolved.</summary>
    UnresolvedBatch,

    /// <summary>Refuses timestamp work on another list, command buffer or recording.</summary>
    WrongRecording,
}
