using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Serialization;

namespace KhaozEngine.Ecs;

/// <summary>
/// Saves and loads a <see cref="World"/> (entities + components + id-allocator state) as JSON.
/// Construct it with the component types your game uses (or scan an assembly). Entities are restored
/// at their exact id and version so <see cref="Entity"/>-typed component fields survive the round-trip.
/// Resources and systems are not serialized.
/// </summary>
public sealed class WorldSerializer
{
    /// <summary>The save <c>FormatVersion</c> this build writes and is the newest it can read.</summary>
    public const int CurrentFormatVersion = 1;

    // Document-level upgrade hooks, keyed by the version they upgrade FROM (ascending). A migration
    // registered at N takes a document at version N and returns it at version N+1. Load applies every
    // migration from the save's version up to CurrentFormatVersion before deserializing.
    private static readonly SortedDictionary<int, Func<JsonObject, JsonObject>> _migrations = new();

    /// <summary>
    /// Registers a document-level upgrade from <paramref name="fromVersion"/> to <paramref name="fromVersion"/>+1.
    /// On <see cref="Load(string)"/> of an older save, registered migrations run in ascending order to bring the
    /// document up to <see cref="CurrentFormatVersion"/> before it is deserialized. The hook receives and returns
    /// the raw <see cref="JsonObject"/> save document.
    /// </summary>
    public static void RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> upgrade)
        => _migrations[fromVersion] = upgrade ?? throw new ArgumentNullException(nameof(upgrade));

    // Resolves the persistence key for a component type: its [ComponentId] if present, else Type.FullName.
    private static string KeyFor(Type t) => t.GetCustomAttribute<ComponentIdAttribute>()?.Id ?? t.FullName!;

    private readonly Dictionary<string, Type> _byName = new();
    private readonly JsonSerializerOptions _options;

    /// <param name="componentTypes">Each must be a <c>struct</c> implementing <see cref="IComponent"/>.</param>
    public WorldSerializer(params Type[] componentTypes) : this(componentTypes, null) { }

    /// <param name="componentTypes">Each must be a <c>struct</c> implementing <see cref="IComponent"/>.</param>
    /// <param name="options">Optional JSON options; defaults to <c>IncludeFields = true</c>. Add
    /// converters here for value types that don't round-trip by default (e.g. MonoGame Color).</param>
    public WorldSerializer(IEnumerable<Type> componentTypes, JsonSerializerOptions? options)
    {
        _options = options ?? JsonDefaults.IncludeFields;
        foreach (Type t in componentTypes)
        {
            if (!t.IsValueType || t.IsAbstract || !typeof(IComponent).IsAssignableFrom(t))
                throw new ArgumentException($"{t.FullName} is not a struct implementing IComponent.");
            _byName[KeyFor(t)] = t;
        }
        // The built-in Parent component lives in the engine assembly, so callers' type lists/scans
        // won't include it; auto-register so hierarchies serialize.
        _byName[KeyFor(typeof(Parent))] = typeof(Parent);
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
                    ed.Components[KeyFor(t)] = JsonSerializer.SerializeToElement(value, t, _options);
                }
                doc.Entities.Add(ed);
            }
        }
        return JsonSerializer.Serialize(doc, _options);
    }

    /// <summary>Deserializes a world from a JSON string.</summary>
    public World Load(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
            throw new InvalidOperationException("Empty or invalid save document.");

        // Read the version BEFORE deserializing so a future save is rejected, not mis-read. A missing
        // FormatVersion is treated as version 1: pre-existing saves always wrote 1, and that is the
        // lowest known version.
        int found = root.TryGetPropertyValue("FormatVersion", out JsonNode? fv) && fv is not null
            ? fv.GetValue<int>()
            : CurrentFormatVersion;

        if (found > CurrentFormatVersion)
            throw new UnsupportedSaveVersionException(found, CurrentFormatVersion);

        // Older save: bring it up to the current version with any registered migrations (ascending).
        for (int v = found; v < CurrentFormatVersion; v++)
        {
            if (!_migrations.TryGetValue(v, out Func<JsonObject, JsonObject>? upgrade))
                throw new InvalidOperationException(
                    $"No migration registered to upgrade save FormatVersion {v} to {v + 1}.");
            root = upgrade(root);
            root["FormatVersion"] = v + 1;
        }

        SaveDoc doc = root.Deserialize<SaveDoc>(_options)
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
        public int FormatVersion { get; set; } = CurrentFormatVersion;
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
