using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabValidationRefusalTests
{
    [Theory]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.payloadVersion")]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "$.id")]
    public void Asset_header_identifies_the_refused_field(string fault, MapDocumentRefusal reason, string path)
    {
        var valid = new MapPrefabAsset(1, "asset", []);
        var invalid = fault == "version" ? valid with { PayloadVersion = 2 } : valid with { Id = "" };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(invalid));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.FieldPath);
        if (fault == "version")
        {
            Assert.Equal(2, error.ActualVersion);
            Assert.Equal(new[] { 1 }, error.SupportedVersions);
        }
        Assert.Empty(invalid.Children);
        MapPrefabValidation.RequireAsset(valid);
    }

    [Theory]
    [InlineData("payload-id", MapDocumentRefusal.PrefabPayloadIdMismatch)]
    [InlineData("payload-kind", MapDocumentRefusal.PrefabPayloadIdMismatch)]
    [InlineData("numeric-id", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("duplicate-transform", MapDocumentRefusal.PrefabPlacementTransformDuplicated)]
    [InlineData("tags", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("null-tag", MapDocumentRefusal.SchemaValueRequired)]
    public void Placement_child_refusals_retain_the_local_key(string fault, MapDocumentRefusal reason)
    {
        var valid = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "child"),
            new MapPlacement { Id = "child", Kind = "mesh", AssetId = "leaf", Scale = 1 });
        var payload = new MapPlacement { Id = "child", Kind = "mesh", AssetId = "leaf", Scale = 1 };
        var invalid = valid with { Value = payload };
        switch (fault)
        {
            case "payload-id": payload.Id = "other"; break;
            case "payload-kind": invalid = invalid with { Key = new(MapPrefabChildKind.Storey, "child") }; break;
            case "numeric-id": payload.NumericId = 1; break;
            case "duplicate-transform": invalid = invalid with { LocalTransform = new(Vector3.One, 0, 1) }; break;
            case "tags": payload.Tags = null!; break;
            default: payload.Tags = [null!]; break;
        }
        var asset = new MapPrefabAsset(1, "asset", [invalid]);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(asset));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(invalid.Key, error.Child);
        if (fault.StartsWith("payload-", StringComparison.Ordinal))
        {
            Assert.Equal("$.key", error.FieldPath);
            Assert.Equal("Placement", error.SubjectKind);
            Assert.Equal(MapPrefabChildKind.Placement, error.ChildKind);
            Assert.Equal(invalid.Value.Id, error.EntityId);
        }
        if (fault == "numeric-id") Assert.Equal(1, error.NumericId);
        Assert.Same(invalid, Assert.Single(asset.Children));
        MapPrefabValidation.RequireAsset(asset with { Children = [valid] });
    }

    [Theory]
    [InlineData("position-x", MapDocumentRefusal.ValueNonfinite, "$.localTransform.position.x")]
    [InlineData("position-y", MapDocumentRefusal.ValueNonfinite, "$.localTransform.position.y")]
    [InlineData("position-z", MapDocumentRefusal.ValueNonfinite, "$.localTransform.position.z")]
    [InlineData("yaw", MapDocumentRefusal.ValueNonfinite, "$.localTransform.yawRadians")]
    [InlineData("scale-infinite", MapDocumentRefusal.ValueNonfinite, "$.localTransform.scale")]
    [InlineData("scale-zero", MapDocumentRefusal.ValueOutsideRange, "$.localTransform.scale")]
    [InlineData("key-kind", MapDocumentRefusal.SchemaEnumUnsupported, "$.key.kind")]
    [InlineData("key-text", MapDocumentRefusal.SchemaValueRequired, "$.key.key")]
    public void Child_pose_and_key_identify_the_actual_failed_branch(string fault, MapDocumentRefusal reason, string path)
    {
        var valid = new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "level"), "Level");
        MapTransform pose = fault switch
        {
            "position-x" => new(new(float.NaN, 0, 0), 0, 1),
            "position-y" => new(new(0, float.NaN, 0), 0, 1),
            "position-z" => new(new(0, 0, float.NaN), 0, 1),
            "yaw" => new(Vector3.Zero, float.NaN, 1),
            "scale-infinite" => new(Vector3.Zero, 0, float.PositiveInfinity),
            "scale-zero" => new(Vector3.Zero, 0, 0),
            _ => valid.LocalTransform,
        };
        var invalid = valid with
        {
            LocalTransform = pose,
            Key = fault switch
            {
                "key-kind" => new((MapPrefabChildKind)999, "level"),
                "key-text" => new(MapPrefabChildKind.Storey, ""),
                _ => valid.Key,
            }
        };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(new(1, "asset", [invalid])));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.FieldPath);
        if (fault == "key-kind")
        {
            Assert.Equal(typeof(MapPrefabChildKind), error.EnumType);
            Assert.Equal(999, error.ActualInteger);
        }
        else if (reason == MapDocumentRefusal.ValueNonfinite) Assert.False(double.IsFinite(error.ActualNumber!.Value));
        else if (fault == "scale-zero")
        {
            Assert.Equal(0, error.ActualNumber);
            Assert.Equal(0, error.MinimumNumber);
        }
        // Scale is strictly positive. The smallest normal float avoids platform-dependent subnormal handling.
        var accepted = fault.StartsWith("scale-", StringComparison.Ordinal)
            ? valid with { LocalTransform = new(Vector3.Zero, 0, BitConverter.Int32BitsToSingle(0x00800000)) } : valid;
        MapPrefabValidation.RequireAsset(new(1, "asset", [accepted]));
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData(true, MapDocumentRefusal.SchemaArrayLengthMismatch)]
    public void Surface_geometry_refuses_the_actual_owner_or_record_rule(bool embeddedRecord, MapDocumentRefusal reason)
    {
        var patch = PrefabFixtures.Patch("floor", 1, 1, 0);
        var child = PrefabFixtures.SurfaceChild("floor", MapSurfaceRole.SupportFloor, patch);
        if (embeddedRecord) patch.Records.Add(new MapSpaceDoc("room", MapSpaceKind.Room, null, null, [], [], [], []));
        else patch.Key = new("other", 0, 0);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(new(1, "asset", [child])));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(child.Key, error.Child);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal("floor", error.SurfaceId);
        if (embeddedRecord)
        {
            Assert.Equal(1, error.ActualCount);
            Assert.Equal(0, error.ExpectedCount);
            Assert.Single(patch.Records);
            patch.Records.Clear();
        }
        else
        {
            Assert.Equal("other", patch.Key.SurfaceId);
            patch.Key = new("floor", 0, 0);
        }
        MapPrefabValidation.RequireAsset(new(1, "asset", [child]));
    }

    [Theory]
    [InlineData("roof", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("roof-kind", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("light", MapDocumentRefusal.ReferenceMissing)]
    public void Local_annotation_targets_identify_the_missing_or_wrong_kind_reference(string fault, MapDocumentRefusal reason)
    {
        var placement = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "owner"),
            new MapPlacement { Id = "owner", Kind = "mesh", AssetId = "leaf", Scale = 1 });
        var reference = new MapPrefabReference(MapPrefabReferenceScope.Local,
            fault == "roof-kind" ? MapPrefabChildKind.Record : MapPrefabChildKind.Placement, fault == "roof-kind" ? "owner" : "missing");
        var asset = new MapPrefabAsset(1, "asset", [placement]);
        if (fault == "light") asset = asset with
        {
            Children = [placement, new MapPrefabLightChild(new(MapPrefabChildKind.Light, "lamp"),
            new(1, "lamp", reference, Vector3.Zero, Vector3.One, 0, 1, false))]
        };
        else asset = asset with { Roofs = [reference] };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(asset));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(reference.Id, error.EntityId);
        Assert.Equal(reference.Kind, error.ChildKind);
        var accepted = reference with { Id = "owner", Kind = MapPrefabChildKind.Placement };
        if (fault == "light") asset = asset with
        {
            Children = [placement,
            ((MapPrefabLightChild)asset.Children[1]) with { Value = ((MapPrefabLightChild)asset.Children[1]).Value with { Owner = accepted } }]
        };
        else asset = asset with { Roofs = [accepted] };
        MapPrefabValidation.RequireAsset(asset);
    }
    [Theory]
    [InlineData(false, MapDocumentRefusal.PrefabLocalAnchorInvalid)]
    [InlineData(true, MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void Record_anchor_and_prism_ownership_stay_local_until_expansion(bool prismOwnership, MapDocumentRefusal reason)
    {
        var valid = MapPrefabFile.Read(PrefabFixtures.AssetBytes("pool"), "hut");
        var record = valid.Children.OfType<MapPrefabRecordChild>().First(c => prismOwnership ? c.Value is MapPrismDomain : c.Value is MapSpaceDoc);
        var invalid = prismOwnership
            ? record with { Value = ((MapPrismDomain)record.Value) with { InstanceId = "owner" } }
            : record with
            {
                References = record.References.Select(s => s.Path == "/anchor/surfaceId"
                ? s with { Target = s.Target with { Scope = MapPrefabReferenceScope.World } } : s).ToArray()
            };
        var asset = valid with { Children = valid.Children.Select(c => c == record ? invalid : c).ToArray() };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(asset));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(record.Key, error.Child);
        Assert.Equal(record.Value.Id, error.RecordId);
        Assert.Equal(prismOwnership ? "$.value.instanceId" : "$.anchor.surfaceId", error.FieldPath);
        if (prismOwnership) Assert.Equal("owner", error.InstanceId);
        Assert.Contains(invalid, asset.Children);
        MapPrefabValidation.RequireAsset(valid);
    }

    [Theory]
    [InlineData("room", MapDocumentRefusal.AuthoredReferenceKindInvalid, "$.rooms")]
    [InlineData("group", MapDocumentRefusal.AuthoredReferenceKindInvalid, "$.groups")]
    [InlineData("group-room", MapDocumentRefusal.AuthoredReferenceKindInvalid, "$.rooms")]
    [InlineData("height", MapDocumentRefusal.ValueNonfinite, "$.aboveY")]
    public void Roof_annotations_distinguish_reference_roles_from_nonfinite_height(string fault, MapDocumentRefusal reason, string path)
    {
        var roof = PrefabFixtures.World(MapPrefabChildKind.Placement, "roof");
        var room = PrefabFixtures.World(MapPrefabChildKind.Record, "room");
        var group = PrefabFixtures.World(MapPrefabChildKind.RoofGroup, "group");
        MapPrefabChild valid = fault == "group-room"
            ? new MapPrefabRoofGroupChild(new(MapPrefabChildKind.RoofGroup, "group"), new("group", [room]))
            : new MapPrefabRoofChild(new(MapPrefabChildKind.RoofLink, "link"), new("link", roof, [room], [group], 0));
        MapPrefabChild invalid = valid switch
        {
            MapPrefabRoofGroupChild g => g with { Value = g.Value with { Rooms = [room with { Kind = MapPrefabChildKind.Placement }] } },
            MapPrefabRoofChild r => r with
            {
                Value = fault switch
                {
                    "room" => r.Value with { Rooms = [room with { Kind = MapPrefabChildKind.Placement }] },
                    "group" => r.Value with { Groups = [group with { Kind = MapPrefabChildKind.Placement }] },
                    _ => r.Value with { AboveY = float.NaN },
                }
            },
            _ => throw new InvalidOperationException(),
        };
        var asset = new MapPrefabAsset(1, "asset", [invalid]);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(asset));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.FieldPath);
        if (fault == "height") Assert.True(double.IsNaN(error.ActualNumber!.Value));
        else
        {
            Assert.Equal(fault == "group" ? "group" : "room", error.EntityId);
            Assert.Equal(MapPrefabChildKind.Placement, error.ChildKind);
        }
        Assert.Same(invalid, Assert.Single(asset.Children));
        MapPrefabValidation.RequireAsset(asset with { Children = [valid] });
    }

}
