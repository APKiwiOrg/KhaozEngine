using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldPickups
{
    /// <summary>
    /// Re-adopts valid pickup entities in a restored <paramref name="world"/> that this seam is not already
    /// tracking. A candidate must carry <see cref="PickupState"/>, <see cref="NetId"/>, and
    /// <see cref="ReplicatedPosition"/>, and the host must confirm that this exact world and entity own its positive
    /// net id. Invalid, duplicate, ghost, and otherwise unowned entities are skipped.
    /// </summary>
    /// <remarks>
    /// The persisted payload, owner, and position are preserved. Process-local state starts fresh: radius and
    /// time-to-live use <see cref="WorldPickupsConfig.DefaultRadius"/> and
    /// <see cref="WorldPickupsConfig.DefaultTimeToLiveSeconds"/>, age starts at zero, and offer history is empty.
    /// The restored pickup therefore receives a fresh full lifetime and can be offered on the next
    /// <see cref="Update"/>. Repeated calls are idempotent and do not reset an adopted pickup's age or offers.
    /// <para>For asynchronous cell loads, subscribe beside this seam to
    /// <see cref="CellPersistence.CellRestoreApplied"/> and pass the restored cell's world here from that callback.
    /// A caller that restores its own cache calls this after it applies the cached snapshot.</para>
    /// </remarks>
    /// <returns>The number of newly adopted pickups.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public int Rehydrate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var candidates = new List<(long NetId, PickupState State, Vector3 Position, CellCoord? Cell)>();
        foreach (Entity entity in world.Query()
            .With<PickupState>()
            .With<NetId>()
            .With<ReplicatedPosition>()
            .Entities())
        {
            long netId = world.Get<NetId>(entity).Value;
            if (netId <= 0 || live.ContainsKey(netId)) continue;

            Vector3 position = world.Get<ReplicatedPosition>(entity).Value;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) continue;

            if (!host.TryGetEntity(netId, out World ownerWorld, out Entity ownerEntity)
                || !ReferenceEquals(ownerWorld, world)
                || ownerEntity != entity)
                continue;

            PickupState state = world.Get<PickupState>(entity);
            CellCoord? cell = host.TryGetCellCoord(position.X, position.Z, out CellCoord coord) ? coord : null;
            candidates.Add((netId, state, position, cell));
        }

        candidates.Sort(static (a, b) => a.NetId.CompareTo(b.NetId));
        float radius = Math.Max(0f, config.DefaultRadius);
        float ttl = Math.Max(0f, config.DefaultTimeToLiveSeconds);
        int adopted = 0;
        foreach ((long netId, PickupState state, Vector3 position, CellCoord? cell) in candidates)
        {
            if (live.ContainsKey(netId)) continue;
            live.Add(netId, new Pickup
            {
                PayloadId = state.PayloadId,
                OwnerNetId = state.OwnerNetId,
                Position = position,
                Radius = radius,
                RadiusSquared = radius * radius,
                TimeToLive = ttl,
                Cell = cell,
            });
            adopted++;
        }
        return adopted;
    }
}
