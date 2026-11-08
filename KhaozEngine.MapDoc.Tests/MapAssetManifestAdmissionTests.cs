using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapAssetManifestAdmissionTests
{
    [Theory]
    [InlineData("id", "$.id", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("path", "$.path", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("short-digest", "$.sha256", MapDocumentRefusal.DigestMalformed)]
    [InlineData("uppercase-digest", "$.sha256", MapDocumentRefusal.DigestMalformed)]
    [InlineData("payload", "$.payloadVersion", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    public void Reference_admission_identifies_the_refused_field(string fault, string field, MapDocumentRefusal reason)
    {
        var good = new MapAssetRef("mesh", "mesh.bin", new string('a', 64), 1);
        var bad = fault switch
        {
            "id" => good with { Id = " " },
            "path" => good with { Path = " " },
            "short-digest" => good with { Sha256 = new string('a', 63) },
            "uppercase-digest" => good with { Sha256 = new string('A', 64) },
            _ => good with { PayloadVersion = 2 },
        };
        var error = Assert.Throws<MapDocumentException>(() => MapAssetManifestReader.ValidateReference(bad));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(bad.Id, error.ResourceId);
        if (fault == "payload")
        {
            Assert.Equal(2, error.ActualVersion);
            Assert.Equal(new[] { 1 }, error.SupportedVersions);
        }
        MapAssetManifestReader.ValidateReference(good);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resource_admission_distinguishes_missing_values_from_undefined_kinds(bool undefinedKind)
    {
        var reference = new MapAssetRef("mesh", "mesh.bin", new string('a', 64), 1);
        var resource = new MapResourceDoc { Reference = reference, Kind = MapResourceKind.Mesh };
        if (undefinedKind) resource.Kind = (MapResourceKind)999;
        else resource.Dependencies = null!;
        var error = Assert.Throws<MapDocumentException>(() => MapAssetManifestReader.ValidateResource(resource));
        Assert.Equal(undefinedKind ? MapDocumentRefusal.AssetKindUnsupported : MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(undefinedKind ? "$.kind" : "$.dependencies", error.FieldPath);
        Assert.Equal("mesh", error.ResourceId);
        if (undefinedKind) Assert.Equal((MapResourceKind)999, error.ActualResourceKind);
        resource.Kind = MapResourceKind.Mesh;
        resource.Dependencies = [];
        MapAssetManifestReader.ValidateResource(resource);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("resource")]
    public void Null_reference_or_resource_retains_the_exact_exception_contract(string subject)
    {
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (subject == "reference") MapAssetManifestReader.ValidateReference(null!);
            else MapAssetManifestReader.ValidateResource(null!);
        });
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$", error.FieldPath);
        var reference = new MapAssetRef("mesh", "mesh", new string('a', 64), 1);
        if (subject == "reference") MapAssetManifestReader.ValidateReference(reference);
        else MapAssetManifestReader.ValidateResource(new() { Reference = reference, Kind = MapResourceKind.Mesh });
    }

    [Theory]
    [InlineData("null-asset", "$.assets[0]", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("blank-source", "$.assets[0].source", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("null-list", "$.assets[0].supportResourceIds", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("blank-list-id", "$.assets[0].supportResourceIds[0]", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("units", "$.assets[0].sourceUnitsToMetres", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("units-nonfinite", "$.assets[0].sourceUnitsToMetres", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("bounds", "$.assets[0].renderBounds", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("reference-payload", "$.resources[0].reference.payloadVersion", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("resource-null", null, MapDocumentRefusal.SchemaMalformed)]
    [InlineData("json", null, MapDocumentRefusal.SchemaMalformed)]
    public void Manifest_admission_keeps_context_and_accepts_the_corrected_payload(string fault, string? field,
        MapDocumentRefusal reason)
    {
        var fixture = NativeAssetFixtures.Valid();
        byte[] good = fixture.Source.Read(fixture.Roots[0]).ToArray();
        var root = JsonNode.Parse(good)!;
        switch (fault)
        {
            case "null-asset": root["assets"]![0] = null; break;
            case "blank-source": root["assets"]![0]!["source"] = " "; break;
            case "null-list": root["assets"]![0]!["supportResourceIds"] = null; break;
            case "blank-list-id": root["assets"]![0]!["supportResourceIds"]![0] = " "; break;
            case "units": root["assets"]![0]!["sourceUnitsToMetres"] = 0; break;
            // A valid JSON number beyond binary32 range reaches the nonfinite scale branch.
            case "units-nonfinite": root["assets"]![0]!["sourceUnitsToMetres"] = JsonNode.Parse("1e100"); break;
            case "bounds": root["assets"]![0]!["renderBounds"]!["min"]!["x"] = 3; break;
            case "reference-payload": root["resources"]![0]!["reference"]!["payloadVersion"] = 2; break;
            case "resource-null": root["resources"]![0] = null; break;
        }
        byte[] bytes = fault == "json" ? Encoding.UTF8.GetBytes("{") : Encoding.UTF8.GetBytes(root.ToJsonString());
        byte[] before = bytes.ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapAssetManifestReader.Read(bytes, "root-a"));
        Assert.Equal(reason, error.Reason);
        if (field is not null) Assert.Equal(field, error.FieldPath);
        Assert.Equal(before, bytes);
        if (fault is "bounds" or "units" or "units-nonfinite" or "blank-source" or "null-list" or "blank-list-id") Assert.Equal("tree", error.AssetId);
        if (fault == "units")
        {
            Assert.Equal(0, error.ActualNumber);
            Assert.Equal(0, error.MinimumNumber);
        }
        if (fault == "units-nonfinite")
        {
            Assert.Null(error.ActualNumber);
            Assert.Null(error.MinimumNumber);
        }
        if (fault == "json") Assert.Equal("root-a", error.ResourceId);
        // Local bounds admit equality. Repair only the inverted axis at that exact boundary.
        if (fault == "bounds")
        {
            root["assets"]![0]!["renderBounds"]!["min"]!["x"] = 2;
            good = Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        Assert.Single(MapAssetManifestReader.Read(good, "root-a").Assets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Asset_resource_refusal_separates_an_absent_resource_from_a_wrong_kind(bool missing)
    {
        var asset = new MapAssetDoc { Id = "tree", MeshResourceId = "mesh" };
        var resource = new MapResourceDoc { Kind = MapResourceKind.Material };
        var resources = new Dictionary<string, MapResourceDoc>();
        if (!missing) resources.Add("mesh", resource);
        var error = Assert.Throws<MapDocumentException>(() => MapAssetManifestReader.ValidateAssetResources(asset, resources));
        Assert.Equal(missing ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.ReferenceKindMismatch, error.Reason);
        Assert.Equal("tree", error.AssetId);
        Assert.Equal("mesh", error.ResourceId);
        Assert.Equal(MapResourceKind.Mesh, error.ExpectedResourceKind);
        Assert.Equal(missing ? (MapResourceKind?)null : MapResourceKind.Material, error.ActualResourceKind);
        resource.Kind = MapResourceKind.Mesh;
        resources["mesh"] = resource;
        MapAssetManifestReader.ValidateAssetResources(asset, resources);
    }
}
