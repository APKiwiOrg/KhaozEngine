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
        if (variant is "two-instances" or "mixed-history")
        {
            var f = PrefabFixtures.Create("all-resources");
            f.Document.Placements.Add(new() { Id = "collider-control", Kind = "roof", AssetId = "roof", X = 12, Y = 0, Z = -12 });
            var placed = MapPrefabEdits.Prepare(f.Document, f.Assets,
                new MapPlacePrefab("b", "hut", new(new(8, 0, 0), 0, 1), ParentRef(f))).After;
            if (variant == "two-instances") return new(placed, f.Assets);
            return new(MapPrefabEdits.Prepare(placed, f.Assets, new MapUnpackPrefab("b")).After, f.Assets);
        }
        if (variant == "native-generic")
        {
            var f = PrefabFixtures.Create("roomless");
            f.Document.ScatterLayers.Add(new() { Name = "native-bake", Rules = new() { new() { Density = 0, Kinds = new() { new() { Id = "tree-pine", Weight = 1 } } } } });
            f.Document.ScatterOverrides.Add(new() { Shape = new RectShapeDoc { MinX = 0, MinZ = 0, MaxX = 1, MaxZ = 1 }, DensityMultiplier = 1 });
            f.Document.Regions.Add(new() { Name = "unrelated", Shape = new RectShapeDoc { MinX = 2, MinZ = 2, MaxX = 3, MaxZ = 3 } });
            return f;
        }
        if (variant != "empty-map") return PrefabFixtures.Create(variant);
        var empty = PrefabFixtures.Create();
        PrefabFixtures.AuthorWorldParent(empty, new(new(10.25f, 5, -3.125f), 0.371f, 1.2f));
        empty.Document.Prefabs = new(1, 1, [], []);
        return empty;
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
    internal static PrefabFixture LocalCapturedFixture()
    {
        var f = PrefabFixtures.MaterializedExtraction();
        var asset = MapPrefabExtraction.Extract(f.Document, f.Assets, f.Selection, f.Origin, "captured");
        return PrefabFixtures.ExtractedFixture(asset, f.Assets, new(Vector3.Zero, 0, 1));
    }
    internal static PrefabFixture StandaloneInterior(bool domainPresent)
    {
        var f = PrefabFixtures.Create();
        var doc = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var removed = doc.Surfaces.AllRecords().Where(r => r is MapCavePortal or MapBoundaryChain || !domainPresent && r is MapPrismDomain)
            .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var patch in doc.Surfaces.Patches.Values) patch.Records.RemoveAll(r => removed.Contains(r.Id));
        var intent = doc.BuildingIntent!;
        doc.BuildingIntent = new(1, [], [], intent.Lights, intent.Markers) { Roofs = intent.Roofs, Storeys = intent.Storeys };
        doc.NativeResourceTransforms = doc.NativeResourceTransforms!.Where(t => !removed.Contains(t.OwnerId) && t.Kind != MapPrefabChildKind.RoofLink).ToArray();
        var set = doc.Prefabs!;
        doc.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, set.Instances,
            set.Transfers.Select(t => removed.Contains(t.Binding.WorldId) || t.Binding.Child.Kind == MapPrefabChildKind.RoofLink ? t with { Retired = true } : t).ToArray())
        { Attachments = set.Attachments };
        return new(doc, f.Assets);
    }
    internal static void Reload(MapDocument document, MapAssetClosure assets)
    {
        var reload = MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));
        MapBoundDocumentValidation.Validate(reload, assets);
        AssertEqualEffective(document, reload, assets);
    }
    internal static MapEditSession Session(PrefabFixture fixture)
    {
        string dir = PrefabFixtures.TempDirectory();
        foreach (var resource in fixture.Assets.CapturedResources)
        {
            string target = Path.Combine(dir, resource.Reference.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, resource.Bytes.ToArray());
        }
        string path = Path.Combine(dir, "world.map.json");
        MapDocumentFile.Save(fixture.Document, path);
        File.Copy(path, Path.Combine(dir, "other.map.json"));
        var session = new MapEditSession();
        session.Open(path);
        return session;
    }
    internal static MapDocument Document(MapEditSession session) => session.WithDocument((d, r) => NativeDocumentSnapshot.Clone(d, r));
    internal static PrefabSessionState State(MapEditSession session) => session.WithNativeAssets<PrefabSessionState>(c => new(
        MapNativeResolution.Resolve(c.Document, c.Assets, NativeDocumentService.SessionOptionsV2).AuthoredHash,
        MapDocumentFile.SaveText(c.Document), string.Join("|", c.Document.NativeAssets.Select(r => r.ToString())),
        c.Document.Prefabs?.Digest() ?? "", c.Assets.Hash, session.NativeHistoryState.Cursor,
        session.NativeHistoryState.Depth, session.NativeHistoryState.Events, session.IsDirty, session.LastNativeEffects?.Describe()));
    internal static string OtherMapBefore(MapEditSession session) => File.ReadAllText(Path.Combine(Path.GetDirectoryName(session.DocumentPath)!, "other.map.json"));
    internal static string OtherMapNow(MapEditSession session) => OtherMapBefore(session);
    internal static MapPrefabMarkerChild Marker(MapPrefabAsset asset) => (MapPrefabMarkerChild)asset.Children.Single(c => c.Key.Kind == MapPrefabChildKind.Marker);
    internal static void PublishMarkerChange(MapEditSession session, string checkpoint)
    {
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var marker = Marker(asset);
        service.Edit(marker with { Value = marker.Value with { Name = "Changed" } });
        new PrefabAssetPublisher(session, new FaultFiles(checkpoint)).Publish(service.Export(), "hut", "published", true);
    }
    sealed class FaultFiles(string checkpoint) : IMapPrefabPublicationFiles
    {
        public void PublishNew(string path, ReadOnlyMemory<byte> bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes.ToArray());
        }
        public void Checkpoint(string name) { if (name == checkpoint) throw new MapDocumentException("publication fault " + name); }
    }
    internal static IReadOnlyList<PrefabSessionState> ApplyMixedHistory(MapEditSession session, bool water = false)
    {
        var states = new List<PrefabSessionState> { State(session) };
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var marker = Marker(asset);
        service.Edit(marker with { Value = marker.Value with { Name = "Changed" } });
        service.Save("hut", "published");
        states.Add(State(session));
        var mutation = new MutationService(session);
        var current = Document(session);
        if (water) mutation.WaterApply(new MapUpsertWaterBody(current.Water!.Bodies.Single() with { SurfaceY = 0.75f }));
        else
        {
            var floor = current.Surfaces.Patches.Values.Single(p => p.Key.SurfaceId == current.Prefabs!.Transfers.Single(t =>
                t.InstanceId == "b" && t.Binding.Child == new MapPrefabChildKey(MapPrefabChildKind.Surface, "pool-floor")).Binding.WorldId);
            mutation.TerrainApply(new MapSetCornerHeights(floor.Key, 0, 0, floor.Width + 1, floor.Depth + 1, Enumerable.Repeat(-75, floor.Heights.Length).ToArray()));
        }
        states.Add(State(session));
        new NativeCollisionService(session).SetHeights("roof", -0.25f, 0.25f, false);
        states.Add(State(session));
        return states;
    }
    internal static void AssertPublishedState(MapEditSession session, PrefabSessionState expected)
    {
        var actual = State(session);
        Xunit.Assert.Equal(expected.Identity, actual.Identity);
        Xunit.Assert.Equal(expected.DocumentBytes, actual.DocumentBytes);
        Xunit.Assert.Equal(expected.RootPins, actual.RootPins);
        Xunit.Assert.Equal(expected.Bindings, actual.Bindings);
        Xunit.Assert.Equal(expected.ClosureHash, actual.ClosureHash);
        Xunit.Assert.Equal(expected.Cursor, actual.Cursor);
        Xunit.Assert.Equal(3, actual.Depth);
        Xunit.Assert.True(actual.Dirty);
        session.WithNativeAssets(c => { MapBoundDocumentValidation.ValidateEffectiveWater(MapPrefabResolver.Expand(c.Document, c.Assets), c.Assets); return 0; });
    }
    internal static void ApplyGeneric(MapEditSession session, string variant)
    {
        var mutation = new MutationService(session);
        if (variant == "scatter") mutation.ScatterOverrideEdit(0, densityMultiplier: 2);
        else mutation.BakeRegion("native-bake", 0, 0, 1, 1);
    }
    internal static void AssertGenericRoots(MapEditSession session, string variant, bool applied)
    {
        var doc = Document(session);
        Xunit.Assert.Equal(variant == "scatter" && applied ? 2 : 1, doc.ScatterOverrides.Single().DensityMultiplier);
        Xunit.Assert.Equal(variant == "bake" && applied ? 1 : 0, doc.Exclusions.Count);
        if (variant == "bake" && applied)
        {
            var exclusion = Xunit.Assert.Single(doc.Exclusions);
            Xunit.Assert.Equal(new[] { "native-bake" }, exclusion.Layers);
            var rect = Xunit.Assert.IsType<RectShapeDoc>(exclusion.Shape);
            Xunit.Assert.Equal((0f, 0f, 1f, 1f), (rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ));
            Xunit.Assert.Empty(doc.Placements);
        }
        Xunit.Assert.Equal("unrelated", Xunit.Assert.Single(doc.Regions).Name);
    }
    internal static void AssertSavedGenericRoots(MapEditSession session, string variant, bool applied)
    {
        string saved = Path.Combine(Path.GetDirectoryName(session.DocumentPath)!, "reloaded-" + Guid.NewGuid().ToString("N") + ".map.json");
        MapDocumentFile.Save(Document(session), saved);
        var reopened = new MapEditSession();
        reopened.Open(saved);
        AssertGenericRoots(reopened, variant, applied);
        Xunit.Assert.Equal(State(session).Identity, State(reopened).Identity);
        Xunit.Assert.Equal(State(session).RootPins, State(reopened).RootPins);
        Xunit.Assert.Equal(0, reopened.NativeHistoryState.Depth);
    }
    static void AssertEqualEffective(MapDocument a, MapDocument b, MapAssetClosure assets) =>
        Xunit.Assert.Equal(MapPrefabResolver.Expand(a, assets).Digest, MapPrefabResolver.Expand(b, assets).Digest);
}

internal sealed record PrefabSessionState(string Identity, string DocumentBytes, string RootPins, string Bindings,
    string ClosureHash, int Cursor, int Depth, long EventCount, bool Dirty, string? Effects);
