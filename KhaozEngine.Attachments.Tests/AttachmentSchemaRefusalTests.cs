using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class AttachmentSchemaRefusalTests
{
    [Theory]
    [InlineData(AttachmentAccessorRefusal.NormalizedNotBoolean)]
    [InlineData(AttachmentAccessorRefusal.CountNotPositive)]
    [InlineData(AttachmentAccessorRefusal.OffsetWithoutBufferView)]
    [InlineData(AttachmentAccessorRefusal.BufferViewOutsideBuffer)]
    [InlineData(AttachmentAccessorRefusal.ObjectRequired)]
    public void Accessor_shape_refusals_identify_the_schema_location(AttachmentAccessorRefusal reason)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        AttachmentGlb.ValidateSkinnedEmbedded(fixture.Build(), "control.glb");
        switch (reason)
        {
            case AttachmentAccessorRefusal.NormalizedNotBoolean: fixture.Accessors[2]!["normalized"] = "true"; break;
            case AttachmentAccessorRefusal.CountNotPositive: fixture.Accessors[2]!["count"] = 0; break;
            case AttachmentAccessorRefusal.OffsetWithoutBufferView:
                fixture.Accessors[2]!.AsObject().Remove("bufferView");
                fixture.Accessors[2]!["byteOffset"] = 4;
                break;
            case AttachmentAccessorRefusal.BufferViewOutsideBuffer: fixture.Views[2]!["byteLength"] = 353; break;
            case AttachmentAccessorRefusal.ObjectRequired: fixture.Primitives[0]!["attributes"] = new JsonArray(); break;
        }
        var error = Assert.Throws<AttachmentAccessorDataException>(() => AttachmentGlb.ValidateSkinnedEmbedded(fixture.Build(), "schema.glb"));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("schema.glb", error.SourceName);
        Assert.Equal(reason == AttachmentAccessorRefusal.BufferViewOutsideBuffer ? "$.bufferViews[2]"
            : reason == AttachmentAccessorRefusal.ObjectRequired ? "$.meshes[0].primitives[0].attributes"
            : reason == AttachmentAccessorRefusal.NormalizedNotBoolean ? "$.accessors[2].normalized"
            : reason == AttachmentAccessorRefusal.CountNotPositive ? "$.accessors[2].count" : "$.accessors[2].byteOffset", error.JsonPath);
        switch (reason)
        {
            case AttachmentAccessorRefusal.NormalizedNotBoolean: Assert.Equal(2, error.AccessorIndex); break;
            case AttachmentAccessorRefusal.CountNotPositive: Assert.Equal(0, error.Count); break;
            case AttachmentAccessorRefusal.OffsetWithoutBufferView: Assert.Equal(4, error.ByteOffset); break;
            case AttachmentAccessorRefusal.BufferViewOutsideBuffer:
                Assert.Equal(48, error.ByteOffset);
                Assert.Equal(353, error.ByteLength);
                Assert.Equal(352, error.BufferLength);
                break;
            case AttachmentAccessorRefusal.ObjectRequired: Assert.Equal("attributes", error.PropertyName); break;
        }
    }

    [Fact]
    public void Non_array_nodes_have_a_hierarchy_reason_before_graph_construction()
    {
        // Public callers check joint references first, so inject only the node-table shape at its guard.
        using var control = JsonDocument.Parse("[]");
        AttachmentGlbHierarchy.RejectNonArrayNodesForTests(control.RootElement);
        using var malformed = JsonDocument.Parse("{}");
        var error = Assert.Throws<AttachmentHierarchyDataException>(() => AttachmentGlbHierarchy.RejectNonArrayNodesForTests(malformed.RootElement));
        Assert.Equal(AttachmentHierarchyRefusal.NodesNotArray, error.Reason);
        Assert.Equal("$.nodes", error.JsonPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rig_identity_translates_native_JSON_failures_with_source_context(bool syntax)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        Assert.StartsWith("rig-v1:", AttachmentGlb.ComputeRigIdentity(fixture.Build(), "control.glb"));
        byte[] bytes = fixture.Build(json =>
        {
            if (syntax) return json[..^1];
            JsonNode root = JsonNode.Parse(json)!;
            root["buffers"]![0]!["byteLength"] = "invalid";
            return root.ToJsonString();
        });
        var error = Assert.Throws<AttachmentGlbContainerDataException>(() => AttachmentGlb.ComputeRigIdentity(bytes, "native.glb"));
        Assert.Equal(syntax ? AttachmentGlbContainerRefusal.JsonMalformed : AttachmentGlbContainerRefusal.ValueShapeInvalid, error.Reason);
        Assert.Equal("native.glb", error.SourceName);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("accessor")]
    [InlineData("hierarchy")]
    [InlineData("skinned")]
    [InlineData("rig")]
    public void Rig_identity_preserves_each_converted_family_with_source_context(string family)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        Assert.StartsWith("rig-v1:", AttachmentGlb.ComputeRigIdentity(fixture.Build(), "control.glb"));
        if (family == "accessor") fixture.Accessors[2]!["normalized"] = "true";
        if (family == "skinned") fixture.Nodes[1]!["name"] = "joint-0";
        if (family == "rig") fixture.MeshNode.Remove("mesh");
        if (family == "hierarchy") fixture.Nodes[1]!["children"] = new JsonArray(0);
        byte[] bytes = fixture.Build();
        if (family == "container") bytes[0] = 0;
        Action validate = () => AttachmentGlb.ComputeRigIdentity(bytes, "body.glb");
        switch (family)
        {
            case "container":
                var container = Assert.Throws<AttachmentGlbContainerDataException>(validate);
                Assert.Equal(AttachmentGlbContainerRefusal.HeaderInvalid, container.Reason);
                Assert.Equal("body.glb", container.SourceName);
                Assert.Equal(bytes.Length, container.ByteLength);
                Assert.Equal((uint)bytes.Length, container.DeclaredLength);
                Assert.Equal(2u, container.Version);
                Assert.Equal(container.Reason, Assert.IsType<AttachmentGlbContainerDataException>(container.InnerException).Reason);
                break;
            case "accessor":
                var accessor = Assert.Throws<AttachmentAccessorDataException>(validate);
                Assert.Equal(AttachmentAccessorRefusal.NormalizedNotBoolean, accessor.Reason);
                Assert.Equal("body.glb", accessor.SourceName);
                Assert.Equal(2, accessor.AccessorIndex);
                Assert.Equal("$.accessors[2].normalized", accessor.JsonPath);
                Assert.Equal(accessor.Reason, Assert.IsType<AttachmentAccessorDataException>(accessor.InnerException).Reason);
                break;
            case "skinned":
                var skinned = Assert.Throws<AttachmentSkinnedGeometryDataException>(validate);
                Assert.Equal(AttachmentSkinnedGeometryRefusal.JointNameDuplicate, skinned.Reason);
                Assert.Equal("body.glb", skinned.SourceName);
                Assert.Equal(1, skinned.JointOrdinal);
                Assert.Equal("joint-0", skinned.JointName);
                Assert.Equal(skinned.Reason, Assert.IsType<AttachmentSkinnedGeometryDataException>(skinned.InnerException).Reason);
                break;
            case "rig":
                var rig = Assert.Throws<RigIdentityDataException>(validate);
                Assert.Equal(RigIdentityRefusal.MeshNodeHasNoMesh, rig.Reason);
                Assert.Equal("body.glb", rig.SourceName);
                Assert.Equal(4, rig.NodeIndex);
                Assert.Equal(rig.Reason, Assert.IsType<RigIdentityDataException>(rig.InnerException).Reason);
                break;
            case "hierarchy":
                var hierarchy = Assert.Throws<AttachmentHierarchyDataException>(validate);
                Assert.Equal(AttachmentHierarchyRefusal.Cycle, hierarchy.Reason);
                Assert.Equal("body.glb", hierarchy.SourceName);
                Assert.Equal(0, hierarchy.NodeIndex);
                Assert.Equal("$.nodes[0]", hierarchy.JsonPath);
                Assert.Equal(hierarchy.Reason, Assert.IsType<AttachmentHierarchyDataException>(hierarchy.InnerException).Reason);
                break;
        }
    }

    [Theory]
    [InlineData("layer missing in format 3", AttachmentSetRefusal.LayerMissing)]
    [InlineData("layer is 'cloak'", AttachmentSetRefusal.LayerUnknown)]
    [InlineData("covers lists 'a' twice", AttachmentSetRefusal.CoveredRegionDuplicate)]
    [InlineData("rigid part without region", AttachmentSetRefusal.PartRegionMissing)]
    [InlineData("skinned part without regions", AttachmentSetRefusal.PartRegionMissing)]
    [InlineData("a run of zero triangles", AttachmentSetRefusal.PartRegionRunEmpty)]
    [InlineData("layer on a format 2 set", AttachmentSetRefusal.JsonPropertyUnknown)]
    [InlineData("region on a format 2 part", AttachmentSetRefusal.JsonPropertyUnknown)]
    [InlineData("regions on a rigid part", AttachmentSetRefusal.JsonPropertyUnknown)]
    [InlineData("region on a skinned part", AttachmentSetRefusal.JsonPropertyUnknown)]
    [InlineData("covers missing in format 3", AttachmentSetRefusal.JsonPropertyMissing)]
    [InlineData("a run that is not a pair", AttachmentSetRefusal.JsonValueShapeInvalid)]
    [InlineData("runs past Int32", AttachmentSetRefusal.RegionTriangleCountOverflow)]
    public void A_bad_format_3_set_is_refused_with_its_reason(string fault, AttachmentSetRefusal reason)
    {
        string control = AttachmentFixtures.RegionsSetJson();
        Assert.Equal(AttachmentLayer.Garment, AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(control), "control.json").Layer);
        string json = fault switch
        {
            "layer missing in format 3" => control.Replace("\"layer\":\"garment\",", "", StringComparison.Ordinal),
            "layer is 'cloak'" => control.Replace("\"layer\":\"garment\"", "\"layer\":\"cloak\"", StringComparison.Ordinal),
            "covers lists 'a' twice" => control.Replace("\"covers\":[\"a\",\"b\"]", "\"covers\":[\"a\",\"b\",\"a\"]", StringComparison.Ordinal),
            "rigid part without region" => control.Replace(",\"region\":\"a\"", "", StringComparison.Ordinal),
            "skinned part without regions" => control.Replace(",\"regions\":[[\"a\",3],[\"b\",1]]", "", StringComparison.Ordinal),
            "a run of zero triangles" => control.Replace("[\"b\",1]", "[\"b\",0]", StringComparison.Ordinal),
            "layer on a format 2 set" => control.Replace("\"formatVersion\":3", "\"formatVersion\":2", StringComparison.Ordinal),
            "region on a format 2 part" => control.Replace("\"formatVersion\":3,\"layer\":\"garment\",\"covers\":[\"a\",\"b\"]", "\"formatVersion\":2", StringComparison.Ordinal),
            "covers missing in format 3" => control.Replace("\"covers\":[\"a\",\"b\"],", "", StringComparison.Ordinal),
            "regions on a rigid part" => control.Replace(",\"region\":\"a\"", ",\"region\":\"a\",\"regions\":[[\"a\",1]]", StringComparison.Ordinal),
            "region on a skinned part" => control.Replace(",\"regions\":[[\"a\",3],[\"b\",1]]", ",\"regions\":[[\"a\",3],[\"b\",1]],\"region\":\"a\"", StringComparison.Ordinal),
            "a run that is not a pair" => control.Replace("[\"a\",3]", "[\"a\"]", StringComparison.Ordinal),
            "runs past Int32" => control.Replace("[\"a\",3]", "[\"a\",2147483647]", StringComparison.Ordinal),
            _ => throw new ArgumentException(fault),
        };
        Assert.NotEqual(control, json);
        var error = Assert.Throws<AttachmentSetDataException>(() => AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), "set.json"));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("set.json", error.SourceName);
        Assert.Equal(fault switch
        {
            "layer missing in format 3" or "layer on a format 2 set" or "covers missing in format 3" => "$",
            "layer is 'cloak'" => "$.layer",
            "rigid part without region" or "region on a format 2 part" or "regions on a rigid part" => "$.parts[0]",
            "skinned part without regions" or "region on a skinned part" => "$.parts[1]",
            "a run that is not a pair" => "$.parts[1].regions[0]",
            _ => null,
        }, error.JsonPath);
        Assert.Equal(fault switch
        {
            "layer missing in format 3" or "layer on a format 2 set" => "layer",
            "rigid part without region" or "region on a format 2 part" or "region on a skinned part" => "region",
            "skinned part without regions" or "regions on a rigid part" => "regions",
            "covers missing in format 3" => "covers",
            _ => null,
        }, error.PropertyName);
        Assert.Equal(fault == "layer is 'cloak'" ? "cloak" : null, error.LayerName);
        Assert.Equal(fault switch
        {
            "rigid part without region" => (uint?)1,
            "skinned part without regions" or "a run of zero triangles" or "a run that is not a pair" or "runs past Int32" => 2,
            _ => null,
        }, error.PartId);
        Assert.Equal(fault switch { "a run of zero triangles" or "runs past Int32" => (int?)1, "a run that is not a pair" => 0, _ => null }, error.RunIndex);
        Assert.Equal(fault == "covers lists 'a' twice" ? (int?)2 : null, error.CoverIndex);
        Assert.Equal(fault switch { "covers lists 'a' twice" => "a", "a run of zero triangles" or "runs past Int32" => (string?)"b", _ => null }, error.RegionName);
        Assert.Equal(fault == "runs past Int32" ? (long?)int.MaxValue + 1 : null, error.ActualTriangleCount);
        if (fault is "covers lists 'a' twice")
            Assert.Equal("covers", error.ParamName);
    }
}
