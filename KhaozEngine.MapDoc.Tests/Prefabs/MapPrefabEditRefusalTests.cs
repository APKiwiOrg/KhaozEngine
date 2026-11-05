using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabEditRefusalTests
{
    static readonly MapTransform Pose = new(Vector3.Zero, 0, 1);

    [Fact]
    public void Asset_update_requires_the_two_closure_entry_point()
    {
        var f = PrefabFixtures.SpacelessRoomless();
        string hash = f.Assets.GetResource("hut").Reference.Sha256;
        var request = new MapUpdatePrefabAsset("hut", hash, "hut", hash);
        string before = MapDocumentFile.SaveText(f.Document);
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, f.Assets, request));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Equal(f.Assets.Roots, MapPrefabEdits.PrepareAssetUpdate(f.Document, f.Assets, f.Assets, request).After.NativeAssets);
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.PrefabInstanceMissing)]
    [InlineData("removed", MapDocumentRefusal.PrefabInstanceMissing)]
    [InlineData("reserved-live", MapDocumentRefusal.IdentityReserved)]
    [InlineData("reserved-removed", MapDocumentRefusal.IdentityReserved)]
    [InlineData("kind", MapDocumentRefusal.ReferenceKindMismatch)]
    public void Instance_admission_names_the_refused_identity_or_resource(string fault, MapDocumentRefusal reason)
    {
        var f = PrefabFixtures.SpacelessRoomless();
        var live = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapPlacePrefab("a", "hut", Pose)).After;
        var document = fault is "removed" or "reserved-removed"
            ? MapPrefabEdits.Prepare(live, f.Assets, new MapRemovePrefab("a")).After : live;
        string id = fault == "missing" ? "missing" : fault == "kind" ? "b" : "a";
        MapPrefabEdit request = fault is "missing" or "removed" ? new MapMovePrefab(id, Pose)
            : new MapPlacePrefab(id, fault == "kind" ? "mesh" : "hut", Pose);
        string before = MapDocumentFile.SaveText(document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(document, f.Assets, request));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(id, error.InstanceId);
        if (fault == "kind")
        {
            Assert.Equal("mesh", error.ResourceId);
            Assert.Equal(MapResourceKind.Prefab, error.ExpectedResourceKind);
            Assert.Equal(MapResourceKind.Mesh, error.ActualResourceKind);
        }
        Assert.Equal(before, MapDocumentFile.SaveText(document));
        MapPrefabEdit repaired = fault is "missing" or "removed" ? new MapMovePrefab("a", Pose)
            : new MapPlacePrefab(fault == "kind" ? id : "b", "hut", Pose);
        var accepted = MapPrefabEdits.Prepare(fault == "removed" ? live : document, f.Assets, repaired);
        Assert.Contains(accepted.After.Prefabs!.Instances, i => !i.Removed && i.Id == (repaired is MapMovePrefab ? "a" : "b"));
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.AuthoredValueRequired)]
    [InlineData(true, MapDocumentRefusal.PrefabChildAlreadyRemoved)]
    public void Child_edits_require_the_matching_remove_state(bool alreadyRemoved, MapDocumentRefusal reason)
    {
        var f = PrefabFixtures.Create();
        var child = new MapPrefabChildKey(MapPrefabChildKind.Marker, "entry");
        var remove = new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(child));
        var removed = MapPrefabEdits.Prepare(f.Document, f.Assets, remove).After;
        var document = alreadyRemoved ? removed : f.Document;
        MapPrefabEdit request = alreadyRemoved ? remove : new MapRestorePrefabChild("a", child);
        string before = MapDocumentFile.SaveText(document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(document, f.Assets, request));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("a", error.InstanceId);
        Assert.Equal(child, error.Child);
        Assert.Equal(before, MapDocumentFile.SaveText(document));
        var accepted = MapPrefabEdits.Prepare(alreadyRemoved ? f.Document : removed, f.Assets, request).After;
        Assert.Equal(alreadyRemoved, accepted.Prefabs!.Instances.Single().Overrides.Any(o => o.Child == child && o is MapPrefabRemoveOverride));
    }

    [Theory]
    [InlineData("null-list", MapDocumentRefusal.AuthoredValueRequired, "$.initialOverrides")]
    [InlineData("null-item", MapDocumentRefusal.AuthoredValueRequired, "$.initialOverrides[0]")]
    [InlineData("duplicate", MapDocumentRefusal.PrefabOverrideDuplicate, "$.initialOverrides[1]")]
    public void Initial_overrides_refuse_only_the_missing_or_repeated_value(string fault, MapDocumentRefusal reason, string path)
    {
        var f = PrefabFixtures.SpacelessRoomless();
        var child = new MapPrefabChildKey(MapPrefabChildKind.Placement, "item");
        MapPrefabOverride value = new MapPrefabTransformOverride(child, Pose);
        var request = new MapPlacePrefab("b", "hut", Pose)
        {
            InitialOverrides = fault switch { "null-list" => null!, "null-item" => [null!], _ => [value, value] },
        };
        string before = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, f.Assets, request));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("b", error.InstanceId);
        Assert.Equal(path, error.FieldPath);
        if (fault == "duplicate") Assert.Equal(child, error.Child);
        else Assert.Null(error.Child);
        Assert.Equal(fault == "null-list" ? (int?)null : fault == "null-item" ? 0 : 1, error.Index);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        var repaired = request with { InitialOverrides = fault == "null-list" ? [] : [value] };
        var accepted = MapPrefabEdits.Prepare(f.Document, f.Assets, repaired).After;
        Assert.Equal(repaired.InitialOverrides.Count, Assert.Single(accepted.Prefabs!.Instances).Overrides.Count);
    }

    [Theory]
    [InlineData("absent-record", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("wrong-record", MapDocumentRefusal.AuthoredValueMismatch)]
    public void Parent_requirement_retains_the_instance_record_and_anchor(string fault, MapDocumentRefusal reason)
    {
        var f = PrefabFixtures.Create();
        f.Document.Prefabs = new(1, 1, [], []);
        var anchor = new MapPatchKey("exterior", 0, 0);
        var parent = new MapRecordRef(fault == "absent-record" ? "absent" : "exterior-domain", anchor);
        var request = new MapPlacePrefab("b", "hut", Pose, parent)
        { InitialOverrides = PrefabFixtures.MarkerNames(f.Assets.GetPrefab("hut"), "b") };
        string before = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, f.Assets, request));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("b", error.InstanceId);
        Assert.Equal("$.parentSpace", error.FieldPath);
        Assert.Equal(parent.Id, error.SpaceId);
        Assert.Equal(parent.Anchor, error.Patch);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        var repaired = request with { ParentSpace = new("exterior", anchor) };
        var accepted = MapPrefabEdits.Prepare(f.Document, f.Assets, repaired).After;
        Assert.Equal(repaired.ParentSpace, accepted.Prefabs!.Instances.Single(i => i.Id == "b").ParentSpace);
    }

    [Theory]
    [InlineData("old-kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("new-kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("old-pin", MapDocumentRefusal.PrefabAssetPinMismatch)]
    [InlineData("new-pin", MapDocumentRefusal.PrefabAssetPinMismatch)]
    public void Asset_update_reports_the_first_mismatching_kind_or_pin(string fault, MapDocumentRefusal reason)
    {
        var f = PrefabFixtures.SpacelessRoomless();
        var old = f.Assets.GetResource(fault == "old-kind" ? "mesh" : "hut");
        var next = f.Assets.GetResource(fault == "new-kind" ? "mesh" : "hut");
        var request = new MapUpdatePrefabAsset(old.Reference.Id, fault == "old-pin" ? new string('f', 64) : old.Reference.Sha256,
            next.Reference.Id, fault == "new-pin" ? new string('f', 64) : next.Reference.Sha256);
        string before = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabEdits.PrepareAssetUpdate(f.Document, f.Assets, f.Assets, request));
        bool oldFault = fault.StartsWith("old", StringComparison.Ordinal);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(oldFault ? request.ResourceId : request.NewResourceId, error.ResourceId);
        Assert.Equal(oldFault ? request.OldSha256 : request.NewSha256, error.ExpectedHash);
        Assert.Equal(oldFault ? old.Reference.Sha256 : next.Reference.Sha256, error.ActualHash);
        Assert.Equal(MapResourceKind.Prefab, error.ExpectedResourceKind);
        Assert.Equal(oldFault ? old.Kind : next.Kind, error.ActualResourceKind);
        Assert.Equal("$." + (fault switch { "old-kind" => "resourceId", "new-kind" => "newResourceId", "old-pin" => "oldSha256", _ => "newSha256" }), error.FieldPath);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        string hash = f.Assets.GetResource("hut").Reference.Sha256;
        var repaired = new MapUpdatePrefabAsset("hut", hash, "hut", hash);
        Assert.Equal(f.Assets.Roots, MapPrefabEdits.PrepareAssetUpdate(f.Document, f.Assets, f.Assets, repaired).After.NativeAssets);
    }

    [Fact]
    public void Captured_instance_pin_keeps_the_request_and_instance_digests_distinct()
    {
        var f = PrefabFixtures.Create("roomless");
        var instance = Assert.Single(f.Document.Prefabs!.Instances);
        string before = MapDocumentFile.SaveText(f.Document);
        string requested = new('f', 64);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabEdits.RequireMatchingInstancePinForTests(instance, requested));
        Assert.Equal(MapDocumentRefusal.PrefabAssetPinMismatch, error.Reason);
        Assert.Equal(instance.Id, error.InstanceId);
        Assert.Equal(instance.ResourceId, error.ResourceId);
        Assert.Equal(requested, error.ExpectedHash);
        Assert.Equal(instance.ResourceSha256, error.ActualHash);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        MapPrefabEdits.RequireMatchingInstancePinForTests(instance, instance.ResourceSha256);
    }
}
