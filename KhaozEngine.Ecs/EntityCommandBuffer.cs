using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>Records structural changes (create/despawn/set/remove) made during iteration and applies
/// them at a safe point via <see cref="Playback"/>. Created entities use negative placeholder ids
/// that resolve to real entities on playback.</summary>
public sealed class EntityCommandBuffer
{
    private enum Op { Create, Despawn, Set, Remove, Defer }

    private readonly List<(Op op, Entity target, int placeholder, Action<World, Entity>? apply)> _cmds = new();
    private int _nextPlaceholder = -1;

    /// <summary>Records creation of a new entity; the returned handle is a placeholder usable in later Set calls.</summary>
    public Entity Create()
    {
        var ph = new Entity(_nextPlaceholder--, 0);
        _cmds.Add((Op.Create, ph, ph.Id, null));
        return ph;
    }

    public void Despawn(Entity e) => _cmds.Add((Op.Despawn, e, 0, null));

    public void Set<T>(Entity e, T value) where T : struct, IComponent =>
        _cmds.Add((Op.Set, e, 0, (w, target) => w.Set(target, value)));

    public void Remove<T>(Entity e) where T : struct, IComponent =>
        _cmds.Add((Op.Remove, e, 0, (w, target) => w.Remove<T>(target)));

    /// <summary>Records an arbitrary deferred action, run in record order during <see cref="Playback"/>
    /// (interleaved with structural ops). Put non-structural deterministic logic - counters, RNG rolls - here.</summary>
    public void Defer(Action<World> action) =>
        _cmds.Add((Op.Defer, default, 0, (w, _) => action(w)));

    /// <summary>Applies all recorded commands in order, then clears the buffer.</summary>
    public void Playback(World world)
    {
        var resolved = new Dictionary<int, Entity>();   // placeholder id -> real entity
        foreach (var c in _cmds)
        {
            if (c.op == Op.Create) { resolved[c.placeholder] = world.Spawn(); continue; }

            Entity target = Resolve(c.target, resolved);
            switch (c.op)
            {
                case Op.Despawn: world.Despawn(target); break;
                case Op.Set: c.apply!(world, target); break;
                case Op.Remove: c.apply!(world, target); break;
                case Op.Defer: c.apply!(world, default); break;
            }
        }
        _cmds.Clear();
    }

    private static Entity Resolve(Entity e, Dictionary<int, Entity> resolved) =>
        e.Id < 0 && resolved.TryGetValue(e.Id, out Entity real) ? real : e;
}
