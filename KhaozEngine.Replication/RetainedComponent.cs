using System;

namespace KhaozEngine.Replication;

/// <summary>
/// A component frame carried opaquely through a snapshot restore: the owning entity net id, the component's stable
/// extension type id, and its serialized payload bytes (no framing). Produced by
/// <see cref="ClientReplicationView.TryApplyRetainingUnknown"/> for an extension component id the current registry
/// does not know (a registry downgrade: a build missing a registration, a rollback), so the caller can retain it and
/// re-emit it verbatim on the next save via the <see cref="SnapshotWriter.WriteFiltered(KhaozEngine.Ecs.World,
/// ReplicationRegistry, System.Collections.Generic.IReadOnlySet{int}, ReplicationChannels, int?,
/// System.Func{int, System.Collections.Generic.IReadOnlyList{RetainedComponent}})"/> retained-frames overload
/// (retain-and-rewrite), instead of silently dropping data at rest.
/// </summary>
public readonly struct RetainedComponent
{
    public RetainedComponent(int netId, ushort typeId, byte[] payload)
    {
        NetId = netId;
        TypeId = typeId;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <summary>The net id of the entity this frame belongs to.</summary>
    public int NetId { get; }

    /// <summary>The component's stable replication type id (an extension id, &gt;= <see cref="ReplicationRegistry.FirstExtensionTypeId"/>).</summary>
    public ushort TypeId { get; }

    /// <summary>The component's serialized payload bytes, exactly as they appeared on the wire (no type id, no length prefix).</summary>
    public byte[] Payload { get; }
}
