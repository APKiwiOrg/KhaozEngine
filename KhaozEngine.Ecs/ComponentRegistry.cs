using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KhaozEngine.Tests")]

namespace KhaozEngine.Ecs;

/// <summary>Per-world registry: assigns each component type a dense id, records whether it is a
/// zero-field "tag", and can build the right <see cref="Column"/> for an id.</summary>
internal sealed class ComponentRegistry
{
    private readonly Dictionary<Type, int> _ids = new();
    private readonly List<Type> _types = new();
    private readonly List<bool> _isTag = new();
    private readonly List<Func<Column>> _factories = new();
    // Boxed default instance per id, populated only for tags (null otherwise). Save uses it to serialize a tag's
    // presence without Activator.CreateInstance, keeping the save path reflection-free / NativeAOT-safe.
    private readonly List<object?> _tagDefaults = new();

    public int Id<T>() where T : struct, IComponent
    {
        Type t = typeof(T);
        if (_ids.TryGetValue(t, out int id)) return id;
        id = _ids.Count;
        _ids[t] = id;
        _types.Add(t);
        bool isTag = ComponentTagInfo<T>.IsTag;   // reflection-free, so this hot path is NativeAOT-safe (see ComponentTagInfo)
        _isTag.Add(isTag);
        _tagDefaults.Add(isTag ? default(T) : null);
        _factories.Add(static () => new Column<T>());
        // Record the reflection-free column factory so the non-generic load path (RegisterType) can rebuild a
        // Column<T> for this runtime Type without MakeGenericType. Slow path only (per world+type, TryAdd is a no-op
        // after the first), so it stays off the per-row hot path.
        ComponentColumnFactory.Register<T>();
        return id;
    }

    /// <summary>Non-generic registration (used by load/serialization). Returns the existing id if already registered.
    /// Uses the reflection-free column-factory table populated by the generic <see cref="Id{T}"/> path or the
    /// <see cref="WorldSerializer"/> builder; falls back to reflection (JIT only) for a type that was never registered
    /// through the generic seam.</summary>
    public int RegisterType(Type t)
    {
        if (_ids.TryGetValue(t, out int id)) return id;
        if (!t.IsValueType || !typeof(IComponent).IsAssignableFrom(t))
            throw new ArgumentException($"{t.FullName} is not a struct implementing IComponent.");
        id = _ids.Count;

        // Reflection-free fast path: the type's column factory + tag flag were registered through the generic seam
        // (Id<T> or WorldSerializer.Create().Add<T>()). This is the only path reachable under NativeAOT.
        if (ComponentColumnFactory.TryGet(t, out ComponentColumnFactory.Entry entry))
        {
            _ids[t] = id;
            _types.Add(t);
            _isTag.Add(entry.IsTag);
            _tagDefaults.Add(entry.TagDefault);
            _factories.Add(entry.Factory);
            return id;
        }

        // Not pre-registered through the generic path. Under NativeAOT there is no way to build Column<T> for a
        // runtime Type, so fail with an actionable message. RuntimeFeature.IsDynamicCodeSupported is a false constant
        // under NativeAOT, so ILC removes the reflection call below and never emits a trim/AOT warning for it.
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new NotSupportedException(
                $"Component type '{t.FullName}' was not registered for NativeAOT world load. Register it through the " +
                $"generic seam (WorldSerializer.Create().Add<{t.Name}>(), or touch it via the generic ECS API) before loading.");
        return RegisterTypeReflect(t, id);
    }

    // Reflection fallback for the non-generic load path (JIT only). Off the per-tick / replication hot path and
    // unreachable under NativeAOT (see the guard in RegisterType). MakeGenericType + Activator build Column<T> for a
    // runtime Type, and GetFields classifies tags; both need dynamic code / trimmed metadata, hence the annotations.
    [RequiresDynamicCode("Builds Column<T> via Type.MakeGenericType; register component types through the generic WorldSerializer seam under NativeAOT.")]
    [RequiresUnreferencedCode("Reflects over the component type to build its column and classify tags; register component types through the generic WorldSerializer seam under NativeAOT.")]
    private int RegisterTypeReflect(Type t, int id)
    {
        _ids[t] = id;
        _types.Add(t);
        bool isTag = IsTagType(t);
        _isTag.Add(isTag);
        _tagDefaults.Add(isTag ? Activator.CreateInstance(t) : null);
        Type columnType = typeof(Column<>).MakeGenericType(t);
        _factories.Add(() => (Column)Activator.CreateInstance(columnType)!);
        return id;
    }

    public Type TypeOf(int id) => _types[id];
    public bool IsTag(int id) => _isTag[id];
    public Column CreateColumn(int id) => _factories[id]();

    /// <summary>A boxed <c>default</c> instance of the tag at <paramref name="id"/>. Only valid for tag ids
    /// (<see cref="IsTag"/>); used by save to serialize a tag's presence without reflection.</summary>
    public object TagInstance(int id) => _tagDefaults[id]!;

    // Reflection-based tag test for the reflection fallback in RegisterTypeReflect only. GetFields here would return
    // an incomplete set under NativeAOT (field reflection metadata is trimmed), which is exactly why the generic path
    // uses the reflection-free ComponentTagInfo instead.
    [RequiresUnreferencedCode("Uses Type.GetFields, which is trimmed under NativeAOT; the generic path uses ComponentTagInfo instead.")]
    private static bool IsTagType(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0;
}
