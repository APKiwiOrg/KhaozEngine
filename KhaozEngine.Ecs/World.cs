using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>
/// A minimal entity-component-system world. Holds entities, their components (stored by type),
/// and a list of systems run each <see cref="Update(float)"/>. Despawns are deferred until the
/// end of the update so systems can iterate safely.
/// </summary>
public sealed class World
{
    private int _nextId = 1;
    private readonly HashSet<int> _entities = new();
    private readonly Dictionary<Type, Dictionary<int, IComponent>> _stores = new();
    private readonly List<int> _pendingDespawn = new();
    private readonly List<ISystem> _systems = new();

    /// <summary>Creates a new entity with a fresh id and returns its handle.</summary>
    public Entity Spawn()
    {
        var e = new Entity(_nextId++);
        _entities.Add(e.Id);
        return e;
    }

    /// <summary>
    /// Marks an entity for removal. The entity stays alive until the next
    /// <see cref="Update(float)"/> flushes despawns.
    /// </summary>
    public void Despawn(Entity e) => _pendingDespawn.Add(e.Id);

    /// <summary>Returns true if the entity has not yet been removed.</summary>
    public bool IsAlive(Entity e) => _entities.Contains(e.Id);

    /// <summary>Attaches (or replaces) a component of type <typeparamref name="T"/> on the entity.</summary>
    public void Set<T>(Entity e, T component) where T : class, IComponent
    {
        if (!_stores.TryGetValue(typeof(T), out var store))
        {
            store = new Dictionary<int, IComponent>();
            _stores[typeof(T)] = store;
        }
        store[e.Id] = component;
    }

    /// <summary>Returns true if the entity has a component of type <typeparamref name="T"/>.</summary>
    public bool Has<T>(Entity e) where T : class, IComponent =>
        _stores.TryGetValue(typeof(T), out var store) && store.ContainsKey(e.Id);

    /// <summary>Gets the entity's component of type <typeparamref name="T"/>. Throws if absent.</summary>
    public T Get<T>(Entity e) where T : class, IComponent => (T)_stores[typeof(T)][e.Id];

    /// <summary>Tries to get the entity's component of type <typeparamref name="T"/>.</summary>
    /// <returns>True if present; otherwise false and <paramref name="component"/> is null.</returns>
    public bool TryGet<T>(Entity e, out T component) where T : class, IComponent
    {
        if (_stores.TryGetValue(typeof(T), out var store) && store.TryGetValue(e.Id, out var c))
        {
            component = (T)c;
            return true;
        }
        component = null!;
        return false;
    }

    /// <summary>Returns all live entities that have a component of type <typeparamref name="T1"/>.</summary>
    public List<Entity> Query<T1>() where T1 : class, IComponent
    {
        var result = new List<Entity>();
        if (_stores.TryGetValue(typeof(T1), out var store))
            foreach (var id in store.Keys)
                if (_entities.Contains(id)) result.Add(new Entity(id));
        return result;
    }

    /// <summary>Returns all live entities that have both component types.</summary>
    public List<Entity> Query<T1, T2>() where T1 : class, IComponent where T2 : class, IComponent
    {
        var result = new List<Entity>();
        if (!_stores.TryGetValue(typeof(T1), out var store)) return result;
        foreach (var id in store.Keys)
        {
            var e = new Entity(id);
            if (_entities.Contains(id) && Has<T2>(e)) result.Add(e);
        }
        return result;
    }

    /// <summary>Returns all live entities that have all three component types.</summary>
    public List<Entity> Query<T1, T2, T3>()
        where T1 : class, IComponent where T2 : class, IComponent where T3 : class, IComponent
    {
        var result = new List<Entity>();
        if (!_stores.TryGetValue(typeof(T1), out var store)) return result;
        foreach (var id in store.Keys)
        {
            var e = new Entity(id);
            if (_entities.Contains(id) && Has<T2>(e) && Has<T3>(e)) result.Add(e);
        }
        return result;
    }

    /// <summary>Registers a system. Systems run in registration order each <see cref="Update(float)"/>.</summary>
    public void AddSystem(ISystem system) => _systems.Add(system);

    /// <summary>Runs every system in order, then flushes any pending despawns.</summary>
    /// <param name="dt">Elapsed time since the last update, in seconds.</param>
    public void Update(float dt)
    {
        for (int i = 0; i < _systems.Count; i++)
            _systems[i].Update(this, dt);
        FlushDespawns();
    }

    private void FlushDespawns()
    {
        if (_pendingDespawn.Count == 0) return;
        foreach (var id in _pendingDespawn)
        {
            _entities.Remove(id);
            foreach (var store in _stores.Values) store.Remove(id);
        }
        _pendingDespawn.Clear();
    }
}
