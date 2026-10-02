using System.Collections.Generic;

namespace KhaozEngine.Replication;

/// <summary>
/// One slot's legacy reliable delta state: the snapshot sequence and the exact viewer projection carried by the last
/// payload a writer returned for that slot. Both <see cref="ServerReplicator"/> and <see cref="AoiDeltaReplicator"/>
/// diff the next legacy payload from this projection, because a reliable-ordered receiver applies every returned
/// payload in order and therefore holds exactly what was last sent. The projection is owner-scoped and
/// interest-filtered as it was sent, absence included, so a later owner or interest change is diffed against what the
/// receiver really holds rather than a re-projection of older raw bytes.
/// </summary>
/// <remarks>
/// Replaced only after a payload has been completely built. <see cref="AcknowledgedSequence"/> is sequence-only
/// diagnostic bookkeeping. It never selects a diff basis. The projection's component segments reference immutable
/// capture buffers, so holding it keeps that one capture alive until the slot is served again or forgotten.
/// </remarks>
internal sealed class LegacyDeltaSlot
{
    public LegacyDeltaSlot(int sequence, Dictionary<long, CapturedComponents> projection)
    {
        Sequence = sequence;
        Projection = projection;
    }

    /// <summary>The snapshot sequence of the last returned payload. The next payload names it as its baseline.</summary>
    public int Sequence { get; private set; }

    /// <summary>The exact viewer projection the last returned payload brought the receiver to.</summary>
    public Dictionary<long, CapturedComponents> Projection { get; private set; }

    /// <summary>The newest acknowledged sequence at or before <see cref="Sequence"/>, or -1. Diagnostics only.</summary>
    public int AcknowledgedSequence { get; private set; } = -1;

    /// <summary>Records a completely built payload as the slot's new last-sent state.</summary>
    public void Sent(int sequence, Dictionary<long, CapturedComponents> projection)
    {
        Sequence = sequence;
        Projection = projection;
    }

    /// <summary>Records an acknowledgement for diagnostics. Stale, duplicate and unsent sequences are ignored.</summary>
    public void Acknowledge(int sequence)
    {
        if (sequence > AcknowledgedSequence && sequence <= Sequence) AcknowledgedSequence = sequence;
    }
}
