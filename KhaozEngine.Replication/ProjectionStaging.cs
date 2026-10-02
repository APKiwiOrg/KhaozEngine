using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// One private ECS world and net-id map holding typed, decoded values of one projection before publication. Each known
/// component is read once through its registered reader into staging, and publication installs it into the live world
/// through the codec's typed copy, never a second wire read. Unknown extension frames stay opaque and are not staged.
/// Holds at most one projection: <see cref="Reset"/> despawns everything before the next packet stages.
/// Single-threaded.
/// </summary>
internal sealed class ProjectionStaging
{
    private readonly ReplicationRegistry registry;
    private readonly World world = new();
    private readonly Dictionary<long, Entity> entityByNetId = new();

    // Every reader, built-in or extension, sees exactly its own payload slice, so a reader cannot consume bytes of
    // another frame and exact consumption can be checked.
    private readonly FramedPayloadStream window = new();
    private readonly BinaryReader reader;

    public ProjectionStaging(ReplicationRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        reader = new BinaryReader(window);
    }

    internal World World => world;

    internal ReplicationRegistry Registry => registry;

    internal int EntityCount => entityByNetId.Count;

    /// <summary>Despawns every staged entity, so staging holds nothing from an earlier packet.</summary>
    internal void Reset()
    {
        foreach (Entity e in entityByNetId.Values)
            if (world.IsAlive(e)) world.Despawn(e);
        entityByNetId.Clear();
    }

    internal bool TryGetEntity(long netId, out Entity entity) => entityByNetId.TryGetValue(netId, out entity);

    internal Entity GetOrSpawn(long netId)
    {
        if (entityByNetId.TryGetValue(netId, out Entity e)) return e;
        e = world.Spawn();
        entityByNetId.Add(netId, e);
        return e;
    }

    /// <summary>
    /// Reads one frame through its registered reader into the staged entity for <paramref name="netId"/>. Returns
    /// false, staging nothing, for an unregistered extension id, whose payload stays opaque.
    /// </summary>
    /// <exception cref="DeltaRebuildException"><see cref="DeltaRebuildFailure.MalformedPacket"/> for an unregistered
    /// built-in id, a reader failure, or a reader that does not consume exactly the payload.</exception>
    internal bool Decode(long netId, in ProjectedComponent component)
    {
        if (!registry.TryGet(component.TypeId, out ComponentCodec codec))
        {
            if (ReplicationRegistry.IsExtension(component.TypeId)) return false;
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Projection references unregistered built-in type id {component.TypeId}.");
        }
        Entity entity = GetOrSpawn(netId);
        window.Retarget(component.Backing, component.Offset, component.Length);
        try
        {
            codec.Deserialize(world, entity, reader);
            if (window.Position != component.Length)
                throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                    $"Type id {component.TypeId} read {window.Position} of {component.Length} payload bytes.");
        }
        catch (Exception ex) when (ex is not DeltaRebuildException)
        {
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Type id {component.TypeId} payload did not decode: {ex.Message}");
        }
        finally
        {
            window.Release();   // never keep a projection backing alive from staging
        }
        return true;
    }

    /// <summary>Resets, then stages every entity of <paramref name="projection"/> and decodes each known frame once.</summary>
    internal void StageAll(ReplicationProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        Reset();
        foreach (KeyValuePair<long, ProjectedEntity> kv in projection.Entities)
        {
            GetOrSpawn(kv.Key);
            for (int i = 0; i < kv.Value.Count; i++) Decode(kv.Key, kv.Value[i]);
        }
    }
}
