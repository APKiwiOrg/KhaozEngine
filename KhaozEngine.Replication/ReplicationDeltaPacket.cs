using System;

namespace KhaozEngine.Replication;

/// <summary>
/// One built format 2 packet body and its identity. The packet owns an immutable private copy of its body, so later
/// writer scratch reuse can never change bytes a caller is about to send. A keyframe names no baseline. A delta names
/// a baseline in its own epoch. Only the replication writers construct packets.
/// </summary>
public sealed class ReplicationDeltaPacket
{
    private readonly byte[] body;

    internal ReplicationDeltaPacket(ReplicationPacketId id, ReplicationPacketId? baseline, bool isKeyframe,
        ReadOnlySpan<byte> body)
    {
        if (isKeyframe && baseline is not null)
            throw new ArgumentException("A keyframe starts from empty state and names no baseline.", nameof(baseline));
        if (!isKeyframe && baseline is null)
            throw new ArgumentException("A delta must name its baseline.", nameof(baseline));
        if (baseline is ReplicationPacketId b && b.Epoch != id.Epoch)
            throw new ArgumentException($"Delta baseline epoch {b.Epoch} differs from packet epoch {id.Epoch}.",
                nameof(baseline));
        Id = id;
        Baseline = baseline;
        IsKeyframe = isKeyframe;
        this.body = body.ToArray();
    }

    /// <summary>The id of the projection this packet reconstructs.</summary>
    public ReplicationPacketId Id { get; }

    /// <summary>The retained projection a delta applies to, or null for a keyframe.</summary>
    public ReplicationPacketId? Baseline { get; }

    /// <summary>True when the packet reconstructs from empty state.</summary>
    public bool IsKeyframe { get; }

    /// <summary>The complete format 2 body, a read-only view of the packet's own copy.</summary>
    public ReadOnlyMemory<byte> Bytes => body;
}
