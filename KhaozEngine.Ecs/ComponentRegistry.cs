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
    private readonly List<Type> _types = new();
    private readonly List<bool> _isTag = new();
    private readonly List<Func<Column>> _factories = new();

    public int Id<T>() where T : struct, IComponent
    {
        Type t = typeof(T);
        if (_ids.TryGetValue(t, out int id)) return id;
        id = _ids.Count;
        _ids[t] = id;
        _types.Add(t);
        _isTag.Add(TagInfo<T>.IsTag);   // reflection-free, so this hot path is NativeAOT-safe (see TagInfo)
        _factories.Add(static () => new Column<T>());
        return id;
    }

    /// <summary>Non-generic registration (used by load/serialization). Returns the existing id if already registered.</summary>
    public int RegisterType(Type t)
    {
        if (_ids.TryGetValue(t, out int id)) return id;
        if (!t.IsValueType || !typeof(IComponent).IsAssignableFrom(t))
            throw new ArgumentException($"{t.FullName} is not a struct implementing IComponent.");
        id = _ids.Count;
        _ids[t] = id;
        _types.Add(t);
        _isTag.Add(IsTagType(t));
        Type columnType = typeof(Column<>).MakeGenericType(t);
        _factories.Add(() => (Column)Activator.CreateInstance(columnType)!);
        return id;
    }

    public Type TypeOf(int id) => _types[id];
    public bool IsTag(int id) => _isTag[id];
    public Column CreateColumn(int id) => _factories[id]();

    // Reflection-based tag test for the NON-generic RegisterType path only (JSON world save/load). That path already
    // relies on runtime type discovery (MakeGenericType + Activator above), so it is not NativeAOT-safe and is off the
    // per-tick / replication hot path. GetFields here would return an incomplete set under NativeAOT (field reflection
    // metadata is trimmed), which is exactly why the generic Id<T> path uses the reflection-free TagInfo instead.
    private static bool IsTagType(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0;

    // Per-type cached, reflection-free tag classification for the generic Id<T> path. A "tag" is a zero-field
    // component struct. Type.GetFields cannot be used here: under NativeAOT the trimmer strips field reflection
    // metadata for a struct whose fields are only touched by generated code, so GetFields returns an empty set and
    // every real component would be misclassified as a tag (no column allocated -> the archetype/query path breaks).
    // Instead this derives tag-ness from layout + generated value equality, both of which NativeAOT preserves:
    //   - a struct larger than one byte has fields, so it is never a tag.
    //   - a one-byte struct is ambiguous (a zero-field tag and a single one-byte field both size 1), so flip the one
    //     storage byte and compare by value - a zero-field struct has no field to observe the change (stays equal =>
    //     tag), a one-byte field reflects it (becomes unequal => not a tag).
    // This matches the GetFields classification for every struct while staying reflection-free.
    private static class TagInfo<T> where T : struct, IComponent
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
}
