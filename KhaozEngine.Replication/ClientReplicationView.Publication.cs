using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

// Format 2 publication hooks. ProjectionPublication owns the algorithm, and these members expose the view state it
// reconciles. Presentation buffers only ever take fresh copies, so they never alias a retained projection backing.
public sealed partial class ClientReplicationView
{
    internal ReplicationRegistry Registry => registry;

    /// <summary>Publishes a complete staged projection into <paramref name="target"/>. See
    /// <see cref="ProjectionPublication"/>.</summary>
    internal void PublishProjection(World target, ReplicationProjection projection, ProjectionStaging staged) =>
        ProjectionPublication.Publish(target, this, projection, staged);

    // The pre-publication sampled bytes become 'previous', once per publication.
    internal void ShiftPresentationBuffers()
    {
        previousBytes.Clear();
        foreach (KeyValuePair<(long netId, ushort typeId), byte[]> kv in currentBytes) previousBytes[kv.Key] = kv.Value;
    }

    internal void DespawnForPublication(World world, long netId)
    {
        if (entityByNetId.Remove(netId, out Entity e) && world.IsAlive(e)) world.Despawn(e);
        RemoveEntityBuffers(netId);
    }

    internal Entity GetOrSpawnForPublication(World world, long netId) => GetOrSpawn(world, netId);

    // Installs a sampled payload as 'current'. A byte-equal current array is kept, because it is already a
    // presentation copy. Anything else is copied into a fresh exact-length array, as the legacy readers do.
    internal void SetPresentationBytes(long netId, ushort typeId, ReadOnlySpan<byte> payload)
    {
        if (currentBytes.TryGetValue((netId, typeId), out byte[]? existing) && payload.SequenceEqual(existing)) return;
        SetCurrent(netId, typeId, payload.Length == 0 ? Array.Empty<byte>() : payload.ToArray());
    }

    // A component absent from the new set loses current, previous and its sample history.
    internal void RemovePresentationBuffers(long netId, ushort typeId)
    {
        currentBytes.Remove((netId, typeId));
        previousBytes.Remove((netId, typeId));
        sampleHistory.Remove((netId, typeId));
        if (typeIdsByNetId.TryGetValue(netId, out HashSet<ushort>? tracked)) tracked.Remove(typeId);
    }

    /// <summary>Every array referenced by the current, previous and sample-history presentation buffers.</summary>
    internal IEnumerable<byte[]> PresentationArraysForTest()
    {
        foreach (byte[] bytes in currentBytes.Values) yield return bytes;
        foreach (byte[] bytes in previousBytes.Values) yield return bytes;
        foreach (List<(double t, byte[] bytes)> history in sampleHistory.Values)
            foreach ((double t, byte[] bytes) sample in history) yield return sample.bytes;
    }
}
