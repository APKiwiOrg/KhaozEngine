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
    internal SkinningProfile Profile { get; } = SkinningProfile.LinearBlend;
    internal IReadOnlyList<MeshHandle> RigidHandles { get; } = Array.Empty<MeshHandle>();
    internal IReadOnlyList<SkinnedMeshHandle> SkinnedHandles { get; } = Array.Empty<SkinnedMeshHandle>();
    internal IReadOnlyList<string> JointNames { get; } = Array.Empty<string>();
    internal IReadOnlyList<Matrix4x4> InverseBind { get; } = Array.Empty<Matrix4x4>();
    internal IReadOnlyList<Matrix4x4> RestPose { get; } = Array.Empty<Matrix4x4>();

    internal AttachmentPartResources(IReadOnlyList<MeshHandle> handles) =>
        RigidHandles = Array.AsReadOnly(handles.ToArray());

    internal AttachmentPartResources(IReadOnlyList<SkinnedMeshHandle> handles, SkinnedGltfMesh skin)
    {
        IsSkinned = true;
        Profile = skin.Profile;
        SkinnedHandles = Array.AsReadOnly(handles.ToArray());
        Skeleton skeleton = skin.Skeleton ?? throw new ArgumentException("An attachment skin needs a skeleton.", nameof(skin));
        JointNames = Array.AsReadOnly(skeleton.JointToNode.Select(node => skeleton.NodeNames[node]).ToArray());
        InverseBind = Array.AsReadOnly((Matrix4x4[])skin.InverseBind.Clone());
        RestPose = Array.AsReadOnly((Matrix4x4[])skin.RestPose.Clone());
    }
}
