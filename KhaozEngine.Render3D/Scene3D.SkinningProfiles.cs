using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    static SkinnedGltfMesh RetainSkinningSource(SkinnedGltfMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        // Every profile retains one upload-time snapshot for GPU/CPU execution, bounds and history identity.
        // Copy the retained rig too, so later caller edits cannot change pose diagnostics.
        Skeleton? skeleton = mesh.Skeleton;
        if (skeleton is not null)
            skeleton = new Skeleton((int[])skeleton.ParentIndices.Clone(), (JointPose[])skeleton.RestLocal.Clone(),
                (int[])skeleton.NodeLogicalIndex.Clone(), (int[])skeleton.JointToNode.Clone(), skeleton.NodeNames);
        return new SkinnedGltfMesh((SkinnedVertex[])mesh.Vertices.Clone(), (uint[])mesh.Indices32.Clone(),
            (Matrix4x4[])mesh.InverseBind.Clone(), (Matrix4x4[])mesh.RestPose.Clone(), skeleton, mesh.Profile);
    }

    PreparedSkinningPose PrepareSkinning(SkinnedMeshEntry entry, ReadOnlySpan<Matrix4x4> boneMatrices,
        string consumer)
    {
        try
        {
            // Stage 3 entry gate. No public draw may reach an LBS consumer with a DQ payload.
            return _preparedSkinning.Prepare(entry.Source, boneMatrices, entry.ValidationContext,
                entry.DqBounds, consumer);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException(error.Message, nameof(boneMatrices), error);
        }
    }
}
