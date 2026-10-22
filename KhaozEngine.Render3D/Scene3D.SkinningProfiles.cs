using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    static SkinnedGltfMesh RetainSkinningSource(SkinnedGltfMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Profile.Method == SkinningMethod.LinearBlend) return mesh;
        // Upload and CPU execution use the same admitted source words, even if the caller edits its asset.
        return new SkinnedGltfMesh((SkinnedVertex[])mesh.Vertices.Clone(), (uint[])mesh.Indices32.Clone(),
            (Matrix4x4[])mesh.InverseBind.Clone(), (Matrix4x4[])mesh.RestPose.Clone(), mesh.Skeleton, mesh.Profile);
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
