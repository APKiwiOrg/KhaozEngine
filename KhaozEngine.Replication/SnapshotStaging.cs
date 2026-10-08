using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>Decodes an owned snapshot into a private world before admission. Known components are
/// published by typed copy, without invoking their wire readers again. Unknown extensions stay opaque.</summary>
public sealed class SnapshotStaging
{
    readonly ReplicationRegistry registry;
    readonly ClientReplicationView view;
    public World World { get; } = new();
    public IReadOnlyDictionary<long, Entity> Entities => view.Entities;
    public IReadOnlyList<RetainedComponent> Retained { get; private set; } = Array.Empty<RetainedComponent>();

    SnapshotStaging(ReplicationRegistry registry)
    {
        this.registry = registry;
        view = new(registry);
    }

    public static bool TryDecode(ReplicationRegistry registry, byte[] snapshot,
        out SnapshotStaging? staged, out string? error)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidate = new SnapshotStaging(registry);
        if (!candidate.view.TryApplyRetainingUnknown(candidate.World, snapshot, out var retained, out error))
        {
            staged = null;
            return false;
        }
        candidate.Retained = retained;
        staged = candidate;
        return true;
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
