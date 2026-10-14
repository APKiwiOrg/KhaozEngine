using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class PrefabServiceTests
{
    [Theory]
    [InlineData("before-bytes")]
    [InlineData("after-prefab")]
    [InlineData("after-manifest")]
    [InlineData("before-root-switch")]
    [InlineData("outer-validation")]
    public void AssetPublicationFailure_LeavesSessionAndOtherMapsUntouched(string checkpoint)
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("two-instances"));
        var before = PrefabEditorFixtures.State(session);
        string other = PrefabEditorFixtures.OtherMapBefore(session);
        Assert.Contains("publication fault " + checkpoint, Assert.Throws<MapDocumentException>(() => PrefabEditorFixtures.PublishMarkerChange(session, checkpoint)).Message);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        Assert.Equal(other, PrefabEditorFixtures.OtherMapNow(session));
        var otherSession = new MapEditSession();
        otherSession.Open(Path.Combine(Path.GetDirectoryName(session.DocumentPath)!, "other.map.json"));
        Assert.Equal(before.Identity, PrefabEditorFixtures.State(otherSession).Identity);
    }
    [Fact]
    public void Publication_UpdatesEveryReferrerAndKeepsOtherMapPinned()
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.Create("two-instances"));
        string other = PrefabEditorFixtures.OtherMapBefore(session);
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var marker = PrefabEditorFixtures.Marker(asset);
        service.Edit(marker with { Value = marker.Value with { Name = "Changed" } });
        var published = service.Save("hut", "published");
        Assert.StartsWith("prefab/hut/", published.Resource.Id);
        Assert.NotNull(published.Mutation);
        Assert.All(PrefabEditorFixtures.Document(session).Prefabs!.Instances,
            i => Assert.Equal((published.Resource.Id, published.Resource.Sha256), (i.ResourceId, i.ResourceSha256)));
        Assert.Equal(other, PrefabEditorFixtures.OtherMapNow(session));
        Assert.NotNull(session.UndoNative());
        Assert.Equal(before.Identity, PrefabEditorFixtures.State(session).Identity);
        Assert.NotNull(session.RedoNative());
        Assert.All(session.WithNativeAssets(c => MapEffectiveAnnotations.Markers(MapPrefabResolver.Expand(c.Document, c.Assets))), m => Assert.Equal("Changed", m.Name));
    }
    [Fact]
    public void RejectedOrCancelledEdit_LeavesHistoryIdentityAndDirtyUntouched()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var marker = PrefabEditorFixtures.Marker(asset);
        Assert.Throws<MapDocumentException>(() => service.Edit(marker with { Value = marker.Value with { Position = new(float.NaN, 1, 0) } }));
        service.Cancel();
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }
    [Fact]
    public void LocalWaterEdit_UsesTheNativeWaterCommand()
    {
        var f = PrefabFixtures.Create("all-resources");
        var local = new MapPrefabLocalSession(f.Assets.GetPrefab("hut"), f.Assets, f.Document, PrefabEditorFixtures.ParentRef(f));
        string before = Convert.ToHexString(MapPrefabFile.Write(local.Export()));
        var body = local.Document.Water!.Bodies.Single();
        local.Apply(new WaterEditCommand(new MapUpsertWaterBody(body with { SurfaceY = 0.75f })));
        Assert.Equal(0.75f, local.Export().Children.OfType<MapPrefabWaterChild>().Single().Value.SurfaceY);
        string after = Convert.ToHexString(MapPrefabFile.Write(local.Export()));
        Assert.True(local.Undo());
        Assert.Equal(before, Convert.ToHexString(MapPrefabFile.Write(local.Export())));
        Assert.True(local.Redo());
        Assert.Equal(after, Convert.ToHexString(MapPrefabFile.Write(local.Export())));
    }
    [Fact]
    public void LocalExport_MaterializedRowsPreserveEditedAttachments()
    {
        var f = PrefabEditorFixtures.LocalCapturedFixture();
        var session = PrefabEditorFixtures.Session(f);
        var service = new PrefabService(session);
        var source = service.Open("captured");
        var floor = source.Children.OfType<MapPrefabSurfaceChild>().Single();
        var localDocument = service.LocalSession!.Document;
        string floorId = localDocument.Prefabs!.Transfers.Single(t => t.Binding.Child == floor.Key).Binding.WorldId;
        var patch = localDocument.Surfaces.Patches.Values.Single(p => p.Key.SurfaceId == floorId);
        service.LocalSession.Apply(new TerrainEditCommand(new MapSetCornerHeights(patch.Key, 0, 0,
            patch.Width + 1, patch.Depth + 1, Enumerable.Repeat(75, patch.Heights.Length).ToArray())));
        var light = source.Children.OfType<MapPrefabLightChild>().Single();
        service.Edit(light with { Value = light.Value with { Position = new(0, 2.5f, 0), Radius = 4 } });
        var edited = service.Export();
        AssertCaptured(edited);
        string beforeRemove = Convert.ToHexString(MapPrefabFile.Write(edited));
        service.Edit(light, remove: true);
        Assert.Empty(service.Export().MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Light).Children);
        Assert.True(service.LocalSession!.Undo());
        Assert.Equal(beforeRemove, Convert.ToHexString(MapPrefabFile.Write(service.Export())));
        Assert.True(service.LocalSession.Redo());
        Assert.Empty(service.Export().MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Light).Children);
        Assert.True(service.LocalSession.Undo());
        var saved = service.Save("captured", "published");
        service.Cancel();
        AssertCaptured(service.Open(saved.Resource.Id));
        session.Save();
        var reopened = new MapEditSession(); reopened.Open(session.DocumentPath!);
        AssertCaptured(new PrefabService(reopened).Open(saved.Resource.Id));
        var effective = session.WithNativeAssets(c => MapPrefabResolver.Expand(c.Document, c.Assets));
        Assert.Single(MapEffectiveAnnotations.Lights(effective));
        Assert.Single(effective.Surfaces.Refs, s => s.Role == MapSurfaceRole.SupportFloor && s.Id != "ground");
    }
    [Fact]
    public void FileOnlySave_DoesNotChangeTheWorldOrHistory()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("roomless"));
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var child = asset.Children.OfType<MapPrefabPlacementChild>().Single();
        var changed = MapPrefabJson.Copy(child.Value); changed.X = 3;
        service.Edit(child with { Value = changed });
        var saved = service.Save("hut", "published", false);
        Assert.Null(saved.Mutation);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        Assert.Equal(3, service.Open(saved.Resource.Id).Children.OfType<MapPrefabPlacementChild>().Single().Value.X);
    }
    [Fact]
    public void ExistingDifferentBytesAndEscapedPaths_RefuseWithoutAnEdit()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("roomless"));
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session); var asset = service.Open("hut");
        byte[] bytes = MapPrefabFile.Write(asset);
        string address = "published/prefab/hut/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        string target = Path.Combine(Path.GetDirectoryName(session.DocumentPath)!, address);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "different bytes");
        Assert.Throws<MapDocumentException>(() => service.Save("hut", "published"));
        Assert.Equal("different bytes", File.ReadAllText(target));
        Assert.Throws<MapDocumentException>(() => service.Save("hut", "../escape"));
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }
    [Fact]
    public void ParentContext_IsRequiredOnlyForParentDependentAssets()
    {
        var roomless = PrefabFixtures.Create("roomless");
        var local = new MapPrefabLocalSession(roomless.Assets.GetPrefab("hut"), roomless.Assets, roomless.Document);
        Assert.Single(local.Export().Children);
        var parent = PrefabEditorFixtures.Create("empty-map");
        var service = new PrefabService(PrefabEditorFixtures.Session(parent));
        Assert.Throws<MapDocumentException>(() => service.Open("hut"));
    }
    [Fact]
    public void SavingAnIdenticalVersion_CreatesNoHistoryOrDirtyTransition()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("roomless"));
        var service = new PrefabService(session); service.Open("hut");
        var first = service.Save("hut", "published");
        session.Save(); service.Open(first.Resource.Id);
        var before = PrefabEditorFixtures.State(session);
        var repeated = service.Save(first.Resource.Id, "published");
        Assert.Equal(first.Resource, repeated.Resource); Assert.Null(repeated.Mutation);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }
    [Fact]
    public void OpenAfterColliderPublication_UsesTheCurrentBoundClosure()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        var service = new PrefabService(session); service.Open("hut");
        var published = service.Save("hut", "published");
        new NativeCollisionService(session).SetHeights("roof", -0.25f, 0.25f, false);
        string expected = session.WithNativeAssets(c => c.Assets.Roots.Single().Sha256);
        service.Open(published.Resource.Id);
        Assert.Equal(expected, service.LocalSession!.Document.NativeAssets.Single().Sha256);
    }
    [Fact]
    public void RemovingLocalOwner_OmitsRowsAndUndoRestoresCapturedCodecBytes()
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.LocalCapturedFixture());
        var service = new PrefabService(session); var asset = service.Open("captured");
        byte[] before = MapPrefabFile.Write(asset);
        var owner = asset.Children.OfType<MapPrefabPlacementChild>().Single();
        service.Edit(owner, true);
        Assert.Empty(service.Export().MaterializedAttachments); Assert.Empty(service.Export().Children);
        Assert.True(service.LocalSession!.Undo()); Assert.Equal(before, MapPrefabFile.Write(service.Export()));
        Assert.True(service.LocalSession.Redo()); Assert.Empty(service.Export().MaterializedAttachments);
    }
    [Fact]
    public void LocalSession_FarFreePlacementExportsTheSameLocalGeometry()
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f));
        var local = new MapPrefabLocalSession(f.Assets.GetPrefab("hut"), f.Assets, f.Document, PrefabEditorFixtures.ParentRef(f));
        var exported = local.Export();
        Assert.Equal(new Vector3(0, 1, 0), exported.Children.OfType<MapPrefabMarkerChild>().Single().Value.Position);
        Assert.Equal(0.5f, exported.Children.OfType<MapPrefabWaterChild>().Single().Value.SurfaceY);
        var floor = exported.Children.OfType<MapPrefabSurfaceChild>().Single(c => c.Key.Key == "floor");
        Assert.All(floor.Patches.SelectMany(p => p.Heights), height => Assert.Equal(0, height));
    }
    static void AssertCaptured(MapPrefabAsset asset)
    {
        Assert.Equal(2, asset.MaterializedAttachments.Count);
        Assert.All(asset.MaterializedAttachments, r => Assert.Equal(new MapEffectiveTransform(0, 0, 0, 0, 1), r.PlacementFrame));
        Assert.All(asset.MaterializedAttachments.SelectMany(r => r.Children), c =>
        { Assert.Equal(MapPrefabReferenceScope.Local, c.Child.Scope); Assert.Equal(new MapEffectiveTransform(0, 0, 0, 0, 1), c.OwnerLocalPose); });
        Assert.All(asset.MaterializedAttachments, r => Assert.Equal(MapPrefabReferenceScope.Local, r.Placement.Scope));
        Assert.All(asset.Children.OfType<MapPrefabSurfaceChild>().Single().Patches.SelectMany(p => p.Heights), h => Assert.Equal(75, h));
        var light = asset.Children.OfType<MapPrefabLightChild>().Single().Value;
        Assert.Equal(new Vector3(0, 2.5f, 0), light.Position); Assert.Equal(4, light.Radius);
        Assert.Equal(PrefabFixtures.CapturedOriginals().Assets.GetResource("deck-support").Reference.Sha256, asset.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Support).ResourceSha256);
        Assert.Equal(PrefabFixtures.CapturedOriginals().Assets.GetResource("glow-light").Reference.Sha256, asset.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Light).ResourceSha256);
    }
}
