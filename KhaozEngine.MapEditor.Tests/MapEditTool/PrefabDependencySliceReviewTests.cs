using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class PrefabDependencySliceReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldReferenceToCapturedOwner_RetainsOnlyRequiredDependencyGeometryAndOwnership(bool unpacked)
    {
        var f = PrefabEditorFixtures.LocalCapturedFixture();
        var owner = f.Document.Prefabs!.Instances.Single().Bindings.Entries.Single(b => b.Child.Kind == MapPrefabChildKind.Placement).WorldId;
        if (unpacked) f = new(MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("copy")).After, f.Assets);
        var observer = new MapPrefabAsset(1, "observer", new MapPrefabChild[]
        { new MapPrefabLightChild(new(MapPrefabChildKind.Light, "observer-light"), new(1, "observer-light",
            PrefabFixtures.World(MapPrefabChildKind.Placement, owner), new(0, 1, 0), Vector3.One, 1, 2, false)) });
        var assets = PrefabFixtures.PublishPrefab(f.Assets, observer);
        f.Document.NativeAssets = assets.Roots.ToList();
        var session = PrefabEditorFixtures.Session(new(f.Document, assets));
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session); var opened = service.Open("observer");
        var local = service.LocalSession!.Document;
        Assert.Contains(local.Placements, p => p.Id == owner);
        Assert.Equal(2, local.Prefabs!.Attachments.Count);
        Assert.DoesNotContain(local.Surfaces.Refs, s => s.Id == "ground");
        var light = opened.Children.OfType<MapPrefabLightChild>().Single();
        service.Edit(light with { Value = light.Value with { Radius = 3 } });
        Assert.Equal(3, service.Export().Children.OfType<MapPrefabLightChild>().Single().Value.Radius);
        service.Cancel(); Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    [Fact]
    public void WorldGeometryAndOwnerReferences_KeepDependenciesAndDiscardUnrelatedRecords()
    {
        foreach (var f in new[] { PrefabFixtures.WorldGeometryReference(), PrefabFixtures.WorldOwnedLight() })
        {
            f.Document.Placements.Add(new() { Id = "unrelated", Kind = "item", AssetId = "item", X = 12, Y = 0, Z = -12 });
            var session = PrefabEditorFixtures.Session(f);
            var before = PrefabEditorFixtures.State(session);
            var service = new PrefabService(session); var opened = service.Open("hut");
            Assert.DoesNotContain(service.LocalSession!.Document.Placements, p => p.Id == "unrelated");
            Assert.DoesNotContain(service.LocalSession.Document.Surfaces.AllRecords(), r => r.Id == "a/room");
            Assert.Equal(MapPrefabFile.Write(opened), MapPrefabFile.Write(service.Export()));
            service.Cancel(); Assert.Equal(before, PrefabEditorFixtures.State(session));
        }
    }

    [Fact]
    public void WorldReferenceToImplicitSupport_IncludesItsAuthoredProducer()
    {
        var f = PrefabFixtures.CapturedOriginals();
        f.Document.Prefabs = new(1, 1, [], []);
        f.Document.Placements.Add(new() { Id = "world-deck", Kind = "deck", AssetId = "deck", X = 5, Y = 0, Z = -5 });
        const string surface = "world-deck/deck-support/floor";
        var child = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "item"), new()
        {
            Id = "item",
            Kind = "item",
            AssetId = "item",
            X = 6,
            Y = null,
            Z = -6,
            SupportBinding = new(MapSupportBindingKind.Surface, surface, null, 0, 4, 4)
        })
        { References = new[] { new MapPrefabReferenceSlot("/value/supportBinding/surfaceId", PrefabFixtures.World(MapPrefabChildKind.Surface, surface)) } };
        var assets = PrefabFixtures.PublishPrefab(f.Assets, new(1, "observer", new[] { child }));
        f.Document.NativeAssets = assets.Roots.ToList();
        var session = PrefabEditorFixtures.Session(new(f.Document, assets));
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session); var opened = service.Open("observer");
        Assert.Contains(service.LocalSession!.Document.Placements, p => p.Id == "world-deck");
        Assert.Contains(service.LocalSession.Document.Surfaces.Refs, s => s.Id == surface);
        Assert.Equal(surface, opened.Children.OfType<MapPrefabPlacementChild>().Single().Value.SupportBinding!.SurfaceId);
        Assert.Null(opened.Children.OfType<MapPrefabPlacementChild>().Single().Value.Y);
        Assert.Equal(MapPrefabFile.Write(opened), MapPrefabFile.Write(service.Export()));
        var moved = MapPrefabJson.Copy(opened.Children.OfType<MapPrefabPlacementChild>().Single().Value); moved.X = 6.25f;
        service.Edit(opened.Children.OfType<MapPrefabPlacementChild>().Single() with { Value = moved });
        var published = service.Save("observer", "published", false);
        Assert.Null(service.Open(published.Resource.Id).Children.OfType<MapPrefabPlacementChild>().Single().Value.Y);
        service.Cancel(); Assert.Equal(before, PrefabEditorFixtures.State(session));
    }
}
