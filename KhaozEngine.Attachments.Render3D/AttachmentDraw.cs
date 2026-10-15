using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>One material draw of a rigid or skinned attachment part.</summary>
/// <remarks>BoneMatrices borrows the bound view's palette until its next submission or preparation, or cache
/// disposal. Consume synchronously. Only the descriptor selected by IsSkinned is populated.</remarks>
public readonly struct AttachmentDraw
{
    internal AttachmentDraw(uint partId, int materialIndex, in RigidInstanceDraw rigid)
    {
        PartId = partId;
        MaterialIndex = materialIndex;
        IsSkinned = false;
        Rigid = rigid;
        Skinned = default;
        BoneMatrices = default;
    }

    internal AttachmentDraw(uint partId, int materialIndex, in SkinnedInstanceDraw skinned,
        ReadOnlyMemory<Matrix4x4> boneMatrices)
    {
        PartId = partId;
        MaterialIndex = materialIndex;
        IsSkinned = true;
        Rigid = default;
        Skinned = skinned;
        BoneMatrices = boneMatrices;
    }

    public uint PartId { get; }
    public int MaterialIndex { get; }
    public bool IsSkinned { get; }
    public RigidInstanceDraw Rigid { get; }
    public SkinnedInstanceDraw Skinned { get; }
    public ReadOnlyMemory<Matrix4x4> BoneMatrices { get; }
}
