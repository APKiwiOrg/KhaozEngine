using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KhaozEngine.Tests")]

namespace KhaozEngine.Ecs;

/// <summary>Per-world registry: assigns each component type a dense id, records whether it is a
/// zero-field "tag", and can build the right <see cref="Column"/> for an id.</summary>
internal sealed class ComponentRegistry
{
    private readonly Dictionary<Type, int> _ids = new();
    private readonly List<bool> _isTag = new();
    private readonly List<Func<Column>> _factories = new();

    public int Id<T>() where T : struct, IComponent
    {
        Type t = typeof(T);
        if (_ids.TryGetValue(t, out int id)) return id;
        id = _ids.Count;
        _ids[t] = id;
        _isTag.Add(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0);
        _factories.Add(static () => new Column<T>());
        return id;
    }

    public bool IsTag(int id) => _isTag[id];
    public Column CreateColumn(int id) => _factories[id]();
}
