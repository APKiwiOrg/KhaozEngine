using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabDocumentRefusalTests
{
    [Theory]
    [InlineData("missing", MapDocumentRefusal.SchemaValueRequired, "resolverIdentity")]
    [InlineData("payload", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "resolverIdentity.payloadVersion")]
    [InlineData("resolver", MapDocumentRefusal.ResolverContractMismatch, "resolverIdentity")]
    [InlineData("recipe", MapDocumentRefusal.ResolverContractMismatch, "supportRecipe")]
    public void Prefab_resolver_gate_retains_one_finding_for_the_actual_branch(string fault, MapDocumentRefusal reason, string path)
    {
        var document = Empty();
        document.ResolverIdentity = fault switch { "missing" => null, "payload" => new(2, 2), "resolver" => new(1, 1), _ => new(1, 2) };
        if (fault == "recipe") document.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        var finding = Refused(document, null, reason);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(2, finding.ExpectedResolverVersion);
        Assert.Equal(MapSupportRecipe.AuthoredBindingsV2, finding.ExpectedSupportRecipe);
        if (fault == "payload")
        {
            Assert.Equal(2, finding.ActualVersion);
            Assert.Equal(new[] { 1 }, finding.SupportedVersions);
        }
        document.ResolverIdentity = new(1, 2);
        document.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Theory]
    [InlineData("prefab", "$.prefabs.payloadVersion")]
    [InlineData("expansion", "$.prefabs.expansionVersion")]
    [InlineData("building", "$.buildingIntent.payloadVersion")]
    [InlineData("storey", "$.payloadVersion")]
    public void Payload_versions_identify_the_actual_unsupported_field(string section, string path)
    {
        var document = Empty();
        document.Prefabs = new(section == "prefab" ? 2 : 1, section == "expansion" ? 2 : 1, [], []);
        document.BuildingIntent = new(section == "building" ? 2 : 1, [], [], [], [])
        { Storeys = section == "storey" ? [new MapStoreyDoc(2, "level", "Level", null)] : [] };
        var finding = Refused(document, null, MapDocumentRefusal.SchemaPayloadVersionUnsupported);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(2, finding.ActualVersion);
        Assert.Equal(new[] { 1 }, finding.SupportedVersions);
        document.Prefabs = new(1, 1, [], []);
        document.BuildingIntent = new(1, [], [], [], []) { Storeys = section == "storey" ? [new MapStoreyDoc(1, "level", "Level", null)] : [] };
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_instance_pins_report_both_resource_kinds_and_digests(bool wrongKind)
    {
        var fixture = PrefabFixtures.Create("roomless");
        var document = fixture.Document;
        var original = Assert.Single(document.Prefabs!.Instances);
        var resource = fixture.Assets.GetResource(wrongKind ? "mesh" : original.ResourceId);
        var invalid = original with { ResourceId = resource.Reference.Id, ResourceSha256 = wrongKind ? resource.Reference.Sha256 : new string('f', 64) };
        document.Prefabs = new(1, 1, [invalid], []);
        long mark = document.NumericIdHighWaterMark;
        var finding = Refused(document, fixture.Assets, wrongKind ? MapDocumentRefusal.ReferenceKindMismatch : MapDocumentRefusal.PrefabAssetPinMismatch);
        Assert.Equal(original.Id, finding.InstanceId);
        Assert.Equal(invalid.ResourceId, finding.ResourceId);
        Assert.Equal(MapResourceKind.Prefab, finding.ExpectedResourceKind);
        Assert.Equal(wrongKind ? MapResourceKind.Mesh : MapResourceKind.Prefab, finding.ActualResourceKind);
        Assert.Equal(invalid.ResourceSha256, finding.ExpectedHash);
        Assert.Equal(resource.Reference.Sha256, finding.ActualHash);
        Assert.Same(invalid, Assert.Single(document.Prefabs.Instances));
        Assert.Equal(mark, document.NumericIdHighWaterMark);
        document.Prefabs = new(1, 1, [original], []);
        Assert.Empty(MapPrefabValidation.Validate(document, fixture.Assets));
    }

    [Theory]
    [InlineData("unpacked", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("transfer", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("parent", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("override", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("added", MapDocumentRefusal.PrefabAddedChildExists)]
    public void Instance_rules_identify_the_instance_and_child_without_allocating(string fault, MapDocumentRefusal reason)
    {
        var fixture = PrefabFixtures.Create(fault == "parent" ? "basic" : "roomless");
        var document = fixture.Document;
        var original = Assert.Single(document.Prefabs!.Instances);
        var child = fixture.Assets.GetPrefab(original.ResourceId).Children.OfType<MapPrefabPlacementChild>().First();
        var invalid = fault switch
        {
            "unpacked" => original with { Unpacked = true },
            "transfer" => original with { Removed = true },
            "parent" => original with { ParentSpace = null },
            "override" => original with { Overrides = [new MapPrefabTransformOverride(new(MapPrefabChildKind.Placement, "missing"), new(Vector3.Zero, 0, 1))] },
            _ => original with { Overrides = [new MapPrefabAddOverride(child.Key, child)] },
        };
        MapPrefabBindingTransfer[] transfers = fault == "transfer" ? [new(original.Id, original.Bindings.Entries[0])] : [];
        document.Prefabs = new(1, 1, [invalid], transfers);
        long mark = document.NumericIdHighWaterMark;
        var finding = Refused(document, fixture.Assets, reason);
        Assert.Equal(original.Id, finding.InstanceId);
        if (fault == "override") Assert.Equal(new MapPrefabChildKey(MapPrefabChildKind.Placement, "missing"), finding.Child);
        if (fault == "added") Assert.Equal(child.Key, finding.Child);
        Assert.Equal(mark, document.NumericIdHighWaterMark);
        Assert.Same(invalid, Assert.Single(document.Prefabs.Instances));
        var accepted = fault switch
        {
            "transfer" => invalid with { Unpacked = true },
            "override" => invalid with { Overrides = [new MapPrefabTransformOverride(child.Key, new(Vector3.Zero, 0, 1))] },
            "added" => invalid with { Overrides = [new MapPrefabReplaceOverride(child.Key, child)] },
            _ => original,
        };
        document.Prefabs = new(1, 1, [accepted], transfers);
        Assert.Empty(MapPrefabValidation.Validate(document, fixture.Assets));
    }

    [Theory]
    [InlineData(MapPrefabAttachmentKind.Support, MapPrefabChildKind.Light, MapPrefabChildKind.Surface)]
    [InlineData(MapPrefabAttachmentKind.Light, MapPrefabChildKind.Surface, MapPrefabChildKind.Light)]
    public void Attachment_child_kind_identifies_its_typed_attachment_namespace(MapPrefabAttachmentKind kind, MapPrefabChildKind invalid, MapPrefabChildKind accepted)
    {
        var document = Empty();
        var child = new MapPrefabAttachedChild(new(invalid, "child"), "world-child", null, new(0, 0, 0, 0, 1), new string('a', 64));
        var attachment = new MapPrefabAttachmentTransfer("owner", "resource", new string('a', 64), kind, [child], true);
        document.Prefabs = new(1, 1, [], []) { Attachments = [attachment] };
        var finding = Refused(document, null, MapDocumentRefusal.AuthoredReferenceKindInvalid);
        Assert.Equal(kind, finding.AttachmentKind);
        Assert.Equal(child.LocalChild, finding.Child);
        Assert.Equal("owner", finding.PlacementId);
        Assert.Equal("resource", finding.ResourceId);
        Assert.Equal("world-child", finding.WorldId);
        document.Prefabs = new(1, 1, [], []) { Attachments = [attachment with { Children = [child with { LocalChild = new(accepted, "child") }] }] };
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Fact]
    public void Storey_owner_must_name_an_existing_instance()
    {
        var document = Empty();
        document.BuildingIntent = new(1, [], [], [], []) { Storeys = [new(1, "level", "Level", "missing")] };
        var finding = Refused(document, null, MapDocumentRefusal.ReferenceMissing);
        Assert.Equal("missing", finding.InstanceId);
        Assert.Equal("level", finding.EntityId);
        document.BuildingIntent = new(1, [], [], [], []) { Storeys = [new(1, "level", "Level", null)] };
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Fact]
    public void Typed_direct_failures_become_findings_without_changing_the_string_projection()
    {
        var document = Empty();
        document.BuildingIntent = new(1, [], [], [new(1, "lamp", null, Vector3.Zero, Vector3.One, 0, 0, false)], []);
        var finding = Refused(document, null, MapDocumentRefusal.ValueOutsideRange);
        Assert.Equal("lamp", finding.EntityId);
        Assert.Equal("$.radius", finding.FieldPath);
        Assert.Equal(0, finding.ActualNumber);
        document.BuildingIntent = new(1, [], [], [new(1, "lamp", null, Vector3.Zero, Vector3.One, 0, BitConverter.Int32BitsToSingle(0x00800000), false)], []);
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("removed", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("ghost", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("source-dormant", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("source-missing", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("source-mismatch", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("source-foreign", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void Binding_inventory_and_liveness_refusals_keep_the_typed_owner(string fault, MapDocumentRefusal reason)
    {
        var fixture = PrefabFixtures.Create("roomless");
        var document = fixture.Document;
        var original = Assert.Single(document.Prefabs!.Instances);
        var binding = Assert.Single(original.Bindings.Entries);
        bool source = fault.StartsWith("source-", StringComparison.Ordinal);
        var extra = binding with { Child = new(MapPrefabChildKind.Placement, "ghost"), WorldId = "a/ghost", NumericId = null };
        MapPrefabBinding[] entries = fault switch
        {
            "missing" or "source-foreign" => [],
            "removed" or "source-dormant" => [binding with { Removed = true }],
            "ghost" => [binding, extra],
            _ => [binding],
        };
        MapPrefabBindingTransfer[] transfers = fault switch
        {
            "source-dormant" or "source-foreign" => [new(original.Id, binding)],
            "source-mismatch" => [new(original.Id, binding with { WorldId = "other" })],
            _ => [],
        };
        var invalid = original with { Removed = source, Unpacked = source, Bindings = new(entries) };
        document.Prefabs = new(1, 1, [invalid], transfers);
        long mark = document.NumericIdHighWaterMark;
        var finding = Refused(document, fixture.Assets, reason);
        Assert.Equal(original.Id, finding.InstanceId);
        Assert.Equal(fault == "ghost" ? extra.Child : binding.Child, finding.Child);
        if (fault == "source-dormant") { Assert.Equal(1, finding.ActualCount); Assert.Equal(0, finding.ExpectedCount); }
        if (fault is "source-missing" or "source-mismatch")
        {
            Assert.Equal(fault == "source-missing" ? 0 : 1, finding.ActualCount);
            Assert.Equal(1, finding.ExpectedCount);
        }
        Assert.Same(invalid, Assert.Single(document.Prefabs.Instances));
        Assert.Equal(mark, document.NumericIdHighWaterMark);
        var accepted = invalid with { Bindings = new(fault == "ghost" ? [binding, extra with { Removed = true }] : [binding]) };
        MapPrefabBindingTransfer[] acceptedTransfers = source && fault != "source-dormant" ? [new(original.Id, binding)] : [];
        if (fault == "source-dormant") accepted = invalid;
        document.Prefabs = new(1, 1, [accepted], acceptedTransfers);
        Assert.Empty(MapPrefabValidation.Validate(document, fixture.Assets));
    }

    [Theory]
    [InlineData("digest-length", MapDocumentRefusal.DigestMalformed)]
    [InlineData("digest-case", MapDocumentRefusal.DigestMalformed)]
    [InlineData("x", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("y", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("z", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("yaw", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("scale-nonfinite", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("scale-zero", MapDocumentRefusal.ValueOutsideRange)]
    public void Standalone_transform_scalars_keep_the_first_failed_component(string fault, MapDocumentRefusal reason)
    {
        var document = Empty();
        var pose = new MapEffectiveTransform(0, 0, 0, 0, 1);
        var invalid = fault switch
        {
            "x" => pose with { X = double.NaN },
            "y" => pose with { Y = double.NaN },
            "z" => pose with { Z = double.NaN },
            "yaw" => pose with { YawRadians = double.NaN },
            "scale-nonfinite" => pose with { Scale = double.PositiveInfinity },
            "scale-zero" => pose with { Scale = 0 },
            _ => pose,
        };
        string digest = fault == "digest-length" ? new string('a', 63) : new string(fault == "digest-case" ? 'A' : 'a', 64);
        document.NativeResourceTransforms = [new(MapPrefabChildKind.Placement, "owner", invalid, digest)];
        var finding = Refused(document, null, reason);
        if (reason == MapDocumentRefusal.DigestMalformed) Assert.Equal(digest, finding.ActualHash);
        else
        {
            Assert.Equal(fault switch { "yaw" => "$.transform.yawRadians", "scale-nonfinite" or "scale-zero" => "$.transform.scale", _ => "$.transform." + fault }, finding.FieldPath);
            if (fault == "scale-zero") { Assert.Equal(0, finding.ActualNumber); Assert.Equal(0, finding.MinimumNumber); }
            else Assert.False(double.IsFinite(finding.ActualNumber!.Value));
        }
        // The smallest normal binary64 scale avoids subnormal-dependent controls at this strict-positive boundary.
        var accepted = fault.StartsWith("scale-", StringComparison.Ordinal) ? pose with { Scale = BitConverter.Int64BitsToDouble(0x0010000000000000) } : pose;
        document.NativeResourceTransforms = [new(MapPrefabChildKind.Placement, "owner", accepted, new string('a', 64))];
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    internal static MapDocument Empty() => new()
    {
        ResolverIdentity = new(1, 2),
        SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
        Prefabs = new(1, 1, [], []),
    };

    internal static MapDocumentFinding Refused(MapDocument document, MapAssetClosure? assets, MapDocumentRefusal reason)
    {
        var findings = new List<MapDocumentFinding>();
        var errors = MapPrefabValidation.Validate(document, assets, findings);
        Assert.Single(errors);
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(finding.Message, errors[0]);
        Assert.Equal(errors, MapPrefabValidation.Validate(document, assets));
        return finding;
    }
}
