using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Markers;

internal sealed record MarkerFixture(MapDocument Document, MapAssetClosure Assets);

/// <summary>Public invented marker fixtures. Linked into other test projects, so nothing here reads files.</summary>
internal static partial class MarkerFixtures
{
    internal const string GoldenResource = "golden-marker";

    internal static MapResolveOptions Options() => new("public-marker-fixture", 1, "marker-options/1", 2);

    internal static MapExactPoint Exact(float x, float y, float z) =>
        new(MapExactValue.FromSingle(x), MapExactValue.FromSingle(y), MapExactValue.FromSingle(z));

    internal static string FormatSevenJson(bool analytic) => analytic
        ? """{"formatVersion":7,"id":"analytic-seven","bounds":{"minX":-32,"minZ":-32,"maxX":32,"maxZ":32},"terrain":{}}"""
        : """{"formatVersion":7,"id":"native-seven","bounds":{"minX":-32,"minZ":-32,"maxX":32,"maxZ":32},"playableBounds":{"minX":-32,"minZ":-32,"maxX":32,"maxZ":32},"terrain":{},"resolverIdentity":{"payloadVersion":1,"resolverVersion":2},"supportRecipe":"AuthoredBindingsV2","surfaces":[]}""";

    /// <summary>The standalone payload-1 point, built through the released positional constructor. Position.Y is its
    /// authored height.</summary>
    internal static MapLocalMarkerDoc BasicMarker() =>
        new(1, "m", "arrival", new Vector3(-0.25f, 12.25f, -1.5f), 0.371f, "entry-role", true, new[] { "first", "second" });

    internal static MapPrefabAsset PayloadOneAsset() => new(1, GoldenResource, new MapPrefabChild[]
    {
        new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "entry"),
            new MapLocalMarkerDoc(1, "entry", "arrival-a", new Vector3(1, 2, -1), 0.371f, "entry-role", true, new[] { "first", "second" })),
    });

    internal const string PayloadTwoResource = "marker-two";
    internal static MapMarkerHeight ExplicitHeight() => new(12.25f, null);
    internal static MapMarkerHeight SurfaceHeight() => new(null, new MapSupportBinding(MapSupportBindingKind.Surface, "floor", null, null, null, null));
    internal static MapMarkerHeight SpaceHeight() => new(null, new MapSupportBinding(MapSupportBindingKind.Space, null, "level-2", 20.2f, 0.5f, 0.5f));

    /// <summary>One payload-2 marker per height form, keyed explicit, surface and space, each derived from a payload-1
    /// point through the shared height encoding.</summary>
    internal static MapLocalMarkerDoc[] PayloadTwoMarkers(string prefix) =>
    [
        MapMarkerHeights.Apply(new(1, prefix + "explicit", prefix + "explicit-name", new Vector3(-0.25f, 3, -1.5f), 0.371f, "entry-role", true, new[] { "first", "second" }), ExplicitHeight()),
        MapMarkerHeights.Apply(new(1, prefix + "surface", prefix + "surface-name", new Vector3(0.5f, 7, 1.25f), 0, "guard-role", true, Array.Empty<string>()), SurfaceHeight())
            with { ArchetypeId = "guard" },
        MapMarkerHeights.Apply(new(1, prefix + "space", prefix + "space-name", new Vector3(2, -4, -3), -1.5f, "guard-role", false, new[] { "upper" }), SpaceHeight()),
    ];

    internal static MapPrefabAsset PayloadTwoAsset() => new(1, PayloadTwoResource,
        PayloadTwoMarkers("").Select(m => (MapPrefabChild)new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, m.Id), m)).ToArray());

    internal static MarkerFixture Create(string variant = "basic")
    {
        var prefabs = new List<MapPrefabAsset> { PayloadOneAsset() };
        var markers = new List<MapLocalMarkerDoc>();
        var instances = new List<(string Id, MapPrefabAsset Asset)>();
        switch (variant)
        {
            case "basic": markers.Add(BasicMarker()); break;
            case "prefab-basic": instances.Add(("a", prefabs[0])); break;
            case "payload-two":
                prefabs.Add(PayloadTwoAsset());
                markers.AddRange(PayloadTwoMarkers("w-"));
                instances.Add(("b", prefabs[1]));
                break;
            default: throw new ArgumentException("Unknown marker variant " + variant);
        }
        var (roots, source) = Closure(prefabs);
        var assets = MapAssetClosure.Load(roots, source);
        MapDocument document = MapDocumentFile.LoadText(FormatSevenJson(false));
        document.NativeAssets = roots.ToList();
        if (markers.Count != 0) document.BuildingIntent = new(1, [], [], [], markers.ToArray());
        if (instances.Count != 0)
            document.Prefabs = new(1, 1, instances.Select(i => new MapPrefabInstance(i.Id, i.Asset.Id, assets.GetResource(i.Asset.Id).Reference.Sha256,
                new(Vector3.Zero, 0, 1), null, new(i.Asset.Children.Select(c => new MapPrefabBinding(c.Key, i.Id + "/" + c.Key.Key, null, false)).ToArray()),
                [])).ToArray(), []);
        return new(document, assets);
    }

    /// <summary>Saves and reloads one storage form through the public codec.</summary>
    internal static MarkerFixture Reload(MarkerFixture fixture, bool tiled)
    {
        if (!tiled) return fixture with { Document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document)) };
        string directory = Path.Combine(Path.GetTempPath(), "ke-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(fixture.Document, directory);
            return fixture with { Document = MapDocumentFile.LoadTiled(directory) };
        }
        finally { Directory.Delete(directory, true); }
    }

    static (IReadOnlyList<MapAssetRef> Roots, MemorySource Source) Closure(IReadOnlyList<MapPrefabAsset> prefabs)
    {
        var source = new MemorySource();
        var resources = new JsonArray();
        foreach (MapPrefabAsset prefab in prefabs)
        {
            MapAssetRef reference = source.Add(prefab.Id, MapPrefabFile.Write(prefab));
            resources.Add(new JsonObject
            {
                ["reference"] = new JsonObject { ["id"] = reference.Id, ["path"] = reference.Path, ["sha256"] = reference.Sha256, ["payloadVersion"] = reference.PayloadVersion },
                ["kind"] = "Prefab",
                ["dependencies"] = new JsonArray(),
            });
        }
        MapAssetRef manifest = source.Add("marker-manifest",
            Encoding.UTF8.GetBytes(new JsonObject { ["payloadVersion"] = 1, ["assets"] = new JsonArray(), ["resources"] = resources }.ToJsonString()));
        return (new[] { manifest }, source);
    }

    sealed class MemorySource : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        internal MapAssetRef Add(string id, byte[] bytes)
        {
            _bytes.Add(id, bytes);
            return new(id, "assets/" + id, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
        }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes[reference.Id];
    }
}
