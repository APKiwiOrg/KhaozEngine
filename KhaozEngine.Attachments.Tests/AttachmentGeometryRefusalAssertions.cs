using System;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

internal static class AttachmentGeometryRefusalAssertions
{
    internal static void Rigid(string name, Action validate, string source)
    {
        var reason = name switch
        {
            "skins" => AttachmentRigidGeometryRefusal.SkinPresent,
            "animation" => AttachmentRigidGeometryRefusal.AnimationPresent,
            "node-skin" or "node-weights" => AttachmentRigidGeometryRefusal.NodeDeformationPresent,
            "mesh-weights" => AttachmentRigidGeometryRefusal.MeshMorphWeightsPresent,
            "morph-target" => AttachmentRigidGeometryRefusal.PrimitiveMorphTargetsPresent,
            "joints-attribute" or "weights-attribute" => AttachmentRigidGeometryRefusal.SkinAttributePresent,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Missing rigid refusal expectation."),
        };
        var error = Assert.Throws<AttachmentRigidGeometryDataException>(validate);
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(source, error.SourceName);
        if (reason == AttachmentRigidGeometryRefusal.NodeDeformationPresent)
        {
            Assert.Equal(0, error.NodeIndex);
            Assert.Equal(name == "node-skin", error.HasSkin);
            Assert.Equal(name == "node-weights", error.HasWeights);
        }
        if (reason is AttachmentRigidGeometryRefusal.MeshMorphWeightsPresent
            or AttachmentRigidGeometryRefusal.PrimitiveMorphTargetsPresent or AttachmentRigidGeometryRefusal.SkinAttributePresent)
            Assert.Equal(0, error.MeshIndex);
        if (reason is AttachmentRigidGeometryRefusal.PrimitiveMorphTargetsPresent or AttachmentRigidGeometryRefusal.SkinAttributePresent)
            Assert.Equal(0, error.PrimitiveIndex);
        if (reason == AttachmentRigidGeometryRefusal.SkinAttributePresent)
            Assert.Equal(name == "joints-attribute" ? "JOINTS_0" : "WEIGHTS_0", error.AttributeName);
    }

