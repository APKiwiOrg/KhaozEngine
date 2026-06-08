using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    private readonly Dictionary<Type, List<object>> _events = new();

    /// <summary>Records a typed event for this tick. Read via <see cref="Events{T}"/>; cleared by
    /// <see cref="AdvanceTick"/>. Value-type events are boxed (events are infrequent).</summary>
    public void Emit<T>(T evt)
    {
        if (!_events.TryGetValue(typeof(T), out List<object>? list))
        {
            list = new List<object>();
            _events[typeof(T)] = list;
        }
        list.Add(evt!);
    }

    /// <summary>This tick's events of type <typeparamref name="T"/>, in emission order (empty if none).</summary>
    public IEnumerable<T> Events<T>() =>
        _events.TryGetValue(typeof(T), out List<object>? list) ? list.Cast<T>() : Enumerable.Empty<T>();

    internal void ClearEvents() => _events.Clear();
}
