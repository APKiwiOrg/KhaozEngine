using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabUniqueRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Required_collections_and_items_have_distinct_owner_and_index_context(bool item)
    {
        var asset = item ? new MapPrefabAsset(1, "asset", [null!]) : new MapPrefabAsset(1, "asset", []) { Roofs = null! };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(asset));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(item ? "prefab child" : "roof inventory", error.SubjectKind);
        Assert.Equal(item ? 0 : (int?)null, error.Index);
        MapPrefabValidation.RequireAsset(new(1, "asset", item ? [new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "level"), "Level")] : []));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("null")]
    public void String_identities_retain_their_owner_for_duplicate_or_blank_keys(string fault)
    {
        bool blank = fault == "blank";
        var document = MapPrefabDocumentRefusalTests.Empty();
        var light = new MapLocalLightDoc(1, fault == "null" ? null! : blank ? "" : "lamp", null, Vector3.Zero, Vector3.One, 0, 1, false);
        document.BuildingIntent = new(1, [], [], blank ? [light] : [light, light], []);
        var finding = MapPrefabDocumentRefusalTests.Refused(document, null, blank ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.IdentityDuplicate);
        Assert.Equal(light.Id, finding.EntityId);
        Assert.Equal("light", finding.SubjectKind);
        Assert.Equal(blank ? 0 : 1, finding.Index);
        document.BuildingIntent = new(1, [], [], blank ? [light with { Id = "x" }] : [light with { Id = "lamp" }, light with { Id = "other" }], []);
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Fact]
    public void Child_keys_include_the_child_kind_namespace()
    {
        var child = new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "same"), "Level");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(new(1, "asset", [child, child])));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(child.Key, error.Child);
        Assert.Equal(MapPrefabChildKind.Storey, error.ChildKind);
        Assert.Equal("same", error.EntityId);
        Assert.Equal(1, error.Index);
        MapPrefabValidation.RequireAsset(new(1, "asset", [child, new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "same"),
            new MapPlacement { Id = "same", Kind = "mesh", AssetId = "leaf", Scale = 1 })]));
    }

    [Fact]
    public void Patch_keys_retain_surface_and_both_slot_coordinates()
    {
        var patch = PrefabFixtures.Patch("floor", 1, 1, 0);
        var child = PrefabFixtures.SurfaceChild("floor", MapSurfaceRole.SupportFloor, patch) with
        { Patches = [patch, patch.Clone()], References = [PrefabFixtures.Surf("/patches/0/key/surfaceId", "floor"), PrefabFixtures.Surf("/patches/1/key/surfaceId", "floor")] };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(new(1, "asset", [child])));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal("floor", error.SurfaceId);
        Assert.Equal(1, error.Index);
        var other = patch.Clone(); other.Key = new("floor", 1, 0);
        MapPrefabValidation.RequireAsset(new(1, "asset", [child with { Patches = [patch, other] }]));
    }

    [Fact]
    public void Roof_reference_keys_include_scope_and_kind()
    {
        var roof = new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof");
        var child = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "roof"), new MapPlacement { Id = "roof", Kind = "roof", AssetId = "leaf", Scale = 1 });
        var asset = new MapPrefabAsset(1, "asset", [child]) { Roofs = [roof, roof] };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(asset));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(roof.Scope, error.ReferenceScope);
        Assert.Equal(roof.Kind, error.ChildKind);
        Assert.Equal("roof", error.EntityId);
        Assert.Equal("roof inventory", error.SubjectKind);
        MapPrefabValidation.RequireAsset(asset with { Roofs = [roof, roof with { Scope = MapPrefabReferenceScope.World }] });
    }

    [Fact]
    public void Transfer_keys_include_source_instance_and_typed_local_child()
    {
        var document = MapPrefabDocumentRefusalTests.Empty();
        var transfer = new MapPrefabBindingTransfer("source,(one)", new(new(MapPrefabChildKind.Storey, "child,(two)"), "world", null, true));
        document.Prefabs = new(1, 1, [], [transfer, transfer]);
        var finding = MapPrefabDocumentRefusalTests.Refused(document, null, MapDocumentRefusal.IdentityDuplicate);
        Assert.Equal("source,(one)", finding.InstanceId);
        Assert.Equal(transfer.Binding.Child, finding.Child);
        Assert.Equal(transfer.Binding.Child.Kind, finding.ChildKind);
        document.Prefabs = new(1, 1, [], [transfer, transfer with { InstanceId = "other" }]);
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Fact]
    public void Attachment_keys_include_owner_kind_and_resource()
    {
        var document = MapPrefabDocumentRefusalTests.Empty();
        var attachment = new MapPrefabAttachmentTransfer("owner,(one)", "resource,(two)", new string('a', 64), MapPrefabAttachmentKind.Support, [], false);
        document.Prefabs = new(1, 1, [], []) { Attachments = [attachment, attachment] };
        var finding = MapPrefabDocumentRefusalTests.Refused(document, null, MapDocumentRefusal.IdentityDuplicate);
        Assert.Equal("owner,(one)", finding.PlacementId);
        Assert.Equal("resource,(two)", finding.ResourceId);
        Assert.Equal(MapPrefabAttachmentKind.Support, finding.AttachmentKind);
        document.Prefabs = new(1, 1, [], []) { Attachments = [attachment, attachment with { Kind = MapPrefabAttachmentKind.Light }] };
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Fact]
    public void Standalone_transform_keys_include_the_world_identity_namespace()
    {
        var document = MapPrefabDocumentRefusalTests.Empty();
        var pose = new MapStandaloneResourceTransform(MapPrefabChildKind.Placement, "owner", new(0, 0, 0, 0, 1), new string('a', 64));
        document.NativeResourceTransforms = [pose, pose];
        var finding = MapPrefabDocumentRefusalTests.Refused(document, null, MapDocumentRefusal.IdentityDuplicate);
        Assert.Equal("owner", finding.WorldId);
        Assert.Equal(MapPrefabChildKind.Placement, finding.ChildKind);
        document.NativeResourceTransforms = [pose, pose with { Kind = MapPrefabChildKind.Light }];
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }
}
