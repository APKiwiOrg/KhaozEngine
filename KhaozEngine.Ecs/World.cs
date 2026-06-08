using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

public sealed class World
{
    private int _nextId = 1;
    private readonly HashSet<int> _entities = new();
    private readonly Dictionary<Type, Dictionary<int, IComponent>> _stores = new();
    private readonly List<int> _pendingDespawn = new();
    private readonly List<ISystem> _systems = new();

    public Entity Spawn()
    {
        var e = new Entity(_nextId++);
        _entities.Add(e.Id);
        return e;
    }

    public void Despawn(Entity e) => _pendingDespawn.Add(e.Id);

    public bool IsAlive(Entity e) => _entities.Contains(e.Id);

    public void Set<T>(Entity e, T component) where T : class, IComponent
    {
        if (!_stores.TryGetValue(typeof(T), out var store))
        {
            store = new Dictionary<int, IComponent>();
            _stores[typeof(T)] = store;
        }
        store[e.Id] = component;
    }

    public bool Has<T>(Entity e) where T : class, IComponent =>
        _stores.TryGetValue(typeof(T), out var store) && store.ContainsKey(e.Id);

    public T Get<T>(Entity e) where T : class, IComponent => (T)_stores[typeof(T)][e.Id];

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

    public List<Entity> Query<T1>() where T1 : class, IComponent
    {
        var result = new List<Entity>();
        if (_stores.TryGetValue(typeof(T1), out var store))
            foreach (var id in store.Keys)
                if (_entities.Contains(id)) result.Add(new Entity(id));
        return result;
    }

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

    public void AddSystem(ISystem system) => _systems.Add(system);

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
