using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class PrefabAuthoringReviewTests
{
    [Fact]
    public void ExtractRoomAndWater_CarriesParentAndFarOriginThroughEditExportSaveAndReopen()
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f));
        var session = PrefabEditorFixtures.Session(f);
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session);
        var selected = Selection(f.Document);
        var expected = MapPrefabExtraction.Extract(f.Document, f.Assets, selected, PrefabEditorFixtures.Far, "hut");
        var opened = service.Extract(selected, PrefabEditorFixtures.Far, "hut");
        Assert.Equal(MapPrefabFile.Write(expected), MapPrefabFile.Write(opened));
        Assert.Equal(PrefabEditorFixtures.Far, service.LocalSession!.Document.Prefabs!.Instances.Last().Transform);
        var marker = opened.Children.OfType<MapPrefabMarkerChild>().Single();
        service.Edit(marker with { Value = marker.Value with { Name = "Extracted entry" } });
        var exported = service.Export();
        Assert.Equal(0.5f, exported.Children.OfType<MapPrefabWaterChild>().Single().Value.SurfaceY);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        var published = service.Save("hut", "published");
        session.Save();
        var reloaded = new MapEditSession(); reloaded.Open(session.DocumentPath!);
        var reopened = new PrefabService(reloaded).Open(published.Resource.Id);
        Assert.Equal(MapPrefabFile.Write(exported), MapPrefabFile.Write(reopened));
    }

    [Fact]
    public void ExtractNewRoomAsset_UsesSelectedParentWithoutInferringExterior()
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f));
        var session = PrefabEditorFixtures.Session(f);
        var service = new PrefabService(session);
        var asset = service.Extract(Selection(f.Document), PrefabEditorFixtures.Far, "new-room-asset");
        Assert.Equal("new-room-asset", asset.Id);
        var local = service.LocalSession!.Document.Prefabs!.Instances.Last();
        Assert.Equal(PrefabEditorFixtures.ParentRef(f), local.ParentSpace);
        Assert.Equal(PrefabEditorFixtures.Far, local.Transform);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnpackedRoomSource_OpensInCapturedContextAndKeepsWorldUntouched(bool far, bool save)
    {
        var f = far ? PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f)) : PrefabFixtures.Create("all-resources");
        var session = PrefabEditorFixtures.Session(f);
        new MutationService(session).PrefabApply(new MapUnpackPrefab("a"));
        var before = PrefabEditorFixtures.State(session);
        var beforeDocument = PrefabEditorFixtures.Document(session);
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var local = service.LocalSession!.Document;
        Assert.DoesNotContain(local.Surfaces.AllRecords(), r => r.Id == "a/room" || r.Id == "a/pool");
        Assert.DoesNotContain(local.Placements, p => p.Id.StartsWith("a/", StringComparison.Ordinal));
        Assert.DoesNotContain(local.Prefabs!.Transfers, t => t.InstanceId == "a");
        Assert.Equal(0.5f, asset.Children.OfType<MapPrefabWaterChild>().Single().Value.SurfaceY);
        var marker = asset.Children.OfType<MapPrefabMarkerChild>().Single();
        service.Edit(marker with { Value = marker.Value with { Name = "Detached edit" } });
        Assert.Equal("Detached edit", service.Export().Children.OfType<MapPrefabMarkerChild>().Single().Value.Name);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        if (save)
        {
            service.Save("hut", "published");
            session.Save();
            var reloaded = new MapEditSession(); reloaded.Open(session.DocumentPath!);
            Assert.Equal(PrefabEditorFixtures.State(session).Identity, PrefabEditorFixtures.State(reloaded).Identity);
            Assert.Equal("a/room", reloaded.WithNativeAssets(c => MapPrefabResolver.Expand(c.Document, c.Assets).Surfaces.AllRecords().OfType<MapSpaceDoc>().Single(s => s.Id == "a/room").Id));
        }
        else service.Cancel();
        var afterDocument = PrefabEditorFixtures.Document(session);
        afterDocument.NativeAssets = beforeDocument.NativeAssets;
        Assert.Equal(MapDocumentFile.SaveText(beforeDocument), MapDocumentFile.SaveText(afterDocument));
        Assert.Equal(before.Bindings, PrefabEditorFixtures.State(session).Bindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalInstanceId_AvoidsLiveAndTombstonedAuthoredCollisions(bool removed)
    {
        var f = PrefabFixtures.Create("roomless");
        var original = f.Document.Prefabs!.Instances.Single() with { Id = "prefab-local-session", Removed = removed };
        var reserved = original with
        {
            Id = "reservation-owner",
            Removed = true,
            Bindings = new(new[] { original.Bindings.Entries.Single() with
            { WorldId = MapPrefabBindingPlanner.WorldId("prefab-local-session-1", original.Bindings.Entries.Single().Child), NumericId = null, Removed = true } })
        };
        f.Document.Prefabs = new(1, 1, new[] { original, reserved }, []);
        var session = PrefabEditorFixtures.Session(f);
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session);
        var opened = service.Open("hut");
        string localId = service.LocalSession!.Document.Prefabs!.Transfers.Single().InstanceId;
        Assert.NotEqual(original.Id, localId);
        Assert.Equal("prefab-local-session-2", localId);
        var child = opened.Children.OfType<MapPrefabPlacementChild>().Single();
        var value = MapPrefabJson.Copy(child.Value); value.X = 2;
        service.Edit(child with { Value = value });
        Assert.True(service.LocalSession.Undo()); Assert.True(service.LocalSession.Redo());
        Assert.Equal(localId, service.LocalSession.Document.Prefabs!.Transfers.Single().InstanceId);
        Assert.Equal(2, service.Export().Children.OfType<MapPrefabPlacementChild>().Single().Value.X);
        service.Open("hut");
        Assert.Equal(localId, service.LocalSession.Document.Prefabs!.Transfers.Single().InstanceId);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        Assert.Equal(MapPrefabJson.Bytes(original), MapPrefabJson.Bytes(PrefabEditorFixtures.Document(session).Prefabs!.Instances.Single(i => i.Id == original.Id)));
        Assert.Equal(MapPrefabJson.Bytes(reserved), MapPrefabJson.Bytes(PrefabEditorFixtures.Document(session).Prefabs!.Instances.Single(i => i.Id == reserved.Id)));
    }

    [Fact]
    public void Extraction_AmbiguousAuthoredParentsRefusesAndPreservesLocalSession()
    {
        var world = PrefabFixtures.Create("all-resources");
        var parent = PrefabEditorFixtures.ParentRef(world);
        world.Document.Surfaces.Patches[parent.Anchor].Records.Add(new MapSpaceDoc("parent-alias", MapSpaceKind.Exterior,
            null, parent, [], [], [], []));
        PrefabFixtures.SealWorldParent(world.Document);
        var candidate = MapPrefabEdits.Prepare(world.Document, world.Assets, new MapPlacePrefab("b", "hut", new(new(8, 0, 0), 0, 1),
            new("parent-alias", parent.Anchor))
        { InitialOverrides = PrefabFixtures.MarkerNames(world.Assets.GetPrefab("hut"), "b") }).After;
        var session = PrefabEditorFixtures.Session(new(candidate, world.Assets));
        var service = new PrefabService(session);
        var existing = service.Open("hut", parent);
        var before = PrefabEditorFixtures.State(session);
        var selected = candidate.Prefabs!.Instances.SelectMany(i => i.Bindings.Entries).Where(b => !b.Removed)
            .Select(b => new MapPrefabReference(MapPrefabReferenceScope.World, b.Child.Kind, b.WorldId)).ToArray();
        Assert.Contains("ambiguous authored parent", Assert.Throws<MapDocumentException>(() => service.Extract(selected, new(Vector3.Zero, 0, 1), "ambiguous")).Message);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        Assert.Equal(MapPrefabFile.Write(existing), MapPrefabFile.Write(service.Export()));
    }

    internal static MapPrefabReference[] Selection(MapDocument document) => document.Prefabs!.Instances.Single().Bindings.Entries
        .Where(b => !b.Removed).Select(b => new MapPrefabReference(MapPrefabReferenceScope.World, b.Child.Kind, b.WorldId)).ToArray();
}
