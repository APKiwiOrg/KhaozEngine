using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

// F-R2. A standalone placement keeps its released world-space support request. Only a prefab-local request
// transforms by the instance pose, and unpack persists that request in world space from the pre-seat frame.
public sealed class MapPrefabSupportRequestTests
{
    [Fact]
    public void ScaledStandalonePlacement_KeepsItsWorldSpaceRequestOnWholeAndSubsetPaths()
    {
        MapDocument document = SupportFixtures.CaveWithPlacements();
        document.Placements.Single(p => p.Id == "p-space").Scale = 2;
        var assets = SupportFixtures.Assets();
        var whole = MapResolverV2.Resolve(document, assets, MapDocumentSurfaceSource.Capture(document), SupportFixtures.V2);
        Assert.Equal(4.0f, whole.Document.Placements.Single(p => p.PlacementId == "p-space").Transform.Position.Y);
        var native = MapNativeResolution.Resolve(document, assets, SupportFixtures.V2);
        string[] ids = document.Placements.Select(p => p.Id).ToArray();
        var subset = MapNativeResolution.ResolvePlacements(document, assets, SupportFixtures.V2, ids);
        Assert.Equal(native.Placements.Select(p => (p.PlacementId, p.Transform)), subset.Select(p => (p.PlacementId, p.Transform)));
        Assert.Equal(whole.Document.Placements.Select(p => (p.PlacementId, p.Transform)), native.Placements.Select(p => (p.PlacementId, p.Transform)));
        Assert.Equal(2f, native.Placements.Single(p => p.PlacementId == "p-space").Transform.Scale);
    }

