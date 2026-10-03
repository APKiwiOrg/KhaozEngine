namespace KhaozEngine.Replication;

/// <summary>
/// Test diagnostics for one writer slot's format 2 state. Stream owners read only
/// <see cref="ServerReplicator.RebuildNeedsRepair"/> or <see cref="AoiDeltaReplicator.RebuildNeedsRepair"/> and
/// <see cref="DeltaRebuildException.Failure"/>, never this record. A slot without format 2 state reads as default.
/// </summary>
/// <param name="RetainedCount">Retained projections: pins, committed sends and the unsent candidate.</param>
/// <param name="RetainedBytes">Distinct reachable backing bytes over every retained projection, each array counted
/// once at its full length.</param>
/// <param name="CandidateId">The built but not yet sent candidate, or null.</param>
/// <param name="AcknowledgedId">The acknowledged baseline the next delta diffs from, or null.</param>
/// <param name="NewSentCount">Committed sends newer than <paramref name="AcknowledgedId"/>, or every committed send
/// when nothing is acknowledged. The no-ack window compares against it.</param>
/// <param name="Entities">Entities summed over retained projections. An upper bound, since an entity held by two
/// projections counts twice.</param>
/// <param name="Components">Component frames summed over retained projections, zero-byte and opaque frames included.
/// An upper bound, since frames held by two projections count twice.</param>
internal readonly record struct RebuildUsage(
    int RetainedCount,
    int RetainedBytes,
    ReplicationPacketId? CandidateId,
    ReplicationPacketId? AcknowledgedId,
    int NewSentCount,
    int Entities,
    int Components);
