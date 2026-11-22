using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Flattens the visible garment chains and all collider sources into one wearer rig.</summary>
internal static class WornMotionBinding
{
    const int MaxColliders = 64;

    internal static WornMotion Create(object bindingIdentity, Skeleton skeleton, ReadOnlySpan<Matrix4x4> inverseBind,
        IReadOnlyList<BoundAttachmentSet> worn, OutfitDraw[][]? draws, LoadedBodyColliders? body, string? bodyRigIdentity,
        ChainSimulationOptions? options)
    {
        for (int index = 0; index < worn.Count; index++)
        {
            BoundAttachmentSet set = worn[index];
            if (!ReferenceEquals(set.Skeleton, skeleton))
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.HostSkeletonMismatch,
                    $"Worn set {index} uses a different skeleton instance from the wearer.")
                { WornIndex = index, SourceName = set.SourceName };
        }

        BodyColliderSet? bodySet = bodyRigIdentity is null ? body?.Set : body?.ForRig(bodyRigIdentity);
        WornCollider[] colliders = BindColliders(skeleton, inverseBind, worn, body, bodySet);

        var anchors = new Dictionary<int, int>();
        var anchorBones = new List<int>();
        var chains = new List<ChainRigChain>();
        var links = new List<ChainLink>();
        var parts = new Dictionary<uint, WornMotionPart>?[worn.Count];
        for (int wornIndex = 0; wornIndex < worn.Count; wornIndex++)
        {
            BoundAttachmentSet set = worn[wornIndex];
            bool[]? visible = draws is null ? null : VisibleParts(draws[wornIndex], set.Definition.Parts.Count);
            for (int part = 0; part < set.Definition.Parts.Count; part++)
            {
                if (visible?[part] == false || set.PartMotion(part) is not { } motion) continue;
                int start = chains.Count;
                uint partId = set.Definition.Parts[part].Id;
                (parts[wornIndex] ??= []).Add(partId, new WornMotionPart(motion, start));
                foreach (BoundAttachmentChain chain in motion.Chains)
                {
                    if (!anchors.TryGetValue(chain.AnchorBone, out int anchor))
                    {
                        anchor = anchorBones.Count;
                        anchors.Add(chain.AnchorBone, anchor);
                        anchorBones.Add(chain.AnchorBone);
                    }
                    chains.Add(new ChainRigChain(anchor, chain.RestLocals, chain.Tip, chain.Settings));
                }
                foreach (ChainLink link in motion.Links)
                    links.Add(new ChainLink(start + link.First, start + link.Second));
            }
        }

        var rig = new ChainRig(anchorBones.Count, chains, links);
        return new WornMotion(bindingIdentity, skeleton, parts, rig, anchorBones.ToArray(), colliders, options);
    }

    static bool[] VisibleParts(OutfitDraw[] draws, int partCount)
    {
        var visible = new bool[partCount];
        foreach (ref readonly OutfitDraw draw in draws.AsSpan()) visible[draw.Part] = true;
        return visible;
    }

    static WornCollider[] BindColliders(Skeleton skeleton, ReadOnlySpan<Matrix4x4> inverseBind,
        IReadOnlyList<BoundAttachmentSet> worn, LoadedBodyColliders? body, BodyColliderSet? bodySet)
    {
        long count = bodySet?.Colliders.Count ?? 0;
        foreach (BoundAttachmentSet set in worn) count += set.Definition.Colliders.Count;
        if (count > MaxColliders)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.WearerCollidersTooMany,
                $"The wearer has {count} colliders, above the maximum of {MaxColliders}.")
            { ColliderCount = count, MaximumColliderCount = MaxColliders };

        var result = new WornCollider[(int)count];
        int written = 0;
        if (bodySet is not null)
            BindSource(body!.SourceName, bodySet.Colliders, skeleton, inverseBind, result, ref written);
        foreach (BoundAttachmentSet set in worn)
            BindSource(set.SourceName, set.Definition.Colliders, skeleton, inverseBind, result, ref written);
        return result;
    }

    static void BindSource(string sourceName, IReadOnlyList<ColliderDefinition> colliders, Skeleton skeleton,
        ReadOnlySpan<Matrix4x4> inverseBind, Span<WornCollider> result, ref int written)
    {
        if (colliders.Count > 0 && inverseBind.Length != skeleton.BoneCount)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.ColliderHostMeshRequired,
                $"{sourceName}: binding a collider requires the host skin's inverse binds.")
            { SourceName = sourceName };
        for (int index = 0; index < colliders.Count; index++)
        {
            ColliderDefinition collider = colliders[index];
            int bone = skeleton.TryIndexOf(collider.Joint, out int node) ? skeleton.BoneIndexOfNode(node) : -1;
            if (bone < 0)
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.ColliderJointUnresolved,
                    $"{sourceName}: collider {index} joint '{collider.Joint}' is outside the host skin.")
                { SourceName = sourceName, ColliderIndex = index, JointName = collider.Joint };
            result[written++] = WornCollider.Bind(bone, collider, inverseBind[bone]);
        }
    }
}

/// <summary>A collider with its endpoints and radius in the host bone's local frame.</summary>
internal readonly record struct WornCollider(int Bone, ColliderCapsule Local)
{
    internal static WornCollider Bind(int bone, in ColliderDefinition collider, in Matrix4x4 inverseBind) =>
        new(bone, Transform(new ColliderCapsule(collider.Start, collider.End, collider.Radius), inverseBind));

    internal ColliderCapsule Pose(in Matrix4x4 jointWorld) => Transform(Local, jointWorld);

    static ColliderCapsule Transform(in ColliderCapsule capsule, in Matrix4x4 matrix)
    {
        float scale = MathF.Sqrt(MathF.Max(Vector3.TransformNormal(Vector3.UnitX, matrix).LengthSquared(),
            MathF.Max(Vector3.TransformNormal(Vector3.UnitY, matrix).LengthSquared(),
                Vector3.TransformNormal(Vector3.UnitZ, matrix).LengthSquared())));
        return new ColliderCapsule(Vector3.Transform(capsule.Start, matrix), Vector3.Transform(capsule.End, matrix),
            capsule.Radius * scale);
    }
}
