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
/// The inter-cell messaging seam: how cells exchange ghost-sync, migrate, and ack messages
/// (<see cref="CellMessageKind"/>). <see cref="ShardHost"/> drives it - <see cref="ShardHost.SyncGhosts"/> and
/// <see cref="ShardHost.ProcessHandoffs"/> <see cref="Send"/> messages and <see cref="Drain"/> them at tick/sync
/// boundaries. The shipped default is the in-process <see cref="InProcessCellLink"/> (in-memory, deterministic,
/// whole handshake within a single host pass); a cross-process / cross-machine implementation is infrastructure.
/// </summary>
/// <remarks>
/// <para><b>Network-impl contract</b> (for an infra implementation that carries messages between nodes):</para>
/// <list type="bullet">
/// <item>A <see cref="CellMessage"/> is fully serializable: <see cref="CellMessage.Source"/>/<see cref="CellMessage.Target"/>
/// are two ints each, <see cref="CellMessage.Kind"/> is a byte, <see cref="CellMessage.Payload"/> is an opaque
/// <c>byte[]</c> (a Replication snapshot for ghost/migrate; a NetId for ack). Route a sent message to the node
/// hosting its <see cref="CellMessage.Target"/> cell and surface it from that node's <see cref="Drain"/>.</item>
/// <item><see cref="Drain"/> must be <b>kind-scoped</b> and preserve per-(target, kind) <b>FIFO</b> order, and must
/// not drop or reorder other kinds - the host drains migrate and ack in separate passes and relies on acks
/// surviving the migrate pass.</item>
/// <item>Delivery should be reliable and at-least-once; the handoff protocol tolerates an ack arriving a later
/// boundary (the entity stays <see cref="Migrating"/> on the source meanwhile), but a permanently dropped migrate
/// or ack would strand an entity, so use a reliable channel.</item>
/// <item>Cross-node clock sync (so cells drain on compatible boundaries) is the infra's concern, out of engine scope.</item>
/// </list>
/// </remarks>
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

    /// <summary>
    /// Whether any message of any kind is still queued for <paramref name="target"/>. <see cref="ShardHost"/> uses
    /// it as an eviction gate: unloading a cell with an undrained inbound migrate would strand the crossing entity.
    /// <b>The default returns true</b>, deliberately conservative, so a link that cannot answer blocks eviction
    /// rather than silently allowing a lossy one. Implement it to let cells unload on a custom link.
    /// </summary>
    bool HasPending(CellCoord target) => true;

    /// <summary>
    /// Drops any bookkeeping the link holds for <paramref name="target"/>, called once its cell has been unloaded
    /// so a per-target queue does not outlive the cell it belonged to. The host only calls it after
    /// <see cref="HasPending"/> reported nothing queued, so this discards no message. The default is a no-op.
    /// </summary>
    void Forget(CellCoord target) { }
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

    /// <inheritdoc />
    public bool HasPending(CellCoord target) =>
        inboxes.TryGetValue(target, out List<CellMessage>? inbox) && inbox.Count > 0;

    /// <inheritdoc />
    public void Forget(CellCoord target) => inboxes.Remove(target);
}
