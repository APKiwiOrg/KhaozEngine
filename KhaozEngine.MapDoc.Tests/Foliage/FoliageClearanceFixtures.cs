using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static partial class FoliageFixtures
{
    internal static MapPrefabReference Owner(string id, MapPrefabChildKind kind = MapPrefabChildKind.Placement) =>
        new(MapPrefabReferenceScope.World, kind, id);

    internal static MapFoliageLayer ClearanceLayer(MapFoliageLayer layer, MapSupportBinding? support = null,
        IReadOnlyList<MapFoliageClearanceRule>? rules = null, IReadOnlyList<MapFoliageMask>? masks = null,
        IReadOnlyList<MapFoliageOwnerSupport>? owners = null, IReadOnlyList<MapFoliageCompatibilityOrdinal>? ordinals = null,
        float? door = null, float? edge = null, bool? indoors = null, bool? solid = null) =>
        new(1, layer.Id, support ?? layer.Support, layer.OriginX, layer.OriginZ, layer.CellSize, layer.Width, layer.Depth,
            layer.RowDirection, layer.Seed, layer.Spacing, layer.ScaleMin, layer.ScaleMax, layer.RootOffset, layer.Models,
            layer.AllowedVisibleMaterials, layer.Materials, layer.Policy, indoors ?? layer.ExcludeIndoors,
            solid ?? layer.ExcludeSolidObjects, door ?? layer.DoorClearance, edge ?? layer.EdgeFade,
            ordinals ?? layer.CompatibilityOrdinals, rules ?? layer.ClearanceRules, masks ?? layer.ExceptionMasks,
            owners ?? layer.OwnerSupports, layer.Chunks);

    internal static FoliageFixture WithClearance(FoliageFixture fixture, Func<MapFoliageLayer, MapFoliageLayer> edit)
    {
        fixture.Document.Foliage = new(1, [edit(fixture.Document.Foliage!.Layers[0])]);
        return fixture;
    }

    internal static MapPlacement ClearingPlacement(string id, string surface, float x, float z, bool explicitY = false) =>
        new()
        {
            Id = id,
            Kind = "tree",
            AssetId = "tree",
            X = x,
            Z = z,
            Y = explicitY ? 100 : null,
            Scale = 1,
            Tags = ["CLEAR"],
            SupportBinding = explicitY ? null : Surface(surface)
        };

    /// <summary>Invented metre-grid clearance fixtures. Ordinals reverse the physical height order deliberately.
    /// The prefab has one explicit-height tree with a non-square local footprint and no foliage asset member.</summary>
    internal static FoliageFixture Clearance(string variant)
    {
        var set = new MapSurfaceSet();
        void Floor(string id, int height) => WaterFixtures.Surface(set, id, MapSurfaceRole.SupportFloor,
            MapLatticeFrame.ImportedMetreCentimetre, -32, -32, 32, 32, (_, _) => height);
        Floor("floor-1", 2000);
        Floor("roof-seat", -200);
        Floor("peer-seat", 3000);
        Floor("unrelated", 5000);
        if (variant == "typed") Floor("shared", 5000);
        foreach (MapSurfacePatch patch in set.Patches.Values)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
        var (roots, source) = ClearanceAssets();
        MapPrefabAsset prefab = new(1, "clearing-prefab", [new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "prop"),
            ClearingPlacement("prop", "floor-1", 0, 0, true))]);
        MapAssetRef prefabRef = source.Add("clearing-prefab", System.Text.Encoding.UTF8.GetString(MapPrefabFile.Write(prefab)));
        MapAssetRef index = source.Add("clearing-prefab-index", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(prefabRef, "Prefab", "mesh", "collider", "selection", "material")),
        }.ToJsonString());
        roots = roots.Append(index).ToArray();
        MapDocument document = MixedResolutionFixtures.Document(set);
        document.NativeAssets = roots.ToList();
        var bytes = Enumerable.Repeat((byte)255, 32 * 32).ToArray();
        MapFoliageLayer layer = Layer("cover-a", Surface("floor-1"), -16, -16, 1, 32, 32, bytes, [10]);
        MapFoliageClearanceKind kind = variant == "door" ? MapFoliageClearanceKind.Door :
            variant == "roof" ? MapFoliageClearanceKind.RoofAbove : MapFoliageClearanceKind.Solid;
        layer = ClearanceLayer(layer, rules: [new("clear", "tree", "clear", kind, new(-0.5f, -1.5f, 0.5f, 1.5f))],
            ordinals: [new("floor-1", 1), new("roof-seat", 2), new("peer-seat", 1), new("unrelated", 3)], door: 0.75f, edge: 0.25f);
        document.Foliage = new(1, [layer]);
        if (variant == "prefab")
        {
            var pose = new MapTransform(new(-12.25f, -450, 7.125f), 0.371f, 1.2f);
            document.Prefabs = new(1, 1, [new("p", "clearing-prefab", prefabRef.Sha256, pose, null,
                new([new(new(MapPrefabChildKind.Placement, "prop"), "p/prop", null, false)]), [])], []);
            document.Foliage = new(1, [ClearanceLayer(layer,
                owners: [new(Owner("p/prop"), Surface("floor-1"))])]);
        }
        else if (variant is "roof" or "mixed")
        {
            document.Placements.Add(ClearingPlacement("higher", "roof-seat", -4, 0));
            document.Placements.Add(ClearingPlacement("peer", "peer-seat", 4, 0));
        }
        else document.Placements.Add(ClearingPlacement("prop", "floor-1", 0, 0));
        return new(document, MapAssetClosure.Load(roots, source));
    }
    internal static FoliageFixture ClearanceReleased(string variant)
    {
        var fixture = FlagsAndCuts();
        var layer = fixture.Document.Foliage!.Layers[0];
        if (variant == "edge-fade")
        {
            var patch = fixture.Document.Surfaces.Patches[new(GroundId, 0, 0)];
            patch.Cells[30 * 64 + 31] = patch.Cells[30 * 64 + 31] with { Underlay = Dirt };
            patch.Cells[31 * 64 + 30] = patch.Cells[31 * 64 + 30] with { Underlay = Pond };
            layer = ReleasedLayer(1, [Grass]);
            return WithClearance(fixture, _ => ClearanceLayer(layer, edge: 0.5f));
        }
        bool roof = variant == "roof";
        if (roof)
        {
            var surface = fixture.Document.Surfaces.Refs.Single() with { Id = "seat", SemanticSha256 = "" };
            var patch = fixture.Document.Surfaces.Patches.Single().Value.Clone();
            patch.Key = new("seat", 0, 0);
            Array.Fill(patch.Heights, 500);
            surface = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                [new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
            };
            fixture.Document.Surfaces.Refs.Add(surface);
            fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        }
        fixture.Document.Placements.Add(ClearingPlacement("clearing", roof ? "seat" : GroundId, 30.5f, -30.5f));
        return WithClearance(fixture, l => ClearanceLayer(l,
            rules: [new("released", "tree", "clear", roof ? MapFoliageClearanceKind.RoofAbove : variant == "door" ?
                MapFoliageClearanceKind.Door : MapFoliageClearanceKind.Solid, new(-0.5f, -0.5f, 0.5f, 0.5f))],
            ordinals: roof ? [new(GroundId, 0), new("seat", 1)] : [new(GroundId, 0)], door: variant == "door" ? 1.5f : 0));
    }

    internal static FoliageFixture NestedClearance()
    {
        var fixture = Clearance("solid");
        var patch = fixture.Document.Surfaces.Patches[new("floor-1", 0, 0)];
        var anchor = patch.Key;
        patch.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("outside-domain", new("outside", anchor), anchor,
            Enumerable.Range(0, patch.Depth).SelectMany(z => Enumerable.Range(0, patch.Width).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "floor-1", null), new(MapBoundKind.OpenTop, null, null)));
        foreach (var (id, parent, size, top) in new[] { ("room", "outside", 4, 25f), ("nested", "room", 2, 23f) })
        {
            patch.Records.Add(new MapSpaceDoc(id, MapSpaceKind.Room, new(parent, anchor), null, [], [], [], []));
            patch.Records.Add(new MapPrismDomain(id + "-domain", 1, new(id, anchor), new(parent, anchor), "", "storey",
                new(MapLatticeFrame.ImportedMetreCentimetre, [new(0, 0, size, size)], []), 20, top));
        }
        fixture.Document.BuildingIntent = new(1, [], [], [], []) { Storeys = [new(1, "storey", "Storey", null)] };
        var binding = new MapSupportBinding(MapSupportBindingKind.Space, null, "nested", 20.2f, 1, 0.5f);
        fixture.Document.Placements[0].X = 0.5f;
        fixture.Document.Placements[0].Z = -0.5f;
        fixture.Document.Placements[0].SupportBinding = binding;
        return WithClearance(fixture, l => ClearanceLayer(l, support: binding));
    }

    // Task 1's decorated payload and its identity mutations now have live typed owners and authored spaces.
    // Density and payload metadata remain exactly the released fixture values.
    static void DecorateClearanceOwners(MapDocument document)
    {
        var patch = document.Surfaces.Patches[new(FloorId, 0, 0)];
        foreach (var (id, start) in new[] { ("hall", 0), ("other-hall", 2) })
        {
            patch.Records.Add(new MapSpaceDoc(id, MapSpaceKind.Exterior, null, null, [], [], [], []));
            patch.Records.Add(new MapSpaceFootprint(id + "-domain", new(id, patch.Key), patch.Key,
                [start, start + 1, 64 + start, 65 + start], new(MapBoundKind.SupportFloor, FloorId, null), new(MapBoundKind.OpenTop, null, null)));
        }
        foreach (var (id, x) in new[] { ("house-1", 0.5f), ("house-2", 2.5f) })
            document.Placements.Add(new MapPlacement { Id = id, Kind = "tree", AssetId = "tree", X = x, Y = 1, Z = -0.5f, Scale = 1 });
        document.BuildingIntent = new(1, [], [], [], [new(1, "house-1", "House marker", new(0.5f, 1, -0.5f), 0, "fixture", true, [])]);
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            var surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
    }

    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) ClearanceAssets()
    {
        var (roots, source) = AssetInputs();
        var root = JsonNode.Parse(source.Read(roots[0]).Span)!.AsObject();
        foreach (var node in root["assets"]!.AsArray())
        {
            var asset = node!.AsObject();
            asset["supportResourceIds"] = new JsonArray();
            asset["lightResourceIds"] = new JsonArray();
            asset["lodResourceIds"] = new JsonArray();
            asset["sourceUnitsToMetres"] = 1;
            asset["renderBounds"] = JsonNode.Parse("""{"min":{"x":-0.05,"y":-0.05,"z":-0.05},"max":{"x":0.05,"y":0.05,"z":0.05}}""");
            asset.Remove("lodBounds");
            asset.Remove("lightBounds");
        }
        // Invented 0.1 m prop in the released KECL payload 1 format. This fixture stays physics-free.
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        { writer.Write(0x4B45434Cu); writer.Write((byte)1); writer.Write((byte)4); writer.Write(0.05f); writer.Write(0.05f); writer.Write(0.05f); }
        foreach (string id in new[] { "collider", "selection" })
        {
            var replaced = source.Add(id, stream.ToArray());
            root["resources"]!.AsArray().Single(r => r!["reference"]!["id"]!.GetValue<string>() == id)!["reference"]!["sha256"] = replaced.Sha256;
        }
        return (new[] { source.Add("root-a", root.ToJsonString()) }.Concat(roots.Skip(1)).ToArray(), source);
    }

}
