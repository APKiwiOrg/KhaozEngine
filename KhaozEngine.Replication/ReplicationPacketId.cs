namespace KhaozEngine.Replication;

/// <summary>
/// Identity of one format 2 replication packet and of the projection it reconstructs. Sequences compare only within
/// one epoch, through <see cref="ReplicationSequence"/>. Construction does not validate the epoch. Public stream
/// setup rejects epoch zero.
/// </summary>
/// <param name="Epoch">Nonzero server issued stream epoch. A slot reuse, repair or reset gets a greater one.</param>
/// <param name="Sequence">Unsigned per-slot serve identity. It wraps through <see cref="uint.MaxValue"/> to 0 and is
/// not a simulation tick or a movement command sequence.</param>
public readonly record struct ReplicationPacketId(ulong Epoch, uint Sequence);
