using System;
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
}
