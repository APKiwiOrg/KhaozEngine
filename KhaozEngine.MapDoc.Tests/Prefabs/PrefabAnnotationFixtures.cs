using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    internal static PrefabFixture LinkedBuildings(bool overlappingGroups = false)
    {
        var f = Seam();
        var asset = f.Assets.GetPrefab("hut");
        var link = asset.Children.OfType<MapPrefabRoofChild>().Single();
        asset = asset with
        {
            Children = asset.Children.Select(c => c == link ? link with
            { Value = link.Value with { Rooms = [], Groups = new[] { World(MapPrefabChildKind.RoofGroup, "shared") } } } : c).ToArray()
        };
        f = Reasset(f, asset, new Dictionary<string, string> { ["roof"] = "roof-a", ["room"] = "room-a", ["roof-link"] = "a" });
        var a = f.Document.Prefabs!.Instances.Single();
        long numeric = f.Document.NumericIdHighWaterMark;
        MapPrefabInstance Copy(string id, Vector3 position, string roof, string room, bool shelter) => a with
        {
            Id = id,
            Transform = new(position, 0, 1),
            Bindings = new(a.Bindings.Entries.Select(binding => binding with
            {
                WorldId = binding.Child.Key switch { "roof" => roof, "room" => room, "roof-link" => id, _ => id + "/" + binding.Child.Key },
                NumericId = binding.Child.Kind == MapPrefabChildKind.Placement ? ++numeric : null,
                Removed = shelter && binding.Child.Kind == MapPrefabChildKind.RoofLink,
            }).ToArray()),
            // Marker names are globally unique, so each copy renames its marker through an explicit replace override.
            Overrides = (shelter ? new MapPrefabOverride[] { new MapPrefabRemoveOverride(link.Key, null) } : []).Concat(MarkerNames(asset, id)).ToArray(),
        };
        var b = Copy("b", new(8, 0, 0), "roof-b", "room-b", false);
        var c = Copy("c", new(8, 0, -8), "shelter-roof", "room-c", true);
        f.Document.NumericIdHighWaterMark = numeric;
        f.Document.Prefabs = new(1, 1, new[] { a, b, c }, []);
        var groups = new List<MapRoofGroupDoc>
        {
            new("shared", new[] { World(MapPrefabChildKind.Record, "room-a"), World(MapPrefabChildKind.Record, "room-b") }),
            new("other", new[] { World(MapPrefabChildKind.Record, "room-c") }),
        };
        if (overlappingGroups)
        {
            groups.Add(new("overlap", new[] { World(MapPrefabChildKind.Record, "room-a") }));
            link = (MapPrefabRoofChild)asset.Children.Single(x => x.Key == link.Key);
            asset = asset with
            {
                Children = asset.Children.Select(x => x == link ? link with
                { Value = link.Value with { Groups = link.Value.Groups.Append(World(MapPrefabChildKind.RoofGroup, "overlap")).ToArray() } } : x).ToArray()
            };
            var assets = LoadAssets(MapPrefabFile.Write(asset), true);
            f.Document.NativeAssets = assets.Roots.ToList();
            f.Document.Prefabs = new(1, 1, f.Document.Prefabs.Instances.Select(i => i with { ResourceSha256 = assets.GetResource("hut").Reference.Sha256 }).ToArray(), []);
            f = new(f.Document, assets);
        }
        f.Document.BuildingIntent = new(1, [], groups, [], []) { Roofs = new[] { World(MapPrefabChildKind.Placement, "roof-a") } };
        return f;
    }

    internal static MapPrefabReference World(MapPrefabChildKind kind, string id) => new(MapPrefabReferenceScope.World, kind, id);
    internal static MapFramePoint LinkedRoomPoint(MapEffectiveResources resources) => Point(resources, 1, 1, -1);

    internal static PrefabFixture TwoLeafLamps(bool explicitLight = false, bool conflict = false)
    {
        var f = Create("roomless");
        MapPrefabPlacementChild Lamp(string key, float x) => new(new(MapPrefabChildKind.Placement, key), new MapPlacement
        { Id = key, Kind = "lamp", AssetId = "lantern", X = x, Y = 5, Z = -3.125f, Yaw = 0.371f, Scale = 1.2f });
        var children = new List<MapPrefabChild> { Lamp("lamp-a", 10.25f), Lamp("lamp-b", 20.25f) };
        if (explicitLight || conflict) children.Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, conflict ? "lamp-a/lamp-light/emission" : "light-extra"),
            new(1, conflict ? "lamp-a/lamp-light/emission" : "light-extra", null, new(0, 1, 0), Vector3.One, 1, 2, true)));
        var asset = new MapPrefabAsset(1, "hut", children);
        var resources = f.Assets.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        resources["hut"] = MapPrefabFile.Write(asset);
        resources["lamp-light"] = MapLightFile.Write(new(1, "emission", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "$owner"),
            new(0, 1, 0), Vector3.One, 1, 2, true));
        var manifest = JsonNode.Parse(resources["manifest"])!.AsObject();
        var leaf = manifest["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "lantern")!;
        leaf["sourceUnitsToMetres"] = 0.01f;
        leaf["lightResourceIds"] = new JsonArray("lamp-light");
        var rows = manifest["resources"]!.AsArray();
        rows.Add(Resource(new("lamp-light", "assets/lamp-light", Convert.ToHexStringLower(SHA256.HashData(resources["lamp-light"])), 1), "Light", []));
        rows.Single(r => r!["reference"]!["id"]!.GetValue<string>() == "hut")!["dependencies"]!.AsArray().Add("lamp-light");
        foreach (var row in rows)
            row!["reference"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(resources[row["reference"]!["id"]!.GetValue<string>()]));
        resources["manifest"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var roots = f.Assets.Roots.Select(r => r with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(resources[r.Id])) }).ToArray();
        var captured = MapAssetClosure.Load(roots, new AnnotationSource(resources));
        return Reasset(f, asset, new Dictionary<string, string>
        {
            ["lamp-a"] = "owner-a",
            ["lamp-b"] = "owner-b",
            ["lamp-a/lamp-light/emission"] = "light-a",
            ["lamp-b/lamp-light/emission"] = "light-b",
            ["light-extra"] = "light-extra",
        }, captured);
    }

    internal static PrefabFixture RoofInventoryRemoval(bool worldSameText)
    {
        var f = Create();
        if (worldSameText)
        {
            var asset = f.Assets.GetPrefab("hut");
            f = Reasset(f, asset with { Roofs = asset.Roofs.Append(World(MapPrefabChildKind.Placement, "roof")).ToArray() });
            f.Document.Placements.Add(new()
            {
                Id = "roof",
                Kind = "roof",
                AssetId = "roof",
                X = 8,
                Y = 3,
                Z = -8,
                Scale = 1,
                NumericId = ++f.Document.NumericIdHighWaterMark
            });
        }
        else f.Document.BuildingIntent = new(1, [], [], [], []) { Roofs = new[] { World(MapPrefabChildKind.Placement, "a/roof") } };
        var instance = f.Document.Prefabs!.Instances.Single();
        var removed = new[] { new MapPrefabChildKey(MapPrefabChildKind.Placement, "roof"), new MapPrefabChildKey(MapPrefabChildKind.RoofLink, "roof-link") };
        f.Document.Prefabs = new(1, 1, new[] { instance with
        {
            Bindings = new(instance.Bindings.Entries.Select(b => removed.Contains(b.Child) ? b with { Removed = true } : b).ToArray()),
            Overrides = removed.Select(k => (MapPrefabOverride)new MapPrefabRemoveOverride(k, null)).ToArray(),
        } }, []);
        return f;
    }

    internal static PrefabFixture AnnotationTransformed()
    {
        var f = Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f));
        var asset = f.Assets.GetPrefab("hut");
        return Reasset(f, asset with
        {
            Children = asset.Children.Select(c => c is MapPrefabMarkerChild marker ? marker with
            { Value = marker.Value with { Name = "entry" } } : c).ToArray()
        });
    }

    internal static PrefabFixture WorldOwnedLight()
    {
        var f = Create();
        var asset = f.Assets.GetPrefab("hut");
        asset = asset with
        {
            Children = asset.Children.Select(c => c is MapPrefabLightChild light ? light with
            { Value = light.Value with { Owner = World(MapPrefabChildKind.Placement, "world-owner"), Position = new(0, 1, 0), Radius = 2 } } : c).ToArray()
        };
        f = Reasset(f, asset);
        f.Document.Placements.Add(new()
        {
            Id = "world-owner",
            Kind = "lamp",
            AssetId = "item",
            X = 10.25f,
            Y = 5,
            Z = -3.125f,
            Yaw = 0.371f,
            Scale = 1.2f,
            NumericId = ++f.Document.NumericIdHighWaterMark
        });
        return f;
    }

    sealed class AnnotationSource(Dictionary<string, byte[]> bytes) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => bytes[reference.Id];
    }
}
