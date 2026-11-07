using System;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

internal static class RigIdentityRefusalAssertions
{
    internal static void Verify(string name, Action validate)
    {
        if (name is "duplicate-name" or "missing-name")
        {
            var skin = Assert.Throws<AttachmentSkinnedGeometryDataException>(validate);
            Assert.Equal(name == "duplicate-name" ? AttachmentSkinnedGeometryRefusal.JointNameDuplicate : AttachmentSkinnedGeometryRefusal.JointNameEmpty, skin.Reason);
            Assert.Equal("body.glb", skin.SourceName);
            Assert.Equal(1, skin.JointOrdinal);
            if (name == "duplicate-name") Assert.Equal("joint-0", skin.JointName);
            else Assert.Equal(1, skin.NodeIndex);
            return;
        }
        var reason = name switch
        {
            "no-buffer" => RigIdentityRefusal.EmbeddedBufferCountMismatch,
            "two-skins" => RigIdentityRefusal.SkinCountMismatch,
            "other-skin" => RigIdentityRefusal.MeshNodeSkinMismatch,
            "skin-no-mesh" => RigIdentityRefusal.MeshNodeHasNoMesh,
            "node-morph" or "mesh-morph" => RigIdentityRefusal.MorphWeightsUnsupported,
            "no-mesh" => RigIdentityRefusal.SkinnedGeometryMissing,
            "empty-primitives" => RigIdentityRefusal.PrimitivesEmpty,
            "morph" => RigIdentityRefusal.PrimitiveShapeOrMorphUnsupported,
            "compression" => RigIdentityRefusal.CompressionUnsupported,
            "bad-mode" => RigIdentityRefusal.PrimitiveModeInvalid,
            "malformed-influence" => RigIdentityRefusal.InfluenceAttributeMalformed,
            "no-first-set" => RigIdentityRefusal.FirstInfluenceSetMissing,
            "zero-weights" => RigIdentityRefusal.WeightsAllZero,
            "bad-index" => RigIdentityRefusal.VertexIndexOutsidePositionCount,
            "joint-range" or "weight-nan" => RigIdentityRefusal.JointIndexOrWeightInvalid,
            "attribute-count" => RigIdentityRefusal.VertexAccessorCountMismatch,
            "position-nan" => RigIdentityRefusal.FitValueNonFinite,
            "matrix-and-trs" => RigIdentityRefusal.MatrixAndTrsBothPresent,
            "bad-transform" => RigIdentityRefusal.RestTransformLengthMismatch,
            "rest-nonfinite" => RigIdentityRefusal.RestTransformComponentInvalid,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Missing rig refusal expectation."),
        };
        var error = Assert.Throws<RigIdentityDataException>(validate);
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal("body.glb", error.SourceName);
        switch (reason)
        {
            case RigIdentityRefusal.EmbeddedBufferCountMismatch: Assert.Equal(0, error.BufferCount); break;
            case RigIdentityRefusal.SkinCountMismatch: Assert.Equal(2, error.SkinCount); break;
            case RigIdentityRefusal.MeshNodeSkinMismatch:
                Assert.Equal(4, error.NodeIndex);
                Assert.Equal(1, error.SkinIndex);
                break;
            case RigIdentityRefusal.MeshNodeHasNoMesh: Assert.Equal(4, error.NodeIndex); break;
            case RigIdentityRefusal.MorphWeightsUnsupported:
                Assert.Equal(name == "node-morph" ? "$.nodes[4].weights" : "$.meshes[0].weights", error.JsonPath);
                Assert.Equal(name == "node-morph" ? 4 : 0, error.ElementIndex);
                break;
            case RigIdentityRefusal.PrimitivesEmpty:
                Assert.Equal(0, error.MeshIndex);
                Assert.Equal(0, error.PrimitiveCount);
                break;
            case RigIdentityRefusal.PrimitiveShapeOrMorphUnsupported:
                Assert.Equal(0, error.MeshIndex);
                Assert.Equal(0, error.PrimitiveIndex);
                break;
            case RigIdentityRefusal.CompressionUnsupported:
                Assert.Equal(0, error.PrimitiveIndex);
                Assert.Equal("KHR_draco_mesh_compression", error.ExtensionName);
                break;
            case RigIdentityRefusal.PrimitiveModeInvalid:
                Assert.Equal(0, error.PrimitiveIndex);
                Assert.Equal(7, error.Mode);
                break;
            case RigIdentityRefusal.InfluenceAttributeMalformed:
                Assert.Equal(0, error.PrimitiveIndex);
                Assert.Equal("JOINTS_01", error.AttributeName);
                break;
            case RigIdentityRefusal.FirstInfluenceSetMissing: Assert.Equal(0, error.PrimitiveIndex); break;
            case RigIdentityRefusal.WeightsAllZero:
                Assert.Equal(0, error.PrimitiveIndex);
                Assert.Equal(1, error.VertexIndex);
                break;
            case RigIdentityRefusal.VertexIndexOutsidePositionCount:
                Assert.Equal(0, error.PrimitiveIndex);
                Assert.Equal(3u, error.Index);
                Assert.Equal(3, error.VertexCount);
                break;
            case RigIdentityRefusal.JointIndexOrWeightInvalid:
                Assert.Equal(1, error.VertexIndex);
                Assert.Equal(0, error.Lane);
                Assert.Equal(name == "joint-range" ? 4f : 0f, error.JointIndex);
                Assert.Equal(name == "weight-nan" ? float.NaN : .25f, error.Weight);
                Assert.Equal(4, error.JointCount);
                break;
            case RigIdentityRefusal.VertexAccessorCountMismatch:
                Assert.Equal(2, error.ActualCount);
                Assert.Equal(3, error.PositionCount);
                break;
            case RigIdentityRefusal.FitValueNonFinite:
                Assert.Equal("$.meshes[0].primitives[0].attributes.POSITION", error.JsonPath);
                Assert.Equal(1, error.VertexIndex);
                Assert.Equal(0, error.ComponentIndex);
                break;
            case RigIdentityRefusal.MatrixAndTrsBothPresent: Assert.Equal(1, error.NodeIndex); break;
            case RigIdentityRefusal.RestTransformLengthMismatch:
                Assert.Equal(1, error.NodeIndex);
                Assert.Equal(3, error.ExpectedCount);
                Assert.Equal(2, error.ActualCount);
                break;
            case RigIdentityRefusal.RestTransformComponentInvalid:
                Assert.Equal(1, error.NodeIndex);
                Assert.Equal(1, error.ComponentIndex);
                break;
        }
    }
}
