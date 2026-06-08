using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>A unit of per-frame logic. Run in registration order within its group by <see cref="World.Update"/>.</summary>
public interface ISystem
{
    void Update(World world, float dt);
}

public sealed partial class World
{
    private const string DefaultGroup = "default";
    private readonly Dictionary<string, List<ISystem>> _groups = new();   // group -> systems (registration order)
    private readonly List<string> _groupOrder = new();                    // group run order
    private readonly Dictionary<Type, object> _resources = new();

    /// <summary>Deferred structural changes recorded by systems during iteration. Played back (and cleared)
    /// after each system runs, so one system's changes are visible to the next.</summary>
    public EntityCommandBuffer Commands { get; } = new();

    /// <summary>The current group run order.</summary>
    public IReadOnlyList<string> SystemGroups => _groupOrder;

    /// <summary>Registers a system in a named group (created on first use). Systems run in registration order within their group.</summary>
    public void AddSystem(ISystem system, string group = DefaultGroup) => GetOrCreateGroup(group).Add(system);

    /// <summary>Defines the group run order: the listed groups first (in order), then any other existing group in its current order. Listed groups are created if new.</summary>
    public void SetGroupOrder(params string[] groups)
    {
        foreach (string g in groups) GetOrCreateGroup(g);

        var ordered = new List<string>();
        var seen = new HashSet<string>();
        foreach (string g in groups)
            if (seen.Add(g)) ordered.Add(g);
        foreach (string g in _groupOrder)
            if (!seen.Contains(g)) ordered.Add(g);

        _groupOrder.Clear();
        _groupOrder.AddRange(ordered);
    }

    /// <summary>Runs every group in order, flushing <see cref="Commands"/> after each system.</summary>
    public void Update(float dt)
    {
        for (int i = 0; i < _groupOrder.Count; i++)
            RunGroup(_groups[_groupOrder[i]], dt);
    }

    /// <summary>Runs a single group's systems in registration order, flushing <see cref="Commands"/> after each. Throws if the group does not exist.</summary>
    public void UpdateGroup(string group, float dt)
    {
        if (!_groups.TryGetValue(group, out List<ISystem>? systems))
            throw new ArgumentException($"No system group named '{group}'.", nameof(group));
        RunGroup(systems, dt);
    }

    private void RunGroup(List<ISystem> systems, float dt)
    {
        for (int i = 0; i < systems.Count; i++)
        {
            systems[i].Update(this, dt);
            Commands.Playback(this);
        }
    }

    private List<ISystem> GetOrCreateGroup(string group)
    {
        if (!_groups.TryGetValue(group, out List<ISystem>? list))
        {
            list = new List<ISystem>();
            _groups[group] = list;
            _groupOrder.Add(group);
        }
        return list;
    }

    /// <summary>Stores a world-global singleton of type <typeparamref name="T"/>.</summary>
    public void SetResource<T>(T value) where T : class => _resources[typeof(T)] = value;

    /// <summary>Gets the world-global singleton of type <typeparamref name="T"/>. Throws if unset.</summary>
    public T GetResource<T>() where T : class => (T)_resources[typeof(T)];

    /// <summary>True if a resource of type <typeparamref name="T"/> has been set.</summary>
    public bool HasResource<T>() where T : class => _resources.ContainsKey(typeof(T));
}
