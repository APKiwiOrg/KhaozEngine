using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>Decodes an owned snapshot into a private world before admission. Known components are
/// published by typed copy, without invoking their wire readers again. Unknown extensions stay opaque.</summary>
public sealed class SnapshotStaging
{
    readonly ReplicationRegistry registry;
    public World World { get; } = new();
    public ReplicationRegistry Registry => registry;
    public IReadOnlyDictionary<long, Entity> Entities { get; private set; } = new Dictionary<long, Entity>();
    public IReadOnlyList<RetainedComponent> Retained { get; private set; } = Array.Empty<RetainedComponent>();

    SnapshotStaging(ReplicationRegistry registry)
    {
        this.registry = registry;
    }

    public static bool TryDecode(ReplicationRegistry registry, byte[] snapshot,
        out SnapshotStaging? staged, out string? error)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidate = new SnapshotStaging(registry);
        var view = new ClientReplicationView(registry);
        if (!view.TryApplyRetainingUnknown(candidate.World, snapshot, out var retained, out error))
        {
            staged = null;
            return false;
        }
        candidate.Entities = view.Entities;
        candidate.Retained = retained;
        staged = candidate;
        return true;
    }

    /// <summary>Stages one live entity by typed copy on the requested transport channel. No codec
    /// writer or reader runs, so a local relocation does not quantize an intermediate pose.</summary>
    public static SnapshotStaging Capture(ReplicationRegistry registry, World source, Entity entity,
        ReplicationChannels channel, long? ownerNetId = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.TryGet(entity, out NetId id)) throw new ArgumentException("The source has no network identity.", nameof(entity));
        var staged = new SnapshotStaging(registry);
        Entity target = staged.World.Spawn();
        staged.World.Set(target, id);
        foreach (ComponentCodec codec in registry.Ordered)
            if (codec.ShouldWrite(channel, id.Value, ownerNetId) && codec.HasComponent(source, entity))
                codec.CopyComponent(source, entity, staged.World, target);
        staged.Entities = new Dictionary<long, Entity> { [id.Value] = target };
        return staged;
    }

    /// <summary>Publishes one admitted entity into a different world. The caller owns net-id
    /// collision handling and retains any admission lease through publication and ownership changes.</summary>
    public Entity CopyTo(long netId, World destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(World, destination)) throw new ArgumentException("Publication needs a different world.", nameof(destination));
        if (!Entities.TryGetValue(netId, out Entity source) || !World.IsAlive(source))
            throw new ArgumentException("The staged entity does not exist.", nameof(netId));
        Entity target = destination.Spawn();
        try
        {
            destination.Set(target, new NetId(netId));
            foreach (ComponentCodec codec in registry.Ordered)
                if (codec.HasComponent(World, source)) codec.CopyComponent(World, source, destination, target);
            return target;
        }
        catch
        {
            destination.Despawn(target);
            throw;
        }
    }
}
