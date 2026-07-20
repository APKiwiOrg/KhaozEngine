using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // Order contract: Added<T>()/Changed<T>() enumerate in first-track insertion order. Downstream
    // determinism-sensitive code (replication capture, anything hashing iteration results) relies on a
    // stable, hash-seed-independent order, so the HashSet is only the O(1) dedup check and the paired
    // List is the enumeration source. Do not iterate the sets.
    private readonly HashSet<(Entity entity, int typeId)> _added = new();
    private readonly HashSet<(Entity entity, int typeId)> _changed = new();
    private readonly List<(Entity entity, int typeId)> _addedOrder = new();
    private readonly List<(Entity entity, int typeId)> _changedOrder = new();
    private readonly Dictionary<int, List<Entity>> _removed = new();

    /// <summary>Monotonic frame counter advanced by <see cref="AdvanceTick"/>.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Advances the frame tick and clears the per-tick change sets. Call once per frame.</summary>
    public void AdvanceTick()
    {
        Tick++;
        _added.Clear();
        _changed.Clear();
        _addedOrder.Clear();
        _changedOrder.Clear();
        _removed.Clear();
        ClearEvents();
    }

    /// <summary>Records a value-mutation of component <typeparamref name="T"/> on <paramref name="e"/>
    /// (for <c>ref</c> writes the ECS can't see). No-op if the entity lacks the component.</summary>
    public void MarkChanged<T>(Entity e) where T : struct, IComponent
    {
        int id = Reg.Id<T>();
        if (Has<T>(e) && _changed.Add((e, id)))
            _changedOrder.Add((e, id));
    }

    /// <summary>Entities that gained component <typeparamref name="T"/> this tick (live only).</summary>
    public IEnumerable<Entity> Added<T>() where T : struct, IComponent => ByType(_addedOrder, Reg.Id<T>());

    /// <summary>Entities whose component <typeparamref name="T"/> value changed this tick (live only).</summary>
    public IEnumerable<Entity> Changed<T>() where T : struct, IComponent => ByType(_changedOrder, Reg.Id<T>());

    /// <summary>Entities that lost component <typeparamref name="T"/> this tick. May include dead
    /// (despawned) entities; filter with <c>.Where(world.IsAlive)</c> for survivors.</summary>
    public IEnumerable<Entity> Removed<T>() where T : struct, IComponent =>
        _removed.TryGetValue(Reg.Id<T>(), out List<Entity>? list) ? list : Enumerable.Empty<Entity>();

    private IEnumerable<Entity> ByType(List<(Entity entity, int typeId)> order, int id)
    {
        foreach (var (entity, typeId) in order)
            if (typeId == id && IsAlive(entity))
                yield return entity;
    }

    private void TrackAddedOrChanged(Entity e, int id, bool adding)
    {
        if (adding)
        {
            if (_added.Add((e, id))) _addedOrder.Add((e, id));
        }
        else
        {
            if (_changed.Add((e, id))) _changedOrder.Add((e, id));
        }
    }

    private void TrackRemoved(Entity e, int id)
    {
        if (!_removed.TryGetValue(id, out List<Entity>? list))
        {
            list = new List<Entity>();
            _removed[id] = list;
        }
        list.Add(e);
    }
}
