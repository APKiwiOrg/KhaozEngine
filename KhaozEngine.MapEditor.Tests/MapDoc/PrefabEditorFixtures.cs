using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Physics;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc;

internal static class PrefabEditorFixtures
{
    internal static readonly MapTransform Far = new(new(30000.25f, -450, -29999.75f), 0.371f, 1.2f);
    internal static PrefabFixture Create(string variant)
    {
        if (variant != "empty-map") return PrefabFixtures.Create(variant);
        var f = PrefabFixtures.Create();
        PrefabFixtures.AuthorWorldParent(f, new(new(10.25f, 5, -3.125f), 0.371f, 1.2f));
        f.Document.Prefabs = new(1, 1, [], []);
        return f;
    }
    internal static MapRecordRef ParentRef(PrefabFixture f) => new("exterior",
        f.Document.Surfaces.Patches.Values.Single(p => p.Records.OfType<MapSpaceDoc>().Any(s => s.Id == "exterior")).Key);
    internal static EditorHistory History(MapDocument document, MapAssetClosure assets)
    {
        var h = new EditorHistory();
        h.BindNativeAssets(document, assets, NativePlacementBoundsProvider.Instance);
        return h;
    }
    internal static PrefabFixture FarLeaf(bool attached)
    {
        var f = attached ? PrefabFixtures.MaterializedOverrideFixture() : PrefabFixtures.Create("roomless");
        if (!attached)
        {
            var asset = f.Assets.GetPrefab("hut");
            var leaf = (MapPrefabPlacementChild)asset.Children.Single();
            leaf.Value.X = 4; leaf.Value.Z = -4; leaf.Value.Yaw = 0.17f;
            f = PrefabFixtures.ExtractedFixture(asset, f.Assets, Far);
        }
        else
        {
            var i = f.Document.Prefabs!.Instances.Single();
            f.Document.Prefabs = new(1, 1, new[] { i with { Transform = Far } }, []);
        }
        f.Document.TileSize = 64;
        f.Document.Bounds = new() { MinX = 29800, MinZ = -30200, MaxX = 30200, MaxZ = -29800 };
        f.Document.PlayableBounds = f.Document.Bounds;
        string id = f.Document.Prefabs!.Instances.Single().Id;
        return new(MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab(id)).After, f.Assets);
    }
    internal static (MapDocument Document, MapAssetClosure Assets, IEditorCommand First, IEditorCommand Second, IEditorCommand Rejected)
        CoalescedAttachedLeaf(string operation)
    {
        var f = FarLeaf(true);
        const string id = "deck-owner";
        return operation switch
        {
            "move" => (f.Document, f.Assets, new MovePlacementCommand(id, 30003.25f, -30006.125f, -450),
                new MovePlacementCommand(id, 30003.5f, -30006, -450), new MovePlacementCommand(id, float.NaN, -30006, -450)),
            "rotate" => (f.Document, f.Assets, new RotatePlacementCommand(id, 0.4f), new RotatePlacementCommand(id, 0.5f), new RotatePlacementCommand(id, float.NaN)),
            "scale" => (f.Document, f.Assets, new ScalePlacementCommand(id, 1.1f), new ScalePlacementCommand(id, 0.8f), new ScalePlacementCommand(id, 0)),
            _ => throw new ArgumentException(operation),
        };
    }
    internal static (PrefabFixture Fixture, MapTerrainEdit Edit) SupportReseat()
    {
        var f = PrefabFixtures.Create();
        f.Document.Prefabs = null;
        f.Document.TileSize = 64;
        using var bytes = new MemoryStream();
        PropCollisionFormat.Write(new BoxShape(new(75, 0.1f, 0.1f)), bytes);
        var assets = PrefabFixtures.PublishPrefab(f.Assets, f.Assets.GetPrefab("hut"),
            new Dictionary<string, byte[]> { ["item.selection"] = bytes.ToArray() });
        f.Document.NativeAssets = assets.Roots.ToList();
        var patch = f.Document.Surfaces.Patches[new("exterior", 0, 0)];
        patch.Records.Add(new MapSpaceDoc("alias", MapSpaceKind.Exterior, null, new("exterior", patch.Key), [], [], [], []));
        f.Document.Placements.Add(new()
        {
            Id = "long-leaf",
            Kind = "item",
            AssetId = "item",
            X = 1,
            Z = -1,
            Y = null,
            SupportBinding = new(MapSupportBindingKind.Space, null, "alias", -1.5f, 4, 4)
        });
        return (new(f.Document, assets), new MapSetCornerHeights(patch.Key, 0, 0, 17, 17, Enumerable.Repeat(-150, 289).ToArray()));
    }
    internal static PrefabFixture MeshOnlyResource(string kind)
    {
        var f = PrefabFixtures.CapturedOriginals();
        var manifest = JsonNode.Parse(f.Assets.GetResource("manifest").Bytes.Span)!.AsObject();
        var leaf = manifest["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "deck")!.AsObject();
        leaf.Remove("collisionResourceId");
        leaf.Remove("selectionResourceId");
        if (kind != "support") leaf["supportResourceIds"] = new JsonArray();
        if (kind != "light") leaf["lightResourceIds"] = new JsonArray();
        var asset = f.Assets.GetPrefab("hut");
        var owner = (MapPrefabPlacementChild)asset.Children.Single();
        owner.Value.X = 63;
        owner.Value.Z = -1;
        var assets = PrefabFixtures.PublishPrefab(f.Assets, asset,
            new Dictionary<string, byte[]> { ["manifest"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()) });
        f.Document.NativeAssets = assets.Roots.ToList();
        f.Document.TileSize = 64;
        f.Document.Bounds = new() { MinX = -64, MinZ = -64, MaxX = 256, MaxZ = 64 };
        f.Document.PlayableBounds = f.Document.Bounds;
        f.Document.Prefabs = new(1, 1, [], []);
        if (kind == "world-light")
        {
            f.Document.Placements.Add(new() { Id = "owner", Kind = "deck", AssetId = "deck", X = 63, Y = 0, Z = -1 });
            f.Document.BuildingIntent = new(1, [], [], new[] { new MapLocalLightDoc(1, "owned-light",
                PrefabFixtures.World(MapPrefabChildKind.Placement, "owner"), new(0, 1, 0), Vector3.One, 1, 2, false) }, []);
            return new(f.Document, assets);
        }
        var placed = MapPrefabEdits.Prepare(f.Document, assets, new MapPlacePrefab("only", "hut", new(Vector3.Zero, 0, 1))).After;
        return new(MapPrefabEdits.Prepare(placed, assets, new MapUnpackPrefab("only")).After, assets);
    }
    internal static PrefabFixture IsolatedStorey()
    {
        var f = PrefabFixtures.Create("all-kinds");
        var source = f.Assets.GetPrefab("hut");
        var asset = source with { Children = source.Children.OfType<MapPrefabStoreyChild>().ToArray(), Roofs = [] };
        var assets = PrefabFixtures.PublishPrefab(f.Assets, asset);
        f.Document.NativeAssets = assets.Roots.ToList();
        var instance = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with
        { ResourceSha256 = assets.GetResource("hut").Reference.Sha256, Bindings = new(instance.Bindings.Entries.Where(b => b.Child.Kind == MapPrefabChildKind.Storey).ToArray()) } }, []);
        return new(f.Document, assets);
    }
    internal static PrefabFixture ReExtracted()
    {
        var f = PrefabFixtures.MaterializedOverrideFixture();
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var selected = unpack.Prefabs!.Transfers.Select(t => PrefabFixtures.World(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray();
        var asset = MapPrefabExtraction.Extract(unpack, f.Assets, selected, new(Vector3.Zero, 0, 1), "captured");
        return PrefabFixtures.ExtractedFixture(asset, f.Assets, new(Vector3.Zero, 0, 1));
    }
    internal static void Reload(MapDocument document, MapAssetClosure assets)
    {
        var reload = MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));
        MapBoundDocumentValidation.Validate(reload, assets);
        AssertEqualEffective(document, reload, assets);
    }
    static void AssertEqualEffective(MapDocument a, MapDocument b, MapAssetClosure assets) =>
        Xunit.Assert.Equal(MapPrefabResolver.Expand(a, assets).Digest, MapPrefabResolver.Expand(b, assets).Digest);
}
