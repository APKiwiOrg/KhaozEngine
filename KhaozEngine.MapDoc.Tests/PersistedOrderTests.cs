using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Pins the serialized member order of the persisted types declared across partial files. Reflection
/// would otherwise order their members by which file the compiler met first, so moving a part to a new file
/// would change saved bytes and tile content hashes with no member changing (#1469).</summary>
public class PersistedOrderTests
{
    const string Deliberate =
        "Saved documents, tile files and content hashes depend on this order. If the change is deliberate, it is " +
        "a format change: update KhaozEngine.MapDoc/MapPersistedOrder.cs and this expectation together.";

    static readonly string[] DocumentOrder =
    {
        "$schema", "formatVersion", "id", "displayName", "bounds", "tileSize", "terrain", "scatterLayers",
        "companionLayers", "exclusions", "scatterOverrides", "placements", "spawns", "playerSpawns", "regions",
        "terrainOverrides", "prefabs", "buildingIntent", "nativeResourceTransforms", "supportRecipe", "surfaces",
        "surfacePatches", "water", "playableBounds", "nativeAssets", "numericIdHighWaterMark", "resolverIdentity",
    };

    static readonly string[] PlacementOrder =
    {
        "id", "kind", "x", "z", "y", "yaw", "scale", "tags", "supportBinding", "numericId", "assetId", "displayName",
    };

    [Fact]
    public void DocumentMemberOrder_IsPinned() =>
        AssertOrder(nameof(MapDocument), DocumentOrder, Persisted(DocumentOptions().GetTypeInfo(typeof(MapDocument))));

    [Fact]
    public void PlacementMemberOrder_IsPinnedInDocumentsTilesAndPrefabs()
    {
        AssertOrder(nameof(MapPlacement), PlacementOrder, Persisted(DocumentOptions().GetTypeInfo(typeof(MapPlacement))));
        AssertOrder(nameof(MapPlacement) + " (prefab payload)", PlacementOrder,
            Persisted(MapPrefabJson.ToolOptions.GetTypeInfo(typeof(MapPlacement))));
    }

