using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Sharding;

public sealed partial class ShardHost
{
    /// <summary>
    /// Whether any cell holds a live entity with <paramref name="netId"/> frozen as <see cref="Migrating"/>.
    /// The source stays frozen until the destination acknowledges, including deferred admission or a delayed
    /// <see cref="ICellLink"/>. A missing current owner during this interval does not mean the entity is gone.
    /// </summary>
    public bool IsInHandoff(long netId)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            World world = ordered[i].World;
            bool found = false;
            world.ForEach<Migrating>((Entity entity, ref Migrating _) =>
            {
                if (world.IsAlive(entity) && world.TryGet(entity, out NetId id) && id.Value == netId)
                    found = true;
            });
            if (found) return true;
        }
        return false;
    }
}
