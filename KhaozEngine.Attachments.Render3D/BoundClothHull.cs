using System;
using System.Collections.Immutable;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A fitted rigid support with an authored hull and matching inexpensive capsules.</summary>
public sealed class BoundClothHull
{
    readonly Matrix4x4 _inverseRest;
    readonly ClothHullGroup _group;

    internal BoundClothHull(uint partId, AttachmentLayer layer, int hostBone, Matrix4x4 inverseRest, ClothHullGroup group)
    {
        PartId = partId;
        Layer = layer;
        HostBone = hostBone;
        _inverseRest = inverseRest;
        _group = group;
    }

    public uint PartId { get; }
    public AttachmentLayer Layer { get; }
    public int HostBone { get; }
    public int FeatureGroup => _group.FeatureGroup;
    public ImmutableArray<int> CapsuleIndices => _group.CapsuleIndices;
    public ClothSupportHull Hull => _group.Hull;

    public ClothSupportIdentity Identity(int sourceId) => new(sourceId, PartId, FeatureGroup, Layer);

    /// <summary>Move the rest-model hull with the displayed rigid attachment.</summary>
    public Matrix4x4 Compose(in Matrix4x4 jointModel, in Matrix4x4 model) =>
        _inverseRest * BoneSocket.ComposeRigid(Matrix4x4.Identity, jointModel, model);

    internal static Matrix4x4 InverseRestFrame(RigidAttachmentPartDefinition part, int bone, in Matrix4x4 jointRest)
    {
        try
        {
            // The static part transform cancels in inverse(part * restJoint) * part * currentJoint.
            Matrix4x4 rest = BoneSocket.ComposeRigid(Matrix4x4.Identity, jointRest, Matrix4x4.Identity);
            if (!Matrix4x4.Invert(rest, out Matrix4x4 inverse) || !AttachmentSetBinding.Finite(inverse))
                throw new ArgumentException("The rigid rest frame has no finite inverse.", nameof(jointRest));
            return inverse;
        }
        catch (ArgumentException error)
        {
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.ClothHullBindInvalid,
                $"Attachment part {part.Id}: its cloth support needs a finite invertible rigid rest frame.", error)
            { PartId = part.Id, MeshKey = part.Mesh.Key, JointName = part.Joint, BoneIndex = bone };
        }
    }
}