    internal static void Skinned(string name, Action validate)
    {
        var reason = name switch
        {
            "missing-buffer" => AttachmentSkinnedGeometryRefusal.EmbeddedBufferCountMismatch,
            "zero-skins" or "two-skins" => AttachmentSkinnedGeometryRefusal.SkinCountMismatch,
            "animation" => AttachmentSkinnedGeometryRefusal.AnimationPresent,
            "node-weights" or "mesh-weights" => AttachmentSkinnedGeometryRefusal.MorphWeightsPresent,
            "missing-skin" or "different-skin" or "later-node" => AttachmentSkinnedGeometryRefusal.MeshNodeSkinMismatch,
            "129-joints" or "empty-joints" => AttachmentSkinnedGeometryRefusal.JointCountOutsideRange,
            "repeated-index" => AttachmentSkinnedGeometryRefusal.JointNodeDuplicate,
            "whitespace-name" or "empty-name" or "missing-name" => AttachmentSkinnedGeometryRefusal.JointNameEmpty,
            "repeated-name" => AttachmentSkinnedGeometryRefusal.JointNameDuplicate,
            "bind-count" => AttachmentSkinnedGeometryRefusal.InverseBindCountMismatch,
            "bind-nonfinite" => AttachmentSkinnedGeometryRefusal.InverseBindNonFinite,
            "targets" => AttachmentSkinnedGeometryRefusal.MorphTargetsPresent,
            "joints-1" or "weights-1" or "joints-2" or "weights-2" => AttachmentSkinnedGeometryRefusal.ExtraInfluenceAttribute,
            "joint-count" or "weight-count" => AttachmentSkinnedGeometryRefusal.VertexAttributeCountMismatch,
            "joint-at-count" or "joint-at-count-zero-lane" or "sparse-joint-range" => AttachmentSkinnedGeometryRefusal.JointIndexOutsideSkin,
            "negative-weight" or "nan-weight" or "infinite-weight" => AttachmentSkinnedGeometryRefusal.WeightInvalid,
            "zero-weights" or "sparse-zero" => AttachmentSkinnedGeometryRefusal.WeightsAllZero,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Missing skinned refusal expectation."),
        };
        var error = Assert.Throws<AttachmentSkinnedGeometryDataException>(validate);
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal("bad.glb", error.SourceName);
        switch (reason)
        {
            case AttachmentSkinnedGeometryRefusal.EmbeddedBufferCountMismatch: Assert.Equal(0, error.BufferCount); break;
            case AttachmentSkinnedGeometryRefusal.SkinCountMismatch: Assert.Equal(name == "zero-skins" ? 0 : 2, error.SkinCount); break;
            case AttachmentSkinnedGeometryRefusal.MorphWeightsPresent:
                Assert.Equal(name == "node-weights" ? "$.nodes[4].weights" : "$.meshes[0].weights", error.JsonPath);
                Assert.Equal(name == "node-weights" ? 4 : 0, error.ElementIndex);
                break;
            case AttachmentSkinnedGeometryRefusal.MeshNodeSkinMismatch:
                Assert.Equal(name == "later-node" ? 5 : 4, error.NodeIndex);
                Assert.Equal(name == "different-skin" ? 1 : (int?)null, error.SkinIndex);
                break;
            case AttachmentSkinnedGeometryRefusal.JointCountOutsideRange:
                Assert.Equal("$.skins[0].joints", error.JsonPath);
                Assert.Equal(name == "129-joints" ? 129 : 0, error.JointCount);
                Assert.Equal(128, error.MaximumJointCount);
                break;
            case AttachmentSkinnedGeometryRefusal.JointNodeDuplicate:
                Assert.Equal(1, error.JointOrdinal);
                Assert.Equal(0, error.NodeIndex);
                break;
            case AttachmentSkinnedGeometryRefusal.JointNameEmpty:
                Assert.Equal(1, error.JointOrdinal);
                Assert.Equal(1, error.NodeIndex);
                break;
            case AttachmentSkinnedGeometryRefusal.JointNameDuplicate:
                Assert.Equal(1, error.JointOrdinal);
                Assert.Equal("joint-0", error.JointName);
                break;
            case AttachmentSkinnedGeometryRefusal.InverseBindCountMismatch:
                Assert.Equal(3, error.BindCount);
                Assert.Equal(4, error.JointCount);
                break;
            case AttachmentSkinnedGeometryRefusal.InverseBindNonFinite:
                Assert.Equal(1, error.JointOrdinal);
                Assert.Equal(2, error.ComponentIndex);
                break;
            case AttachmentSkinnedGeometryRefusal.MorphTargetsPresent: Assert.Equal("$.meshes[0].primitives[0].targets", error.JsonPath); break;
            case AttachmentSkinnedGeometryRefusal.ExtraInfluenceAttribute:
                string attribute = name.Replace("joints-", "JOINTS_", StringComparison.Ordinal).Replace("weights-", "WEIGHTS_", StringComparison.Ordinal);
                Assert.Equal(attribute, error.AttributeName);
                Assert.Equal("$.meshes[0].primitives[0].attributes." + attribute, error.JsonPath);
                break;
            case AttachmentSkinnedGeometryRefusal.VertexAttributeCountMismatch:
                Assert.Equal("$.meshes[0].primitives[0]", error.JsonPath);
                Assert.Equal(3, error.PositionCount);
                Assert.Equal(name == "joint-count" ? 2 : 3, error.JointCount);
                Assert.Equal(name == "weight-count" ? 2 : 3, error.WeightCount);
                break;
            case AttachmentSkinnedGeometryRefusal.JointIndexOutsideSkin:
                Assert.Equal("$.meshes[0].primitives[0].attributes.JOINTS_0", error.JsonPath);
                Assert.Equal(1, error.VertexIndex);
                Assert.Equal(3, error.Lane);
                Assert.Equal(4f, error.Index);
                Assert.Equal(4, error.JointCount);
                break;
            case AttachmentSkinnedGeometryRefusal.WeightInvalid:
                Assert.Equal("$.meshes[0].primitives[0].attributes.WEIGHTS_0", error.JsonPath);
                Assert.Equal(1, error.VertexIndex);
                Assert.Equal(0, error.Lane);
                Assert.Equal(name == "negative-weight" ? -.25f : name == "nan-weight" ? float.NaN : float.PositiveInfinity, error.Weight);
                break;
            case AttachmentSkinnedGeometryRefusal.WeightsAllZero:
                Assert.Equal("$.meshes[0].primitives[0].attributes.WEIGHTS_0", error.JsonPath);
                Assert.Equal(1, error.VertexIndex);
                break;
        }
    }
}
