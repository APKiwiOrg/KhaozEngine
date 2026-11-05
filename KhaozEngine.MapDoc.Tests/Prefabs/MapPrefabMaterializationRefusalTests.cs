using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabMaterializationRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveResources_IdentifyMissingPaintTargetsAndSurfaceReferences(bool paint)
    {
        var document = PrefabFixtures.Create("uneven-unpainted").Document;
        var source = MapDocumentSurfaceSource.Capture(document);
        var transforms = new Dictionary<string, MapEffectiveTransform> { ["Surface:paint"] = new(0, 0, 0, 0, 1) };
        if (paint) document.Surfaces.Refs.Add(PrefabFixtures.Surface("paint", MapSurfaceRole.PaintOverride));
        var context = new MapEffectiveContext(document.Surfaces, transforms, new string('b', 64));
        MapEffectiveResources Resources(MapEffectiveContext value) => new(value, source, [], [], null, new(1, [], [], [], []),
            new Dictionary<string, MapExactPoint>(), new Dictionary<string, IReadOnlyList<MapExactPoint>>(), [], [], []);
        string before = MapPrefabJson.Digest("test/effective-resources", document);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (paint) _ = Resources(context);
            else _ = context.Surface("absent");
        });
        Assert.Equal(paint ? MapDocumentRefusal.PrefabPaintTargetRequired : MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(paint ? "paint" : "absent", error.SurfaceId);
        Assert.Equal(before, MapPrefabJson.Digest("test/effective-resources", document));
        if (paint)
        {
            int index = document.Surfaces.Refs.FindIndex(s => s.Id == "paint");
            document.Surfaces.Refs[index] = document.Surfaces.Refs[index] with { PaintTargetSurfaceId = "ground" };
            Assert.True(Resources(new(document.Surfaces, transforms, new string('b', 64))).PaintRegions.ContainsKey("ground"));
        }
        else Assert.Equal("ground", context.Surface("ground").Id);
    }

    [Theory]
    [InlineData("surface", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("anchor", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("record", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("slot", MapDocumentRefusal.ReferenceMissing)]
    public void EffectiveSupport_IdentifiesDuplicateDefinitionsAndMissingAnchorsOrSlots(string fault, MapDocumentRefusal reason)
    {
        var assets = PrefabFixtures.Create("attached").Assets;
        var resource = MapSupportResourceFile.Read(MapSupportResourceFile.Write(PrefabFixtures.DeckSupport()), "deck-support");
        var children = MapEffectiveSupport.Children("deck", "deck-support", resource).ToArray();
        var surface = children.OfType<MapPrefabSurfaceChild>().Single();
        var record = children.OfType<MapPrefabRecordChild>().Single();
        MapPrefabChild[] faulty = fault switch
        {
            "surface" => [surface, surface],
            "anchor" => [surface, record with { Anchor = record.Anchor with { SurfaceId = "absent" } }],
            _ => [surface, record, record],
        };
        var missingSlots = resource with { References = [] };
        string before = MapPrefabJson.Digest("test/effective-support", faulty);
        var transforms = new Dictionary<string, MapEffectiveTransform>();
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (fault == "slot") _ = MapEffectiveSupport.Children("deck", "deck-support", missingSlots).ToArray();
            else _ = MapEffectiveSupport.Install(assets, faulty, transforms);
        });
        Assert.Equal(reason, error.Reason);
        if (fault == "slot")
        {
            Assert.Equal("deck-support", error.ResourceId);
            Assert.Equal("deck", error.PlacementId);
            Assert.Equal("/patches/0/key/surfaceId", error.ReferencePath);
            Assert.Equal("top", error.EntityId);
        }
        else
        {
            Assert.Equal(fault == "surface" ? surface.Key : record.Key, error.Child);
            Assert.Equal(fault == "surface" ? surface.Surface.Id : record.Value.Id, error.EntityId);
            if (fault == "anchor") Assert.Equal(record.Anchor with { SurfaceId = "absent" }, error.Patch);
        }
        Assert.Equal(before, MapPrefabJson.Digest("test/effective-support", faulty));
        Assert.Empty(missingSlots.References);
        if (fault == "slot") Assert.Equal(2, MapEffectiveSupport.Children("deck", "deck-support", resource).Count());
        else Assert.Single(MapEffectiveSupport.Install(assets, children, transforms).AllRecords());
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("surface")]
    [InlineData("anchor")]
    [InlineData("record")]
    [InlineData("light")]
    [InlineData("digest")]
    public void ReusedAttachments_IdentifyMissingDescriptorsAnchorsAndChangedDigests(string fault)
    {
        var document = PrefabFixtures.Create("attachment").Document;
        var light = PrefabFixtures.GlowLight() with { Id = "captured-light", Owner = null };
        document.BuildingIntent = new(1, [], [], [light], []);
        var pose = new MapEffectiveTransform(0, 0, 0, 0, 1);
        document.NativeResourceTransforms = document.NativeResourceTransforms!.Append(
            new(MapPrefabChildKind.Light, light.Id, pose, new string('a', 64))).ToArray();
        var set = document.Prefabs!;
        document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, set.Instances, set.Transfers)
        { Attachments = set.Attachments.Append(new("unpacked-deck", "glow-light", new string('a', 64),
            MapPrefabAttachmentKind.Light, [new(new(MapPrefabChildKind.Light, "glow"), light.Id, null, pose, new string('a', 64))], false)).ToArray() };
        MapPrefabAttachmentDescriptors.Refresh(document);
        var original = document.Prefabs.Attachments.Single(a => a.Kind == (fault == "light" ? MapPrefabAttachmentKind.Light : MapPrefabAttachmentKind.Support));
        var child = original.Children.Single(c => c.LocalChild.Kind == (fault is "anchor" or "record" ? MapPrefabChildKind.Record
            : fault == "light" ? MapPrefabChildKind.Light : MapPrefabChildKind.Surface));
        var attachment = original with { Children = [fault == "anchor" ? child with { Anchor = null } : child] };
        var surfaces = document.Surfaces.Clone();
        var transforms = document.NativeResourceTransforms;
        var intent = document.BuildingIntent;
        switch (fault)
        {
            case "pin": document.NativeResourceTransforms = document.NativeResourceTransforms.Where(t => t.OwnerId != child.WorldId).ToArray(); break;
            case "surface": document.Surfaces.Refs.RemoveAll(s => s.Id == child.WorldId); break;
            case "record": document.Surfaces.Patches[child.Anchor!.Value].Records.Clear(); break;
            case "light": document.BuildingIntent = new(1, [], [], [], []); break;
            case "digest":
                document.NativeResourceTransforms = document.NativeResourceTransforms.Select(t => t.OwnerId == child.WorldId
                    ? t with { Transform = pose } : t).ToArray(); break;
        }
        string before = MapPrefabJson.Digest("test/reused-attachment", document);
        var error = Assert.Throws<MapDocumentException>(() => MapEffectiveAttachments.Reuse(document, attachment));
        Assert.Equal(fault == "digest" ? MapDocumentRefusal.DigestMismatch : MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(child.WorldId, error.EntityId);
        Assert.Equal(child.WorldId, error.WorldId);
        Assert.Equal(child.LocalChild, error.Child);
        Assert.Equal(child.LocalChild.Kind, error.ChildKind);
        Assert.Equal("unpacked-deck", error.PlacementId);
        Assert.Equal(original.ResourceId, error.ResourceId);
        Assert.Null(error.InstanceId);
        if (fault == "record") Assert.Equal(child.Anchor, error.Patch);
        if (fault == "anchor") Assert.Null(error.Patch);
        if (fault == "digest")
        {
            Assert.Equal("captured descriptor", error.DigestKind);
            Assert.Equal(child.SourceDigest, error.ExpectedHash);
            Assert.NotNull(error.ActualHash);
            Assert.NotEqual(error.ExpectedHash, error.ActualHash);
        }
        Assert.Equal(before, MapPrefabJson.Digest("test/reused-attachment", document));
        document.Surfaces = surfaces;
        document.NativeResourceTransforms = transforms;
        document.BuildingIntent = intent;
        var accepted = Assert.Single(MapEffectiveAttachments.Reuse(document, original with { Children = [child] }));
        Assert.Equal(child.WorldId, accepted.Key.Key);
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("replacement", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("add", MapDocumentRefusal.PrefabAddedChildExists)]
    public void Overrides_KeepTheTargetAndReplacementKeysDistinct(string fault, MapDocumentRefusal reason)
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("materialized"), "hut");
        var child = asset.Children.OfType<MapPrefabLightChild>().Single();
        var fresh = child with { Key = new(MapPrefabChildKind.Light, "fresh"), Value = child.Value with { Id = "fresh" } };
        MapPrefabOverride value = fault switch
        {
            "missing" => new MapPrefabTransformOverride(fresh.Key, child.LocalTransform),
            "replacement" => new MapPrefabReplaceOverride(child.Key, fresh),
            _ => new MapPrefabAddOverride(child.Key, child),
        };
        byte[] before = MapPrefabFile.Write(asset);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabMaterializationOverrides.Reconcile(asset, [value]));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("hut", error.AssetId);
        Assert.Equal(fault is "missing" or "replacement" ? fresh.Key : child.Key, error.Child);
        Assert.Null(error.InstanceId);
        if (fault == "replacement") Assert.Equal(child.Key, error.ExpectedChild);
        Assert.Equal(before, MapPrefabFile.Write(asset));
        MapPrefabOverride control = fault switch
        {
            "missing" => new MapPrefabTransformOverride(child.Key, child.LocalTransform),
            "replacement" => new MapPrefabReplaceOverride(child.Key, child),
            _ => new MapPrefabAddOverride(fresh.Key, fresh),
        };
        var accepted = MapPrefabMaterializationOverrides.Reconcile(asset, [control]);
        Assert.Contains(accepted.EffectiveAsset.Children, c => c.Key == control.Child);
    }

    [Theory]
    [InlineData("rows", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments")]
    [InlineData("row", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments[0]")]
    [InlineData("placement", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments[0].placement")]
    [InlineData("children", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments[0].children")]
    [InlineData("owner", MapDocumentRefusal.ReferenceMissing, "$.materializedAttachments[0].placement")]
    [InlineData("resource", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments[0].resourceId")]
    [InlineData("kind", MapDocumentRefusal.SchemaEnumUnsupported, "$.materializedAttachments[0].kind")]
    [InlineData("digest", MapDocumentRefusal.DigestMalformed, "$.materializedAttachments[0].resourceSha256")]
    [InlineData("captured", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments[0].children[0]")]
    [InlineData("child", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments[0].children[0].child")]
    [InlineData("placement-kind", MapDocumentRefusal.AuthoredReferenceKindInvalid, "$.materializedAttachments[0].placement")]
    [InlineData("placement-id", MapDocumentRefusal.SchemaValueRequired, "$.materializedAttachments[0].placement")]
    [InlineData("child-scope", MapDocumentRefusal.AuthoredValueMismatch, "$.materializedAttachments[0].children[0].child")]
    public void RequiredStructure_IdentifiesTheFirstFailedField(string fault, MapDocumentRefusal reason, string path)
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("materialized"), "hut");
        asset = PrefabFixtures.Materialized(asset.Children.OfType<MapPrefabPlacementChild>().ToArray());
        var row = asset.MaterializedAttachments[0];
        var child = row.Children[0];
        var changed = fault switch
        {
            "placement" => row with { Placement = null! },
            "children" => row with { Children = null! },
            "owner" => row with { Placement = row.Placement with { Id = "absent" } },
            "resource" => row with { ResourceId = " " },
            "kind" => row with { Kind = (MapPrefabAttachmentKind)123 },
            "digest" => row with { ResourceSha256 = "bad" },
            "captured" => row with { Children = [null!] },
            "child" => row with { Children = [child with { Child = null! }] },
            "placement-kind" => row with { Placement = row.Placement with { Kind = MapPrefabChildKind.Surface } },
            "placement-id" => row with { Placement = row.Placement with { Id = " " } },
            "child-scope" => row with { Children = [child with { Child = child.Child with { Scope = MapPrefabReferenceScope.World } }] },
            _ => row,
        };
        var rows = asset.MaterializedAttachments.ToArray();
        rows[0] = fault == "row" ? null! : changed;
        var faulty = asset with { MaterializedAttachments = fault == "rows" ? null! : rows };
        var before = rows.ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabLocalMaterializations.RequireStructure(faulty));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("hut", error.AssetId);
        Assert.Equal(path, error.FieldPath);
        Assert.Null(error.InstanceId);
        if (fault == "owner") Assert.Equal("absent", error.EntityId);
        if (fault == "placement-kind")
        {
            Assert.Equal(MapPrefabChildKind.Surface, error.ChildKind);
            Assert.Equal(MapPrefabChildKind.Placement, error.ExpectedChildKind);
        }
        if (fault == "kind")
        {
            Assert.Equal(typeof(MapPrefabAttachmentKind), error.EnumType);
            Assert.Equal(123, error.ActualInteger);
        }
        if (fault == "digest") Assert.Equal("bad", error.ActualHash);
        Assert.Equal(before, rows);
        MapPrefabLocalMaterializations.RequireStructure(asset);
    }

    [Theory]
    [InlineData("x", false, "x")]
    [InlineData("y", false, "y")]
    [InlineData("z", false, "z")]
    [InlineData("yaw", false, "yawRadians")]
    [InlineData("scale", false, "scale")]
    [InlineData("zero-scale", false, "scale")]
    [InlineData("x", true, "x")]
    [InlineData("zero-scale", true, "scale")]
    public void MathematicalFrames_IdentifyNonfiniteFieldsAndNonpositiveScale(string fault, bool captured, string field)
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("materialized"), "hut");
        asset = PrefabFixtures.Materialized(asset.Children.OfType<MapPrefabPlacementChild>().ToArray());
        var row = asset.MaterializedAttachments[0];
        var original = captured ? row.Children[0].OwnerLocalPose : row.PlacementFrame;
        var pose = fault switch
        {
            "x" => original with { X = double.NaN },
            "y" => original with { Y = double.PositiveInfinity },
            "z" => original with { Z = double.NegativeInfinity },
            "yaw" => original with { YawRadians = double.NaN },
            "scale" => original with { Scale = double.PositiveInfinity },
            _ => original with { Scale = 0 },
        };
        var changed = captured ? row with { Children = [row.Children[0] with { OwnerLocalPose = pose }] }
            : row with { PlacementFrame = pose };
        var rows = asset.MaterializedAttachments.ToArray();
        rows[0] = changed;
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabLocalMaterializations.RequireStructure(asset with { MaterializedAttachments = rows }));
        Assert.Equal(fault == "zero-scale" ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal("$.materializedAttachments[0]." + (captured ? "children[0].ownerLocalPose." : "placementFrame.") + field, error.FieldPath);
        Assert.Equal("deck", error.PlacementId);
        Assert.Equal(captured ? row.Children[0].Child.Id : "deck", error.EntityId);
        Assert.Equal(captured ? MapPrefabChildKind.Surface : MapPrefabChildKind.Placement, error.ChildKind);
        Assert.Null(error.InstanceId);
        if (fault == "zero-scale")
        {
            Assert.Equal(0, error.ActualNumber);
            Assert.Equal(0, error.MinimumNumber);
        }
        else Assert.Null(error.ActualNumber);
        Assert.Equal(changed, rows[0]);
        MapPrefabLocalMaterializations.RequireStructure(asset);
    }
}
