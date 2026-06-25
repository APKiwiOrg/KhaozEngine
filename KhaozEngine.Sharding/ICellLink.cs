using System;
using System.Collections.Generic;

namespace KhaozEngine.Sharding;

/// <summary>Kind of an inter-cell message.</summary>
public enum CellMessageKind : byte
{
    /// <summary>A border-overlap ghost snapshot mirrored from the source cell to the target.</summary>
    GhostSync = 1,

    /// <summary>An authority-handoff request: the source serialized a crossing entity for the target to adopt.</summary>
    Migrate = 2,

    /// <summary>The target's acknowledgement that it adopted a migrated entity, so the source can release it.</summary>
    MigrateAck = 3,
}

/// <summary>
/// A message from one cell to another, delivered at a tick/sync boundary. The payload is an opaque
/// <c>byte[]</c> (for <see cref="CellMessageKind.GhostSync"/>/<see cref="CellMessageKind.Migrate"/>, a
/// <see cref="KhaozEngine.Replication"/> snapshot; for <see cref="CellMessageKind.MigrateAck"/>, the acked NetId).
/// </summary>
public readonly struct CellMessage
{
    public CellMessage(CellCoord source, CellCoord target, CellMessageKind kind, byte[] payload)
    {
        Source = source;
        Target = target;
        Kind = kind;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <summary>Cell that produced the message.</summary>
    public CellCoord Source { get; }

    /// <summary>Cell the message is destined for.</summary>
    public CellCoord Target { get; }

    /// <summary>What the message carries.</summary>
    public CellMessageKind Kind { get; }

    /// <summary>Opaque payload (a Replication snapshot for <see cref="CellMessageKind.GhostSync"/>).</summary>
    public byte[] Payload { get; }
}

/// <summary>
/// Seam for cell-to-cell messaging (ghost sync now; authority handoff later). The in-process implementation
/// (<see cref="InProcessCellLink"/>) delivers in-memory and deterministically; a network implementation across
/// nodes is infrastructure. Messages are buffered per target and applied when the host drains them at a boundary.
/// </summary>
public interface ICellLink
{
    /// <summary>Queues a message for its target cell.</summary>
    void Send(in CellMessage message);

    /// <summary>
    /// Returns and removes the messages of <paramref name="kind"/> queued for <paramref name="target"/>, in send
    /// (FIFO) order, leaving messages of other kinds queued. Kind-scoped so multi-kind protocols (ghost sync,
    /// migrate, ack) can drain their own messages in separate passes without discarding each other's.
    /// </summary>
    IReadOnlyList<CellMessage> Drain(CellCoord target, CellMessageKind kind);
}

/// <summary>In-process, deterministic <see cref="ICellLink"/>: per-target FIFO in-memory queues.</summary>
public sealed class InProcessCellLink : ICellLink
{
    private static readonly IReadOnlyList<CellMessage> Empty = Array.Empty<CellMessage>();
    private readonly Dictionary<CellCoord, List<CellMessage>> inboxes = new();

    public void Send(in CellMessage message)
    {
        if (!inboxes.TryGetValue(message.Target, out List<CellMessage>? inbox))
        {
            inbox = new List<CellMessage>();
            inboxes[message.Target] = inbox;
        }
        inbox.Add(message);
    }

    public IReadOnlyList<CellMessage> Drain(CellCoord target, CellMessageKind kind)
    {
        if (!inboxes.TryGetValue(target, out List<CellMessage>? inbox) || inbox.Count == 0) return Empty;

        List<CellMessage>? taken = null;
        List<CellMessage>? remaining = null;
        foreach (CellMessage m in inbox)
        {
            if (m.Kind == kind) (taken ??= new List<CellMessage>()).Add(m);
            else (remaining ??= new List<CellMessage>()).Add(m);
        }
        inboxes[target] = remaining ?? new List<CellMessage>();
        return taken ?? Empty;
    }
}
