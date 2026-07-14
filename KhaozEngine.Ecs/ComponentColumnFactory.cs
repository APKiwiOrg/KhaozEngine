using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Ecs;

/// <summary>
/// Process-wide, Type-keyed table of reflection-free column factories for component structs. The generic
/// registration path (<see cref="ComponentRegistry.Id{T}"/> and the <see cref="WorldSerializer"/> builder's
/// <c>Add&lt;T&gt;()</c>) records, per component type, a factory closure <c>() =&gt; new Column&lt;T&gt;()</c> and its
/// tag classification. This lets the non-generic <see cref="ComponentRegistry.RegisterType(Type)"/> path (used by
/// world save/load) build a column for a runtime <see cref="Type"/> without <c>Type.MakeGenericType</c> +
/// <c>Activator.CreateInstance</c>, so the load path is NativeAOT-safe once the component set has been registered
/// through the generic seam. Registration is idempotent (first writer wins) and the fast lookup is a dictionary hit,
/// off the per-row hot path.
/// </summary>
internal static class ComponentColumnFactory
{
    /// <summary>A registered component type's reflection-free column factory, its tag classification, and a boxed
    /// <c>default</c> instance for tags (a tag carries no data, so its boxed default is all save needs to serialize its
    /// presence without <c>Activator.CreateInstance</c>). <see cref="TagDefault"/> is null for non-tags.</summary>
    internal readonly record struct Entry(Func<Column> Factory, bool IsTag, object? TagDefault);

    private static readonly ConcurrentDictionary<Type, Entry> _table = new();

    /// <summary>Records the reflection-free column factory + tag flag (+ boxed tag default) for <typeparamref name="T"/>. Idempotent.</summary>
    public static void Register<T>() where T : struct, IComponent
    {
        bool isTag = ComponentTagInfo<T>.IsTag;
        _table.TryAdd(typeof(T), new Entry(static () => new Column<T>(), isTag, isTag ? default(T) : null));
    }

    /// <summary>Looks up a type's registered factory. True when it was registered through the generic seam.</summary>
    public static bool TryGet(Type t, out Entry entry) => _table.TryGetValue(t, out entry);
}

/// <summary>
/// Per-type cached, reflection-free tag classification for the generic component path. A "tag" is a zero-field
/// component struct. <see cref="Type.GetFields(System.Reflection.BindingFlags)"/> cannot be used here: under NativeAOT the trimmer strips
/// field reflection metadata for a struct whose fields are only touched by generated code, so <c>GetFields</c>
/// returns an empty set and every real component would be misclassified as a tag (no column allocated -&gt; the
/// archetype/query path breaks). Instead this derives tag-ness from layout + generated value equality, both of
/// which NativeAOT preserves:
/// <list type="bullet">
///   <item>a struct larger than one byte has fields, so it is never a tag.</item>
///   <item>a one-byte struct is ambiguous (a zero-field tag and a single one-byte field both size 1), so flip the
///   one storage byte and compare by value - a zero-field struct has no field to observe the change (stays equal =&gt;
///   tag), a one-byte field reflects it (becomes unequal =&gt; not a tag).</item>
/// </list>
/// This matches the <c>GetFields</c> classification for every struct while staying reflection-free.
/// </summary>
internal static class ComponentTagInfo<T> where T : struct, IComponent
{
    public static readonly bool IsTag = Compute();

    private static bool Compute()
    {
        if (Unsafe.SizeOf<T>() != 1) return false;   // >1 byte of storage => has fields => not a tag
        T zero = default;
        T flipped = default;
        Unsafe.As<T, byte>(ref flipped) = 0xFF;       // flip the single storage byte
        return EqualityComparer<T>.Default.Equals(zero, flipped);   // unchanged by value => no field => tag
    }
}