    [Fact]
    public void SavedDocument_WritesMembersInThePinnedOrder()
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        doc.Schema = "mapdoc.schema.json";
        doc.TerrainOverrides = new MapTerrainOverrides();
        doc.TerrainOverrides.SetDelta(4, 4, 0.5f);
        doc.NumericIdHighWaterMark++;
        doc.Placements.Add(new MapPlacement
        {
            Id = "bound-1",
            Kind = "scenery",
            AssetId = "tree",
            NumericId = doc.NumericIdHighWaterMark,
            DisplayName = "Bound oak",
            X = 61.5f,
            Z = 1.5f,
            SupportBinding = new(MapSupportBindingKind.Surface, "ground", null, null, null, null),
        });
        doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f));

        using JsonDocument saved = JsonDocument.Parse(MapDocumentFile.SaveText(doc));
        string[] written = saved.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        AssertOrder(nameof(MapDocument) + " (saved)", DocumentOrder.Where(written.Contains).ToArray(), written);

        foreach (JsonElement placement in saved.RootElement.GetProperty("placements").EnumerateArray())
        {
            string[] members = placement.EnumerateObject().Select(p => p.Name).ToArray();
            AssertOrder(nameof(MapPlacement) + " (saved)", PlacementOrder.Where(members.Contains).ToArray(), members);
        }
        JsonElement bound = saved.RootElement.GetProperty("placements").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "bound-1");
        Assert.Equal(PlacementOrder.Where(name => name != "y"), bound.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>The guard for the #1453 class of change. Every persisted member of a type declared across partial
    /// files must carry an explicit order, so a member added in a new part, or a part moved to a new file, cannot
    /// take a position from file layout.</summary>
    [Fact]
    public void MultiPartPersistedTypes_OrderEveryPersistedMember()
    {
        HashSet<string> multiPart = MultiPartTypeNames();
        List<Type> checkedTypes = PersistedObjectTypes()
            .Where(type => multiPart.Contains(SimpleName(type))).ToList();
        Assert.Contains(typeof(MapDocument), checkedTypes);
        Assert.Contains(typeof(MapPlacement), checkedTypes);

        var failures = new List<string>();
        foreach (Type type in checkedTypes)
        {
            JsonTypeInfo info = DocumentOptions().GetTypeInfo(type);
            var seen = new Dictionary<int, string>();
            foreach (JsonPropertyInfo property in info.Properties.Where(IsPersisted))
            {
                MemberInfo member = (MemberInfo)property.AttributeProvider!;
                if (member.GetCustomAttribute<JsonPropertyOrderAttribute>() is not { } order)
                    failures.Add($"{type.Name}.{member.Name} has no JsonPropertyOrder.");
                else if (!seen.TryAdd(order.Order, member.Name))
                    failures.Add($"{type.Name}.{member.Name} shares order {order.Order} with {seen[order.Order]}.");
            }
        }
        Assert.True(failures.Count == 0,
            "A type declared across partial files serializes its members in the order the compiler meets the files. " +
            "Give each persisted member a JsonPropertyOrder from KhaozEngine.MapDoc/MapPersistedOrder.cs that keeps " +
            "every existing member where it is, then add it to the pinned order in PersistedOrderTests.\n" +
            string.Join("\n", failures));
    }

    static JsonSerializerOptions DocumentOptions()
    {
        JsonSerializerOptions options = MapDocumentFile.CreateOptions(MapDocRegistry.CreateDefault(), write: true);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    static bool IsPersisted(JsonPropertyInfo property) =>
        property.Get is not null &&
        !(property.AttributeProvider is MemberInfo member &&
          member.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always });

    static string[] Persisted(JsonTypeInfo info) => info.Properties.Where(IsPersisted).Select(p => p.Name).ToArray();

    static void AssertOrder(string type, IReadOnlyList<string> expected, IReadOnlyList<string> actual) =>
        Assert.True(expected.SequenceEqual(actual),
            $"{type} serialized member order changed. {Deliberate}\n" +
            $"Expected: {string.Join(", ", expected)}\nActual:   {string.Join(", ", actual)}");

    /// <summary>Every type the document graph writes through a reflection object contract, starting at the
    /// document and at the prefab payload roots. A member with its own converter writes its own bytes, so the walk
    /// stops there.</summary>
    static HashSet<Type> PersistedObjectTypes()
    {
        var result = new HashSet<Type>();
        Walk(DocumentOptions(), result, typeof(MapDocument));
        Walk(MapPrefabJson.ToolOptions, result, typeof(MapPrefabSet), typeof(MapBuildingIntent),
            typeof(MapStandaloneResourceTransform), typeof(MapPlacement));
        return result;
    }

    static void Walk(JsonSerializerOptions options, HashSet<Type> objects, params Type[] roots)
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>(roots);
        while (pending.TryDequeue(out Type? type))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!seen.Add(type)) continue;
            JsonTypeInfo info = options.GetTypeInfo(type);
            switch (info.Kind)
            {
                case JsonTypeInfoKind.Object:
                    objects.Add(type);
                    foreach (JsonPropertyInfo property in info.Properties.Where(IsPersisted))
                        if (property.CustomConverter is null) pending.Enqueue(property.PropertyType);
                    break;
                case JsonTypeInfoKind.Enumerable:
                    pending.Enqueue(info.ElementType!);
                    break;
                case JsonTypeInfoKind.Dictionary:
                    pending.Enqueue(info.KeyType!);
                    pending.Enqueue(info.ElementType!);
                    break;
            }
        }
    }

    /// <summary>Names of the types the MapDoc package declares in more than one source file.</summary>
    static HashSet<string> MultiPartTypeNames()
    {
        string package = Path.Combine(RepositoryRoot(), "KhaozEngine.MapDoc");
        var partial = new Regex(@"\bpartial\s+(?:class|struct|interface|record(?:\s+class|\s+struct)?)\s+(\w+)");
        var files = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int scanned = 0;
        foreach (string path in Directory.EnumerateFiles(package, "*.cs", SearchOption.AllDirectories))
        {
            string[] segments = Path.GetRelativePath(package, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => segment is "bin" or "obj")) continue;
            scanned++;
            foreach (Match match in partial.Matches(File.ReadAllText(path)))
            {
                string name = match.Groups[1].Value;
                if (!files.TryGetValue(name, out HashSet<string>? declaring)) files[name] = declaring = new(StringComparer.Ordinal);
                declaring.Add(path);
            }
        }
        Assert.True(scanned > 0, "The partial type scan found no MapDoc source files.");
        return files.Where(entry => entry.Value.Count > 1).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    }

    static string SimpleName(Type type)
    {
        int arity = type.Name.IndexOf('`');
        return arity < 0 ? type.Name : type.Name[..arity];
    }

    static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
