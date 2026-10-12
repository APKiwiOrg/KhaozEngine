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
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal sealed record PrefabFixture(MapDocument Document, MapAssetClosure Assets);

internal static partial class PrefabFixtures
{
    internal static MapResolveOptions Options() => new("public-prefab-fixture", 1, "fixture-options/1", 2);
    internal static string TempDirectory()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), ".superpowers", "tmp", "prefab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string FormatSixJson(bool analytic) => analytic
        ? """{"formatVersion":6,"id":"analytic-six","bounds":{"minX":-32,"minZ":-32,"maxX":32,"maxZ":32},"terrain":{}}"""
        : """{"formatVersion":6,"id":"native-six","bounds":{"minX":-32,"minZ":-32,"maxX":32,"maxZ":32},"playableBounds":{"minX":-32,"minZ":-32,"maxX":32,"maxZ":32},"terrain":{},"resolverIdentity":{"payloadVersion":1,"resolverVersion":2},"supportRecipe":"AuthoredBindingsV2"}""";

    internal static PrefabFixture Create(string variant = "basic")
    {
        if (TryPaintVariant(variant, out PrefabFixture? paint)) return paint!;
        if (variant == "support-resource") return SupportResourceFixture();
        if (TryIdentityVariant(variant, out PrefabFixture? derived)) return derived!;
        byte[] bytes = AssetBytes(variant == "pool-material-undeclared" ? "pool" :
            variant is "roomless" or "pool" or "all-resources" or "pool-material-mesh" or "attached" or "materialized" ? variant :
            variant == "asset-version" ? "changed" : "valid");
        var (roots, source) = Inputs(bytes, true, variant);
        var assets = MapAssetClosure.Load(roots, source);
        var document = MapDocumentFile.LoadText(FormatSixJson(false));
        document.NativeAssets = roots.ToList();
        if (variant is not ("roomless" or "attached" or "materialized"))
        {
            var patch = Patch("exterior", 16, 16, -200);
            var anchor = patch.Key;
            patch.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
            patch.Records.Add(new MapSpaceFootprint("exterior-domain", new("exterior", anchor), anchor,
                Enumerable.Range(0, 16).SelectMany(z => Enumerable.Range(0, 16).Select(x => z * 64 + x)).ToArray(),
                new(MapBoundKind.SupportFloor, "exterior", null), new(MapBoundKind.OpenTop, null, null)));
            document.Surfaces.Patches.Add(anchor, patch);
            var surface = Surface("exterior", MapSurfaceRole.SupportFloor);
            document.Surfaces.Refs.Add(surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                new[] { new KeyValuePair<MapPatchKey, string>(anchor, MapSurfaceSemantics.PatchDigest(patch)) })
            });
        }
        var asset = MapPrefabFile.Read(bytes, "hut");
        long numeric = 100;
        var bindings = asset.Children.Select(c => new MapPrefabBinding(c.Key, "a/" + c.Key.Key,
            c.Key.Kind == MapPrefabChildKind.Placement ? ++numeric : null, false)).ToArray();
        document.NumericIdHighWaterMark = numeric;
        // Implicit leaf support and light children take their bindings from the planner under test.
        var planned = variant is "attached" or "materialized"
            ? MapPrefabBindingPlanner.Plan(document, asset, assets, "a", new(bindings)) : null;
        if (planned is not null) document.NumericIdHighWaterMark = planned.HighWaterMark;
        bool roomless = variant is "roomless" or "attached" or "materialized";
        document.Prefabs = new(1, 1, new[] { new MapPrefabInstance("a", "hut", assets.GetResource("hut").Reference.Sha256,
            new(Vector3.Zero, 0, 1), roomless ? null : new("exterior", new("exterior", 0, 0)), planned?.Bindings ?? new(bindings), []) }, []);
        return new(document, assets);
    }

    internal static byte[] AssetBytes(string variant)
    {
        var children = new List<MapPrefabChild>();
        var item = new MapPlacement { Id = "item", Kind = "item", AssetId = "item", X = 1, Y = 0, Z = -1, Scale = 1, Tags = new() { "first", "second" } };
        children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "item"), item));
        if (variant is "attached" or "materialized")
        {
            children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "deck"), new MapPlacement
            { Id = "deck", Kind = "deck", AssetId = "deck", X = -2, Y = 0, Z = -2, Scale = 1 }));
            if (variant == "attached")
                children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "lantern"), new MapPlacement
                { Id = "lantern", Kind = "lantern", AssetId = "lantern", X = 2, Y = 0, Z = -2, Scale = 1 }));
            return MapPrefabFile.Write(variant == "attached" ? new(1, "hut", children) : Materialized(children));
        }
        if (variant != "roomless")
        {
            children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "doorway"), new MapPlacement
            { Id = "doorway", Kind = "doorway", AssetId = "doorway", X = 2, Y = 0, Z = 0, Scale = 1 }));
            children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "roof"), new MapPlacement
            { Id = "roof", Kind = "roof", AssetId = "roof", Y = 3, Scale = 1 }));
            children.Add(SurfaceChild("floor", MapSurfaceRole.SupportFloor, Patch("floor", 4, 4, 0)));
            children.Add(SurfaceChild("ceiling", MapSurfaceRole.Ceiling, Patch("ceiling", 4, 4, 300)));
            var floor = new MapPatchKey("floor", 0, 0);
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "room"), new MapSpaceDoc("room", MapSpaceKind.Room,
                new("$parent", new("$parent", 0, 0)), null, [], [], [], []), floor)
            { References = Slots(Anchor("floor"), Ref("/value/parent", "$parent", "$parent")) });
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "room-domain"), new MapPrismDomain("room-domain", 1,
                new("room", floor), new("$parent", new("$parent", 0, 0)), "", "lower",
                new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(0, 0, 4, 4) }, []), 0, 3), floor)
            {
                References = Slots(Anchor("floor"), Ref("/value/space", "room", "floor"), Ref("/value/parent", "$parent", "$parent"),
                    Slot("/value/storeyId", MapPrefabChildKind.Storey, "lower"))
            });
            foreach (var (id, surfaceId, height) in new[] { ("door-bottom", "floor", 0), ("door-top", "ceiling", 300) })
                children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, id), new MapBoundaryChain(id, MapChainKind.Authored, null,
                    new[] { new MapChainVertex(new(surfaceId, MapLatticeAddress.Create(7, 0, 5)), height),
                        new MapChainVertex(new(surfaceId, MapLatticeAddress.Create(13, 0, 5)), height) }), floor)
                {
                    References = Slots(Anchor("floor"), Surf("/value/vertices/0/vertex/surfaceId", surfaceId),
                        Surf("/value/vertices/1/vertex/surfaceId", surfaceId))
                });
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "room-portal"), new MapCavePortal("room-portal", new("room", floor),
                new("$parent", new("$parent", 0, 0)), new[] { new MapLatticeVertex("floor", MapLatticeAddress.Create(7, 0, 5)), new MapLatticeVertex("floor", MapLatticeAddress.Create(13, 0, 5)) },
                new("door-bottom", floor), new("door-top", floor)), floor)
            {
                References = Slots(Anchor("floor"), Ref("/value/fromSpace", "room", "floor"), Ref("/value/toSpace", "$parent", "$parent"),
                    Surf("/value/interval/0/surfaceId", "floor"), Surf("/value/interval/1/surfaceId", "floor"),
                    Ref("/value/bandBottom", "door-bottom", "floor"), Ref("/value/bandTop", "door-top", "floor"))
            });
            children.Add(new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "lower"), "Lower"));
            // Annotation references carry their scope directly and take no slots.
            children.Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, "lamp"), new(1, "lamp", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "doorway"),
                new(2, 2, 0), Vector3.One, 1, 4, false)));
            children.Add(new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "entry"), new(1, "entry", "Entry", new(0, 1, 0), 0, "entrance", true, new[] { "first", "second" })));
            children.Add(new MapPrefabRoofChild(new(MapPrefabChildKind.RoofLink, "roof-link"), new("roof-link", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof"),
                new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "room") }, [], 3)));
        }
        if (variant is "pool" or "all-resources" or "pool-material-mesh")
        {
            var poolFloor = Patch("pool-floor", 4, 4, -100); poolFloor.CellMinX = 4; poolFloor.CellMinZ = 4;
            var poolCeiling = Patch("pool-ceiling", 4, 4, 300); poolCeiling.CellMinX = 4; poolCeiling.CellMinZ = 4;
            children.Add(SurfaceChild("pool-floor", MapSurfaceRole.SupportFloor, poolFloor));
            children.Add(SurfaceChild("pool-ceiling", MapSurfaceRole.Ceiling, poolCeiling));
            var anchor = poolFloor.Key;
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "pool"), new MapSpaceDoc("pool", MapSpaceKind.Room,
                new("$parent", new("$parent", 0, 0)), null, [], [], [], []), anchor)
            { References = Slots(Anchor("pool-floor"), Ref("/value/parent", "$parent", "$parent")) });
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "pool-domain"), new MapPrismDomain("pool-domain", 1, new("pool", anchor), new("$parent", new("$parent", 0, 0)), "", "lower",
                new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(4, 4, 8, 8) }, []), -1, 3), anchor)
            {
                References = Slots(Anchor("pool-floor"), Ref("/value/space", "pool", "pool-floor"), Ref("/value/parent", "$parent", "$parent"),
                    Slot("/value/storeyId", MapPrefabChildKind.Storey, "lower"))
            });
            children.Add(new MapPrefabMediumChild(new(MapPrefabChildKind.WaterMedium, "water"), new("water", MapWaterMediumKind.Water, 1)));
            children.Add(new MapPrefabWaterChild(new(MapPrefabChildKind.WaterBody, "pool-water"), new("pool-water", 0.5f, "water", "pool-material",
                new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(4, 4, 8, 8) }, []), new[] { new MapRecordRef("pool", anchor) }))
            { References = Slots(Slot("/value/medium", MapPrefabChildKind.WaterMedium, "water"), Ref("/value/covers/0", "pool", "pool-floor")) });
            children.Add(new MapPrefabRoofGroupChild(new(MapPrefabChildKind.RoofGroup, "hut-rooms"), new("hut-rooms",
                new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "room"), new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "pool") })));
        }
        byte[] bytes = MapPrefabFile.Write(new(1, "hut", children)
        { Roofs = variant == "roomless" ? [] : new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof") } });
        if (variant is "valid" or "roomless" or "pool" or "all-resources") return bytes;
        if (variant == "duplicate-member") return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"payloadVersion\":1", "\"payloadVersion\":1,\"payloadVersion\":1", StringComparison.Ordinal));
        var root = JsonNode.Parse(bytes)!.AsObject();
        JsonObject first = root["children"]!.AsArray().Single(c => c!["key"]!["kind"]!.GetValue<string>() == "Placement" && c["key"]!["key"]!.GetValue<string>() == "item")!.AsObject();
        switch (variant)
        {
            case "pool-material-mesh": root["children"]!.AsArray().Single(c => c!["key"]!["kind"]!.GetValue<string>() == "WaterBody")!["value"]!["renderMaterial"] = "mesh"; break;
            case "placement-numeric-id": first["value"]!["numericId"] = "9007199254740993"; break;
            case "record-record-pose-mismatch":
            case "record-record-pose-world":
                var pose = JsonNode.Parse("""{"position":{"x":1,"y":0,"z":0},"yawRadians":0,"scale":1}""")!;
                var displacedFloor = root["children"]!.AsArray().Single(c => c!["key"]!["key"]!.GetValue<string>() == "floor")!.DeepClone();
                displacedFloor["key"]!["key"] = "chain-floor";
                displacedFloor["surface"]!["id"] = "chain-floor";
                displacedFloor["localTransform"] = pose.DeepClone();
                foreach (var patch in displacedFloor["patches"]!.AsArray()) patch!["key"]!["surfaceId"] = "chain-floor";
                foreach (var slot in displacedFloor["references"]!.AsArray()) slot!["target"]!["id"] = "chain-floor";
                root["children"]!.AsArray().Add(displacedFloor);
                var chain = root["children"]!.AsArray().Single(c => c!["key"]!["key"]!.GetValue<string>() == "door-bottom")!;
                chain["anchor"]!["surfaceId"] = "chain-floor";
                chain["localTransform"] = pose.DeepClone();
                foreach (var vertex in chain["value"]!["vertices"]!.AsArray()) vertex!["vertex"]!["surfaceId"] = "chain-floor";
                foreach (var slot in chain["references"]!.AsArray()) slot!["target"]!["id"] = "chain-floor";
                var portal = root["children"]!.AsArray().Single(c => c!["key"]!["key"]!.GetValue<string>() == "room-portal")!;
                portal["value"]!["bandBottom"]!["anchor"]!["surfaceId"] = "chain-floor";
                portal["references"]!.AsArray().Single(r => r!["path"]!.GetValue<string>() == "/value/bandBottom/anchor/surfaceId")!["target"]!["id"] = "chain-floor";
                if (variant == "record-record-pose-world")
                    foreach (var slot in portal["references"]!.AsArray().Where(r => r!["path"]!.GetValue<string>().StartsWith("/value/bandBottom/", StringComparison.Ordinal)))
                        slot!["target"]!["scope"] = "World";
                break;
            case "changed": first["value"]!["tags"]!.AsArray().Add("changed"); break;
            case "future-payload": root["payloadVersion"] = 2; break;
            case "unknown-member": root["unknown"] = true; break;
            case "case-member": root["PayloadVersion"] = 1; break;
            case "missing-member": root.Remove("children"); break;
            case "null-children": root["children"] = null; break;
            case "nested-instance": first["type"] = "Instance"; break;
            case "duplicate-child": root["children"]!.AsArray().Add(first.DeepClone()); break;
            case "unknown-enum": first["key"]!["kind"] = "Other"; break;
            case "nonpositive-scale": first["value"]!["scale"] = 0; break;
            case "embedded-record":
                var surface = root["children"]!.AsArray().First(c => c!["key"]!["kind"]!.GetValue<string>() == "Surface")!;
                surface["patches"]![0]!["records"]!.AsArray().Add(root["children"]!.AsArray().First(c => c!["key"]!["key"]!.GetValue<string>() == "room")!["value"]!.DeepClone());
                break;
            case "missing-slot": root["children"]!.AsArray().First(c => c!["key"]!["key"]!.GetValue<string>() == "room")!["references"] = new JsonArray(); break;
            case "duplicate-slot":
                var record = root["children"]!.AsArray().First(c => c!["key"]!["key"]!.GetValue<string>() == "room")!;
                record["references"]!.AsArray().Add(record["references"]![0]!.DeepClone()); break;
            case "unsupported-slot": first["references"] = new JsonArray(new JsonObject { ["path"] = "/value/madeUp", ["target"] = new JsonObject { ["scope"] = "Local", ["kind"] = "Record", ["id"] = "room" } }); break;
            case "wrong-payload-id": first["value"]!["id"] = "different"; break;
            case "missing-placement-field": first["value"]!.AsObject().Remove("x"); break;
            case "missing-light-field": root["children"]!.AsArray().First(c => c!["key"]!["kind"]!.GetValue<string>() == "Light")!["value"]!.AsObject().Remove("radius"); break;
            case "unknown-light-field": root["children"]!.AsArray().First(c => c!["key"]!["kind"]!.GetValue<string>() == "Light")!["value"]!["future"] = true; break;
            case "case-light-field": root["children"]!.AsArray().First(c => c!["key"]!["kind"]!.GetValue<string>() == "Light")!["value"]!["Radius"] = 4; break;
            case "infinite-transform": first["value"]!["scale"] = JsonNode.Parse("1e400"); break;
            case "slot-scope-conflict":
                root["children"]!.AsArray().First(c => c!["key"]!["key"]!.GetValue<string>() == "room-domain")!["references"]!.AsArray()
                    .Single(r => r!["path"]!.GetValue<string>() == "/value/space/id")!["target"]!["scope"] = "World"; break;
            case "inconsistent-pose":
                root["children"]!.AsArray().First(c => c!["key"]!["key"]!.GetValue<string>() == "room")!["localTransform"] = JsonNode.Parse("""{"position":{"x":1,"y":0,"z":0},"yawRadians":0,"scale":1}"""); break;
            default: throw new ArgumentException("Unknown prefab variant " + variant);
        }
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    internal static MapAssetClosure LoadAssets(byte[] prefabBytes, bool refreshDigest)
    {
        var (roots, source) = Inputs(prefabBytes, refreshDigest);
        return MapAssetClosure.Load(roots, source);
    }

    static (IReadOnlyList<MapAssetRef> Roots, FixtureSource Source) Inputs(byte[] bytes, bool refreshDigest, string variant = "basic")
    {
        var source = new FixtureSource();
        var resources = new JsonArray();
        foreach (var (id, kind, payload) in new[]
        {
            ("mesh", "Mesh", Encoding.UTF8.GetBytes("public-fixture-mesh")),
            ("material", "Material", Encoding.UTF8.GetBytes("public-fixture-material")),
            ("pool-material", "Material", Encoding.UTF8.GetBytes("public-fixture-pool-material")),
            ("doorway.collider", "Collider", DoorwayBytes()), ("roof.collider", "Collider", BoxBytes(2, 0.1f, 2)),
            ("item.selection", "Selection", BoxBytes(0.1f, 0.1f, 0.1f)),
            ("light", "Light", Encoding.UTF8.GetBytes("public-fixture-light")),
            ("surface", "Surface", MapSurfacePatchCodec.Encode(Patch("leaf-support", 1, 1, 0))),
            ("glow-light", "Light", MapLightFile.Write(GlowLight())),
            ("deck-support", "Surface", MapSupportResourceFile.Write(DeckSupport())),
        })
        {
            var reference = source.Add(id, payload);
            bool wrong = variant == "wrong-" + id || variant == "wrong-collider" && id == "doorway.collider";
            resources.Add(Resource(reference, wrong ? "Mesh" : kind, []));
        }
        var prefab = source.Add("hut", bytes);
        if (!refreshDigest) prefab = prefab with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(AssetBytes("valid"))) };
        var dependencies = new[] { "mesh", "material", "doorway.collider", "roof.collider", "item.selection", "light", "surface", "pool-material",
            "glow-light", "deck-support" };
        resources.Add(Resource(prefab, "Prefab", variant == "cyclic-closure" ? new[] { "manifest" } : variant == "wrong-undeclared" ? [] :
            variant == "pool-material-undeclared" ? dependencies.Where(id => id != "pool-material").ToArray() : dependencies));
        var leaves = new JsonArray();
        foreach (string id in new[] { "item", "doorway", "roof", "deck", "lantern" })
            leaves.Add(new JsonObject
            {
                ["id"] = id,
                ["meshResourceId"] = "mesh",
                ["supportResourceIds"] = id == "deck" ? new JsonArray("deck-support") : variant == "wrong-surface" ? new JsonArray("surface") : new JsonArray(),
                ["materialResourceIds"] = new JsonArray("material"),
                ["lodResourceIds"] = new JsonArray(),
                ["lightResourceIds"] = id is "deck" or "lantern" ? new JsonArray("glow-light") : variant == "wrong-light" ? new JsonArray("light") : new JsonArray(),
                ["sourceUnitsToMetres"] = 1,
                ["renderBounds"] = JsonNode.Parse(id switch
                {
                    "item" => """{"min":{"x":-0.1,"y":-0.1,"z":-0.1},"max":{"x":0.1,"y":0.1,"z":0.1}}""",
                    "doorway" => """{"min":{"x":-1,"y":0,"z":-0.15},"max":{"x":1,"y":3,"z":0.15}}""",
                    _ => """{"min":{"x":-2,"y":-0.1,"z":-2},"max":{"x":2,"y":0.1,"z":2}}""",
                }),
                ["source"] = "public/" + id,
                ["license"] = "CC0",
                ["textured"] = false,
            });
        foreach (JsonNode? leaf in leaves)
        {
            string id = leaf!["id"]!.GetValue<string>();
            if (id is "item" or "deck" or "lantern") leaf["selectionResourceId"] = "item.selection";
            else leaf["collisionResourceId"] = id + ".collider";
        }
        if (variant == "wrong-unknown-asset") leaves.RemoveAt(0);
        var manifest = source.Add("manifest", Encoding.UTF8.GetBytes(new JsonObject { ["payloadVersion"] = 1, ["assets"] = leaves, ["resources"] = resources }.ToJsonString()));
        return (new[] { manifest }, source);
    }

    static JsonObject Resource(MapAssetRef reference, string kind, string[] dependencies) => new()
    {
        ["reference"] = new JsonObject { ["id"] = reference.Id, ["path"] = reference.Path, ["sha256"] = reference.Sha256, ["payloadVersion"] = reference.PayloadVersion },
        ["kind"] = kind,
        ["dependencies"] = new JsonArray(dependencies.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
    };
    internal static MapPrefabReferenceSlot Slot(string path, MapPrefabChildKind kind, string id,
        MapPrefabReferenceScope scope = MapPrefabReferenceScope.Local) => new(path, new(scope, kind, id));
    internal static MapPrefabReferenceSlot Surf(string path, string id, MapPrefabReferenceScope scope = MapPrefabReferenceScope.Local) =>
        Slot(path, MapPrefabChildKind.Surface, id, scope);
    internal static MapPrefabReferenceSlot Anchor(string surfaceId) => Surf("/anchor/surfaceId", surfaceId);
    // A record reference consumes one slot for its ID and one for its anchor surface, with one scope.
    internal static MapPrefabReferenceSlot[] Ref(string path, string id, string anchorSurface, MapPrefabReferenceScope scope = MapPrefabReferenceScope.Local) =>
        new[] { Slot(path + "/id", MapPrefabChildKind.Record, id, scope), Surf(path + "/anchor/surfaceId", anchorSurface, scope) };
    internal static MapPrefabReferenceSlot[] Slots(params object[] slots) =>
        slots.SelectMany(s => s as MapPrefabReferenceSlot[] ?? new[] { (MapPrefabReferenceSlot)s }).ToArray();
    internal static MapPrefabSurfaceChild SurfaceChild(string id, MapSurfaceRole role, MapSurfacePatch patch) =>
        new(new(MapPrefabChildKind.Surface, id), Surface(id, role), new[] { patch }) { References = Slots(Surf("/patches/0/key/surfaceId", id)) };
    internal static MapSurfaceRef Surface(string id, MapSurfaceRole role) => new(id, MapLatticeFrame.ImportedMetreCentimetre, role, MapPresencePolicy.Native, null, null, new string('a', 64));
    internal static MapSurfacePatch Patch(string id, int width, int depth, int height) => new()
    {
        Key = new(id, 0, 0),
        Width = width,
        Depth = depth,
        Heights = Enumerable.Repeat(height, (width + 1) * (depth + 1)).ToArray(),
        Cells = new MapSurfaceCell[width * depth],
        Presence = Enumerable.Range(0, (width * depth + 63) / 64)
            .Select(i => width * depth - i * 64 >= 64 ? ulong.MaxValue : (1UL << (width * depth - i * 64)) - 1).ToArray(),
    };

    // Released KECL payload 1 bytes. Keeping this mechanical fixture writer here preserves the package edge.
    static byte[] BoxBytes(float x, float y, float z)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
        { w.Write(0x4B45434Cu); w.Write((byte)1); w.Write((byte)4); w.Write(x); w.Write(y); w.Write(z); }
        return stream.ToArray();
    }
    static byte[] DoorwayBytes()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            w.Write(0x4B45434Cu); w.Write((byte)1); w.Write((byte)5); w.Write(3);
            foreach (var (position, half) in new[] { (new Vector3(-0.8f, 1.25f, 0), new Vector3(0.2f, 1.25f, 0.15f)),
                (new Vector3(0.8f, 1.25f, 0), new Vector3(0.2f, 1.25f, 0.15f)), (new Vector3(0, 2.75f, 0), new Vector3(1, 0.25f, 0.15f)) })
            {
                w.Write(position.X); w.Write(position.Y); w.Write(position.Z);
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
                w.Write((byte)4); w.Write(half.X); w.Write(half.Y); w.Write(half.Z);
            }
        }
        return stream.ToArray();
    }

    sealed class FixtureSource : IMapAssetSource
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
