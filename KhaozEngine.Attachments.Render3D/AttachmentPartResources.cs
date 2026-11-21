using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Borrowed material-ordered handles and immutable skin-order bind metadata for one mesh revision.</summary>
internal sealed class AttachmentPartResources
{
    internal bool IsSkinned { get; }
    internal IReadOnlyList<MeshHandle> RigidHandles { get; } = Array.Empty<MeshHandle>();
    internal IReadOnlyList<SkinnedMeshHandle> SkinnedHandles { get; } = Array.Empty<SkinnedMeshHandle>();
    internal IReadOnlyList<string> JointNames { get; } = Array.Empty<string>();
    internal IReadOnlyList<Matrix4x4> InverseBind { get; } = Array.Empty<Matrix4x4>();
    internal IReadOnlyList<Matrix4x4> RestPose { get; } = Array.Empty<Matrix4x4>();
    // Each skinned handle's decoded index list in material order, kept so an outfit can build mesh views without
    // decoding the mesh again. The cache owns the arrays and nothing writes them after decode.
    internal IReadOnlyList<uint[]> SkinnedIndices { get; } = Array.Empty<uint[]>();
    internal int SkinnedTriangleCount { get; }

    internal AttachmentPartResources(IReadOnlyList<MeshHandle> handles) =>
        RigidHandles = Array.AsReadOnly(handles.ToArray());

    internal AttachmentPartResources(IReadOnlyList<SkinnedMeshHandle> handles, IReadOnlyList<GltfSkinnedMeshPart> parts)
    {
        IsSkinned = true;
        SkinnedHandles = Array.AsReadOnly(handles.ToArray());
        SkinnedIndices = Array.AsReadOnly(parts.Select(part => part.Mesh.Indices32).ToArray());
        SkinnedTriangleCount = parts.Sum(part => part.Mesh.TriangleCount);
        SkinnedGltfMesh skin = parts[0].Mesh;
        // Internal invariant: the cache captures only imported skins, which always have a skeleton.
        Skeleton skeleton = skin.Skeleton ?? throw new ArgumentException("An attachment skin needs a skeleton.", nameof(parts));
        JointNames = Array.AsReadOnly(skeleton.JointToNode.Select(node => skeleton.NodeNames[node]).ToArray());
        InverseBind = Array.AsReadOnly((Matrix4x4[])skin.InverseBind.Clone());
        RestPose = Array.AsReadOnly((Matrix4x4[])skin.RestPose.Clone());
    }
}
