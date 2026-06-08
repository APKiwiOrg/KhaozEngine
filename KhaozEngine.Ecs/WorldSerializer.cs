using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Ecs;

/// <summary>
/// Saves and loads a <see cref="World"/> (entities + components + id-allocator state) as JSON.
/// Construct it with the component types your game uses (or scan an assembly). Entities are restored
/// at their exact id and version so <see cref="Entity"/>-typed component fields survive the round-trip.
/// Resources and systems are not serialized.
/// </summary>
public sealed class WorldSerializer
{
    private readonly Dictionary<string, Type> _byName = new();
    private readonly JsonSerializerOptions _options;

    /// <param name="componentTypes">Each must be a <c>struct</c> implementing <see cref="IComponent"/>.</param>
    public WorldSerializer(params Type[] componentTypes) : this(componentTypes, null) { }

    /// <param name="componentTypes">Each must be a <c>struct</c> implementing <see cref="IComponent"/>.</param>
    /// <param name="options">Optional JSON options; defaults to <c>IncludeFields = true</c>. Add
    /// converters here for value types that don't round-trip by default (e.g. MonoGame Color).</param>
    public WorldSerializer(IEnumerable<Type> componentTypes, JsonSerializerOptions? options)
    {
        _options = options ?? new JsonSerializerOptions { IncludeFields = true };
        foreach (Type t in componentTypes)
        {
            if (!t.IsValueType || t.IsAbstract || !typeof(IComponent).IsAssignableFrom(t))
                throw new ArgumentException($"{t.FullName} is not a struct implementing IComponent.");
            _byName[t.FullName!] = t;
        }
        // The built-in Parent component lives in the engine assembly, so callers' type lists/scans
        // won't include it; auto-register so hierarchies serialize.
        _byName[typeof(Parent).FullName!] = typeof(Parent);
    }

    /// <summary>Builds a serializer from every <c>struct : IComponent</c> in <typeparamref name="T"/>'s assembly.</summary>
    public static WorldSerializer FromAssemblyOf<T>(JsonSerializerOptions? options = null)
    {
        var types = typeof(T).Assembly.GetTypes()
            .Where(t => t.IsValueType && !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t));
        return new WorldSerializer(types, options);
    }

    /// <summary>Serializes the world to a JSON string.</summary>
    public string Save(World world)
    {
        var doc = new SaveDoc
        {
            NextId = world.SaveNextId,
            FreeIds = world.SaveFreeSlots.Select(s => new FreeSlot { Id = s.id, Version = s.version }).ToList(),
        };
        ComponentRegistry reg = world.Registry;
        foreach (Archetype arch in world.SaveArchetypes)
        {
            for (int row = 0; row < arch.Count; row++)
            {
                Entity e = arch.Entities[row];
                var ed = new EntityDoc { Id = e.Id, Version = e.Version };
                foreach (int tid in arch.TypeIds)
                {
                    Type t = reg.TypeOf(tid);
                    object value = reg.IsTag(tid)
                        ? Activator.CreateInstance(t)!
                        : arch.Columns[tid].GetBoxed(row);
                    ed.Components[t.FullName!] = JsonSerializer.SerializeToElement(value, t, _options);
                }
                doc.Entities.Add(ed);
            }
        }
        return JsonSerializer.Serialize(doc, _options);
    }

    /// <summary>Deserializes a world from a JSON string.</summary>
    public World Load(string json)
    {
        SaveDoc doc = JsonSerializer.Deserialize<SaveDoc>(json, _options)
            ?? throw new InvalidOperationException("Empty or invalid save document.");
        var world = new World();
        foreach (EntityDoc ed in doc.Entities)
        {
            Entity e = world.CreateAt(ed.Id, ed.Version);
            foreach (var (name, element) in ed.Components)
            {
                if (!_byName.TryGetValue(name, out Type? t))
                    throw new InvalidOperationException(
                        $"Unknown component type '{name}' on load. Register it with the WorldSerializer.");
                object value = element.Deserialize(t, _options)!;
                world.SetByType(e, t, value);
            }
        }
        world.RestoreAllocator(doc.NextId, doc.FreeIds.Select(f => (f.Id, f.Version)));
        world.RebuildHierarchyIndex();
        return world;
    }

    /// <summary>Serializes the world to a stream (UTF-8 text).</summary>
    public void Save(World world, Stream stream)
    {
        using var w = new StreamWriter(stream, leaveOpen: true);
        w.Write(Save(world));
    }

    /// <summary>Deserializes a world from a stream (UTF-8 text).</summary>
    public World Load(Stream stream)
    {
        using var r = new StreamReader(stream, leaveOpen: true);
        return Load(r.ReadToEnd());
    }

    private sealed class SaveDoc
    {
        public int FormatVersion { get; set; } = 1;
        public int NextId { get; set; }
        public List<FreeSlot> FreeIds { get; set; } = new();
        public List<EntityDoc> Entities { get; set; } = new();
    }

    private sealed class FreeSlot { public int Id { get; set; } public uint Version { get; set; } }

    private sealed class EntityDoc
    {
        public int Id { get; set; }
        public uint Version { get; set; }
        public Dictionary<string, JsonElement> Components { get; set; } = new();
    }
}
