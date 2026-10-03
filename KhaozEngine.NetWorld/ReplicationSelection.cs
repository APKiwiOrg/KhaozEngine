namespace KhaozEngine.NetWorld;

/// <summary>How a session receives replicated state.</summary>
public enum ReplicationDeliveryMode
{
    /// <summary>The reliable delta or snapshot path every peer supports. The default.</summary>
    LegacyReliable = 0,

    /// <summary>Format 2: unreliable state deltas against acknowledged projections, with reliable keyframe repair.
    /// Selected only when both peers opt in and the transport limits allow it.</summary>
    AcknowledgedUnreliable = 1,
}

/// <summary>Why a session holds its <see cref="ReplicationDeliveryMode"/>.</summary>
public enum ReplicationSelectionReason
{
    /// <summary>No mode offer arrived or was sent: the client did not ask, the server did not offer, or an older
    /// server ignored the request. The session stays on <see cref="ReplicationDeliveryMode.LegacyReliable"/>.</summary>
    Unnegotiated,

    /// <summary>The server offered the selected mode and its limits.</summary>
    Selected,

    /// <summary>The server fell back to reliable delivery because the connection's transport limit is unknown or
    /// too small for the configured keyframe budget.</summary>
    UnavailableTransportLimit,

    /// <summary>The server fell back to reliable delivery because its policy does not allow unreliable delta
    /// replication.</summary>
    DisabledServerPolicy,
}

/// <summary>The replication mode a session runs, why, and the format 2 epoch when one is active.</summary>
/// <param name="Mode">The delivery mode in effect.</param>
/// <param name="Reason">Why that mode is in effect.</param>
/// <param name="Epoch">The active format 2 stream epoch, nonzero only under
/// <see cref="ReplicationDeliveryMode.AcknowledgedUnreliable"/>.</param>
public readonly record struct ReplicationSelection(
    ReplicationDeliveryMode Mode,
    ReplicationSelectionReason Reason,
    ulong Epoch);
