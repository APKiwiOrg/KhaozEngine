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

    /// <summary>
    /// The projection this staging was last finished for, by <see cref="StageAll"/> or <see cref="FinishFor"/>. Any
    /// later <see cref="Reset"/>, <see cref="GetOrSpawn"/> or <see cref="Decode"/> clears it. Publication requires
    /// this exact instance before its first change, so a staging decoded for another projection can never leave the
    /// live world half published.
    /// </summary>
    internal ReplicationProjection? StagedFor { get; private set; }

    /// <summary>
    /// The finishing step after a caller decoded the known frames of <paramref name="projection"/> itself: spawns a
    /// staged entity for every net id that has none, then records the projection as <see cref="StagedFor"/>. The
    /// caller guarantees every known frame was decoded.
    /// </summary>
    internal void FinishFor(ReplicationProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        foreach (long netId in projection.Entities.Keys) GetOrSpawn(netId);
        StagedFor = projection;
    }

    /// <summary>Despawns every staged entity, so staging holds nothing from an earlier packet.</summary>
    internal void Reset()
    {
        foreach (Entity e in entityByNetId.Values)
            if (world.IsAlive(e)) world.Despawn(e);
        entityByNetId.Clear();
        StagedFor = null;
    }

    internal bool TryGetEntity(long netId, out Entity entity) => entityByNetId.TryGetValue(netId, out entity);

    internal Entity GetOrSpawn(long netId)
    {
        StagedFor = null;
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
    /// built-in id, a registered id that never replicates, a reader failure, or a reader that does not consume
    /// exactly the payload.</exception>
    internal bool Decode(long netId, in ProjectedComponent component)
    {
        StagedFor = null;
        if (!registry.TryGet(component.TypeId, out ComponentCodec codec))
        {
            if (ReplicationRegistry.IsExtension(component.TypeId)) return false;
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Projection references unregistered built-in type id {component.TypeId}.");
        }
        if ((codec.Channels & ReplicationChannels.Replicate) == 0)
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Projection carries type id {component.TypeId}, which is registered but never replicates.");
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

    /// <summary>
    /// Reads one unframed built-in frame whose length the wire does not state. The registered reader sees the
    /// <paramref name="available"/> bytes from <paramref name="offset"/> onward, the rest of the received body, and the
    /// bytes it consumes become the frame's boundary. Received built-in replacements use this exactly once. Frames
    /// carried over from a baseline already have a boundary and use <see cref="Decode"/>.
    /// </summary>
    /// <returns>The bytes the reader consumed.</returns>
    /// <exception cref="DeltaRebuildException"><see cref="DeltaRebuildFailure.MalformedPacket"/> for an extension or
    /// unregistered built-in id, a registered id that never replicates, or a reader that fails or runs past the
    /// body.</exception>
    internal int DecodeUnframed(long netId, ushort typeId, byte[] source, int offset, int available)
    {
        StagedFor = null;
        if (ReplicationRegistry.IsExtension(typeId))
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Type id {typeId} is an extension, which is always framed.");
        if (!registry.TryGet(typeId, out ComponentCodec codec))
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Body references unregistered built-in type id {typeId}.");
        if ((codec.Channels & ReplicationChannels.Replicate) == 0)
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Body carries type id {typeId}, which is registered but never replicates.");
        Entity entity = GetOrSpawn(netId);
        window.Retarget(source, offset, available);
        try
        {
            codec.Deserialize(world, entity, reader);
            return (int)window.Position;
        }
        catch (Exception ex)
        {
            throw new DeltaRebuildException(DeltaRebuildFailure.MalformedPacket,
                $"Built-in type id {typeId} did not decode from the remaining body: {ex.Message}");
        }
        finally
        {
            window.Release();
        }
    }

    /// <summary>Resets, stages every entity of <paramref name="projection"/>, decodes each known frame once, then
    /// records the projection as <see cref="StagedFor"/>. A decode failure leaves <see cref="StagedFor"/> null.</summary>
    internal void StageAll(ReplicationProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        Reset();
        foreach (KeyValuePair<long, ProjectedEntity> kv in projection.Entities)
        {
            GetOrSpawn(kv.Key);
            for (int i = 0; i < kv.Value.Count; i++) Decode(kv.Key, kv.Value[i]);
        }
        StagedFor = projection;
    }
}