    [Fact]
    public void SpaceBoundUnpack_PersistsWorldRequestThenReloadsAndReseats()
    {
        var f = PrefabFixtures.SpaceBoundChild(10);
        var before = PrefabFixtures.Effective(f);
        var seated = Assert.Single(before.PlacementSupports);
        Assert.Equal("consumer", seated.PlacementId);
        Assert.Equal(12.4f, seated.Support.WorldY, 5);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var request = unpack.Placements.Single(p => p.Id == "consumer").SupportBinding!;
        // The world request comes from the pre-seat frame at Y 10, never from the seated floor at 12.4.
        Assert.Equal(MapSupportBindingKind.Space, request.Kind);
        Assert.Equal(12.64f, request.ReferenceY!.Value, 5);
        Assert.Equal((1.2f, 1.2f), (request.SearchBelow!.Value, request.SearchAbove!.Value));
        foreach (var document in new[] { unpack, MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpack)) })
        {
            var item = document.Placements.Single(p => p.Id == "consumer");
            Assert.Null(item.Y);
            Assert.Equal(request, item.SupportBinding);
            var effective = MapPrefabResolver.Expand(document, f.Assets);
            var support = Assert.Single(effective.PlacementSupports);
            Assert.Equal(seated.Support.Face, support.Support.Face);
            Assert.Equal(seated.Support.WorldY, support.Support.WorldY);
            Assert.Equal(seated.Support.WorldY, effective.Placements.Single(p => p.PlacementId == "consumer").Transform.Position.Y);
        }
        var changed = MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpack));
        string floor = seated.Support.Face!.Value.OwnerId;
        foreach (var patch in changed.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == floor)) Array.Fill(patch.Heights, 50);
        PrefabFixtures.RefreshCapturedPins(changed);
        for (int index = 0; index < changed.Surfaces.Refs.Count; index++)
        {
            var surface = changed.Surfaces.Refs[index];
            changed.Surfaces.Refs[index] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, changed.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
        foreach (var document in new[] { changed, MapDocumentFile.LoadText(MapDocumentFile.SaveText(changed)) })
        {
            var effective = MapPrefabResolver.Expand(document, f.Assets);
            Assert.Equal(13.0f, Assert.Single(effective.PlacementSupports).Support.WorldY, 5);
            Assert.Equal(13.0f, effective.Placements.Single(p => p.PlacementId == "consumer").Transform.Position.Y, 5);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void SpaceBoundExtract_InvertsTheStandaloneWorldRequestAtItsOrigin(float originScale)
    {
        var f = PrefabFixtures.SpaceBoundChild(10);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var world = unpack.Placements.Single(p => p.Id == "consumer").SupportBinding!;
        var origin = new MapTransform(new(3, 10, -2), 0.25f, originScale);
        var asset = MapPrefabExtraction.Extract(unpack, f.Assets,
            [new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "consumer")], origin, "consumer-only");
        var child = Assert.Single(asset.Children.OfType<MapPrefabPlacementChild>());
        Assert.Null(child.Value.Y);
        var frame = MapEffectiveTransform.Compose(MapEffectiveTransform.From(origin), MapPrefabMaterializationOverrides.Pose(child.Value));
        var replayed = MapPrefabResolver.WorldRequest(child.Value.SupportBinding!, frame);
        Assert.Equal(world.ReferenceY!.Value, replayed.ReferenceY!.Value, 4);
        Assert.Equal(world.SearchBelow!.Value, replayed.SearchBelow!.Value, 5);
        Assert.Equal(world.SearchAbove!.Value, replayed.SearchAbove!.Value, 5);
        Assert.Equal(world with { ReferenceY = null, SearchBelow = null, SearchAbove = null },
            child.Value.SupportBinding! with { ReferenceY = null, SearchBelow = null, SearchAbove = null });
    }

    [Fact]
    public void CapturedOwnerExtract_PlacesAndUnpacksAgainstTheSameDeclaredFrame()
    {
        // F-R7. A missing-Y light-bearing owner gains a captured Light row at unpack. Its extracted request must invert
        // against the captured PlacementFrame that placement then declares, not against the nullable-Y pose.
        var f = PrefabFixtures.SpaceBoundChild(10, "lantern");
        var seated = Assert.Single(PrefabFixtures.Effective(f).PlacementSupports);
        Assert.Equal(12.4f, seated.Support.WorldY, 5);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var world = unpack.Placements.Single(p => p.Id == "consumer").SupportBinding!;
        var row = Assert.Single(unpack.Prefabs!.Attachments, a => a.PlacementId == "consumer" && a.Kind == MapPrefabAttachmentKind.Light);
        Assert.NotEmpty(row.Children);
        var selection = row.Children.Select(c => new MapPrefabReference(MapPrefabReferenceScope.World, c.LocalChild.Kind, c.WorldId))
            .Prepend(new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "consumer")).ToArray();
        var origin = new MapTransform(new(3, 7, -2), 0.25f, 2);
        var extracted = MapPrefabExtraction.Extract(unpack, f.Assets, selection, origin, "lamp-post");
        var asset = MapPrefabFile.Read(MapPrefabFile.Write(extracted), "lamp-post");
        var owner = Assert.Single(asset.Children.OfType<MapPrefabPlacementChild>());
        Assert.Null(owner.Value.Y);
        Assert.Equal(MapPrefabReferenceScope.World, owner.References.Single(r => r.Path == "/value/supportBinding/spaceId").Target.Scope);
        Assert.Single(asset.MaterializedAttachments, m => m.Placement.Id == owner.Key.Key);
        var assets = PrefabFixtures.PublishPrefab(f.Assets, asset);
        unpack.NativeAssets = assets.Roots.ToList();
        var placed = MapPrefabEdits.Prepare(unpack, assets, new MapPlacePrefab("copy", asset.Id, origin)).After;
        string copy = placed.Prefabs!.Instances.Single(i => i.Id == "copy").Bindings.Entries.Single(b => b.Child == owner.Key).WorldId;
        var placedSupport = Assert.Single(MapPrefabResolver.Expand(placed, assets).PlacementSupports, s => s.PlacementId == copy);
        Assert.Equal(seated.Support.Face, placedSupport.Support.Face);
        Assert.Equal(seated.Support.WorldY, placedSupport.Support.WorldY);
        var again = MapPrefabEdits.Prepare(placed, assets, new MapUnpackPrefab("copy")).After;
        foreach (var document in new[] { again, MapDocumentFile.LoadText(MapDocumentFile.SaveText(again)) })
        {
            var request = document.Placements.Single(p => p.Id == copy).SupportBinding!;
            Assert.Equal(world.ReferenceY!.Value, request.ReferenceY!.Value, 4);
            Assert.Equal(world.SearchBelow!.Value, request.SearchBelow!.Value, 4);
            Assert.Equal(world.SearchAbove!.Value, request.SearchAbove!.Value, 4);
            var resolved = Assert.Single(MapPrefabResolver.Expand(document, assets).PlacementSupports, s => s.PlacementId == copy);
            Assert.Equal((seated.Support.Face, seated.Support.WorldY), (resolved.Support.Face, resolved.Support.WorldY));
        }
    }
}
