using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabExtractionRefusalTests
{
    [Theory]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("local", MapDocumentRefusal.PrefabExtractionReferenceInvalid)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("absent", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("empty", MapDocumentRefusal.PrefabExtractionEmpty)]
    public void Selection_admission_reports_the_refused_reference(string fault, MapDocumentRefusal reason)
    {
        var f = PrefabFixtures.SpacelessRoomless();
        var reference = PrefabFixtures.World(MapPrefabChildKind.Surface, "ground");
        MapPrefabReference[] selected = fault switch
        {
            "null" => [null!], "local" => [reference with { Scope = MapPrefabReferenceScope.Local }],
            "duplicate" => [reference, reference], "absent" => [reference with { Id = "absent" }], _ => [],
        };
        string before = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabExtraction.Extract(f.Document, f.Assets, selected,
            new(Vector3.Zero, 0, 1), "extracted"));
        Assert.Equal(reason, error.Reason);
        if (fault == "empty") Assert.Equal(nameof(MapPrefabExtraction.Extract), error.Operation);
        else
        {
            int index = fault == "duplicate" ? 1 : 0;
            Assert.Equal(index, error.Index);
            Assert.Equal($"$.selected[{index}]", error.FieldPath);
            Assert.Equal(selected[index]?.Id, error.EntityId);
            Assert.Equal(selected[index]?.Kind, error.ChildKind);
            Assert.Equal(selected[index]?.Scope, error.ReferenceScope);
        }
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        var accepted = MapPrefabExtraction.Extract(f.Document, f.Assets, [reference], new(Vector3.Zero, 0, 1), "extracted");
        Assert.Equal(new MapPrefabChildKey(MapPrefabChildKind.Surface, "ground"), Assert.Single(accepted.Children).Key);
    }

    [Fact]
    public void Asset_removal_requires_the_declared_local_child()
    {
        var f = PrefabFixtures.Create("roomless");
        var asset = f.Assets.GetPrefab("hut");
        var original = Assert.IsType<MapPrefabPlacementChild>(Assert.Single(asset.Children));
        var missing = original with
        {
            Key = new(MapPrefabChildKind.Placement, "missing"),
            Value = MapPrefabReferenceWalker.Copy(original.Value, "missing", original.Value.SupportBinding),
        };
        byte[] before = MapPrefabFile.Write(asset);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabAssetEdits.Replace(asset, missing, remove: true));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(asset.Id, error.AssetId);
        Assert.Equal(missing.Key, error.Child);
        Assert.Equal(before, MapPrefabFile.Write(asset));
        Assert.Empty(MapPrefabAssetEdits.Replace(asset, original, remove: true).Children);
    }

    [Fact]
    public void Local_name_fallback_still_refuses_a_remaining_collision()
    {
        var f = PrefabFixtures.Create("roomless");
        var original = Assert.IsType<MapPrefabPlacementChild>(Assert.Single(f.Assets.GetPrefab("hut").Children));
        var odd = original with { Key = new(MapPrefabChildKind.Placement, "a/item"),
            Value = MapPrefabReferenceWalker.Copy(original.Value, "a/item", original.Value.SupportBinding) };
        var assets = PrefabFixtures.PublishPrefab(f.Assets, new(1, "odd", [odd]));
        f.Document.NativeAssets = assets.Roots.ToList();
        var pose = new MapTransform(Vector3.Zero, 0, 1);
        var second = MapPrefabEdits.Prepare(f.Document, assets, new MapPlacePrefab("b", "hut", pose)).After;
        var document = MapPrefabEdits.Prepare(second, assets, new MapPlacePrefab("c", "odd", pose)).After;
        var selected = document.Prefabs!.Instances.SelectMany(i => i.Bindings.Entries)
            .Select(b => PrefabFixtures.World(b.Child.Kind, b.WorldId)).ToArray();
        Assert.Equal(3, selected.Length);
        string before = MapDocumentFile.SaveText(document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabExtraction.Extract(document, assets, selected, pose, "extracted"));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(new MapPrefabChildKey(MapPrefabChildKind.Placement, "a/item"), error.Child);
        Assert.Equal(before, MapDocumentFile.SaveText(document));
        var accepted = MapPrefabExtraction.Extract(document, assets, selected.Where(r => r.Id != "a/item").ToArray(), pose, "extracted");
        Assert.Equal(2, accepted.Children.Count);
    }
}
