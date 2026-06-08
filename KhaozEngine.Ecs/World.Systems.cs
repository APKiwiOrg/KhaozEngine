using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>A unit of per-frame logic. Run in registration order by <see cref="World.Update"/>.</summary>
public interface ISystem
{
    void Update(World world, float dt);
}

public sealed partial class World
{
    private readonly List<ISystem> _systems = new();
    private readonly Dictionary<Type, object> _resources = new();

    /// <summary>Registers a system. Systems run in registration order each <see cref="Update"/>.</summary>
    public void AddSystem(ISystem system) => _systems.Add(system);

    /// <summary>Runs every system in order.</summary>
    public void Update(float dt)
    {
        for (int i = 0; i < _systems.Count; i++)
            _systems[i].Update(this, dt);
    }

    /// <summary>Stores a world-global singleton of type <typeparamref name="T"/>.</summary>
    public void SetResource<T>(T value) where T : class => _resources[typeof(T)] = value;

    /// <summary>Gets the world-global singleton of type <typeparamref name="T"/>. Throws if unset.</summary>
    public T GetResource<T>() where T : class => (T)_resources[typeof(T)];

    /// <summary>True if a resource of type <typeparamref name="T"/> has been set.</summary>
    public bool HasResource<T>() where T : class => _resources.ContainsKey(typeof(T));
}
