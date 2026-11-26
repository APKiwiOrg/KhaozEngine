using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// The binding has no cloth workspace. It names the immutable fitted surface and the exact support sources.
internal sealed class WornClothBinding(BoundAttachmentSet set, int wornIndex, BoundClothSurface surface,
    WornMotionPart part, WornClothCapsule[] capsules, WornClothHull[] hulls,
    WornClothSegment[] segments)
{
    internal int WornIndex { get; } = wornIndex;
    internal BoundClothSurface Surface { get; } = surface;
    internal WornMotionPart Part { get; } = part;
    internal ChainExclusion Excluded { get; } = new(part.FirstChain, part.Binding.Chains.Length);
    internal WornClothCapsule[] Capsules { get; } = capsules;
    internal WornClothHull[] Hulls { get; } = hulls;
    internal WornClothSegment[] Segments { get; } = segments;

    internal bool HasCapacity => Hulls.Length <= ClothWorkLimits.HullLimit &&
        Segments.Length <= ClothWorkLimits.LowerSegmentLimit;

    internal void CopySkinPalette(Skeleton skeleton, ReadOnlySpan<Matrix4x4> host, Span<Matrix4x4> garment) =>
        set.CopyClothFallbackPalette(skeleton, host, Surface.PartId, garment[..Surface.JointCount]);

    internal void CopyFallback(WornMotion owner, Skeleton skeleton, ReadOnlySpan<Matrix4x4> host, in Matrix4x4 model,
        Span<Vector3> nodes, bool fitted)
    {
        Span<Matrix4x4> palette = stackalloc Matrix4x4[Surface.JointCount];
        CopySkinPalette(skeleton, host, palette);
        if (!fitted) owner.CopyChainPalette(WornIndex, Surface.PartId, host, palette, useClothFallback: true);
        Surface.CopyFallbackNodes(palette, model, nodes);
    }

    internal void ValidatePose(WornMotion owner, Skeleton skeleton, ReadOnlySpan<Matrix4x4> host, in Matrix4x4 model,
        bool fitted = true)
    {
        Span<Vector3> nodes = stackalloc Vector3[Surface.Rig.RestPositions.Length];
        CopyFallback(owner, skeleton, host, model, nodes, fitted);
        Surface.CopyPins(host, model, nodes);
        foreach (var capsule in Capsules) _ = capsule.Pose(host, model);
        foreach (var hull in Hulls) _ = hull.Pose(host, model);
    }

    internal bool CanUsePreviousPose(WornMotion owner, Skeleton skeleton, ReadOnlySpan<Matrix4x4> host, in Matrix4x4 model)
    {
        try
        {
            ValidatePose(owner, skeleton, host, model, fitted: false);
            return true;
        }
        catch (AttachmentBindingArgumentException)
        {
            return false;
        }
        catch (ClothSurfaceDataException)
        {
            return false;
        }
    }

    internal static Dictionary<uint, WornClothBinding?>?[] Create(IReadOnlyList<BoundAttachmentSet> sets,
        Dictionary<uint, WornMotionPart>?[] parts, BoundOutfit? outfit, ChainRig rig, WornCollider[] colliders, int bodyCount)
    {
        var result = new Dictionary<uint, WornClothBinding?>?[sets.Count];
        for (int index = 0; index < sets.Count; index++)
        {
            var set = sets[index];
            for (int part = 0; part < set.Definition.Parts.Count; part++)
            {
                if (set.Definition.Parts[part] is not SkinnedAttachmentPartDefinition { Motion: ClothMotionDefinition } definition)
                    continue;

                uint id = definition.Id;
                var surface = outfit is null ? set.PartCloth(part) : outfit.ClothSurface(index, id);
                WornClothBinding? bound = null;
                if (surface is { HasVisibleMotion: true } && parts[index] is { } visible && visible.TryGetValue(id, out var range))
                {
                    var layer = set.Definition.Layer ?? AttachmentLayer.Body;
                    var (capsules, hulls) = BindRigid(sets, colliders, bodyCount, layer);
                    bound = new(set, index, surface, range, capsules, hulls,
                        BindSegments(parts, sets, rig, layer));
                }
                (result[index] ??= []).Add(id, bound);
            }
        }
        return result;
    }

    private static (WornClothCapsule[], WornClothHull[]) BindRigid(IReadOnlyList<BoundAttachmentSet> sets,
        ReadOnlySpan<WornCollider> colliders, int bodyCount, AttachmentLayer exterior)
    {
        var capsules = new List<WornClothCapsule>();
        var hulls = new List<WornClothHull>();
        for (int i = 0; i < bodyCount; i++)
            capsules.Add(new(colliders[i], default));

        int first = bodyCount;
        for (int source = 0; source < sets.Count; source++)
        {
            var set = sets[source];
            var layer = set.Definition.Layer ?? AttachmentLayer.Body;
            int count = set.Definition.Colliders.Count;
            if (layer <= exterior)
            {
                var identities = new ClothSupportIdentity[count];
                Array.Fill(identities, new ClothSupportIdentity(source + 1, 0, 0, layer));
                foreach (var hull in set.ClothHulls)
                {
                    var identity = hull.Identity(source + 1);
                    hulls.Add(new(hull, identity));
                    foreach (int index in hull.CapsuleIndices) identities[index] = identity;
                }
                for (int i = 0; i < count; i++)
                    capsules.Add(new(colliders[first + i], identities[i]));
            }
            first += count;
        }
        return (capsules.ToArray(), hulls.ToArray());
    }

    private static WornClothSegment[] BindSegments(Dictionary<uint, WornMotionPart>?[] parts,
        IReadOnlyList<BoundAttachmentSet> sets, ChainRig rig, AttachmentLayer exterior)
    {
        var segments = new List<WornClothSegment>();
        for (int index = 0; index < sets.Count; index++)
        {
            var layer = sets[index].Definition.Layer;
            if (layer is not { } lower || lower >= exterior || parts[index] is not { } visible)
                continue;
            foreach (var definition in sets[index].Definition.Parts)
            {
                if (!visible.TryGetValue(definition.Id, out var part)) continue;
                var identity = new ClothSupportIdentity(index + 1, definition.Id, 0, lower);
                for (int chain = part.FirstChain; chain < part.FirstChain + part.Binding.Chains.Length; chain++)
                    for (int segment = 0; segment < rig.Chains[chain].Joints.Count; segment++)
                        segments.Add(new(new ChainSegment(rig, chain, segment), identity));
            }
        }
        return segments.ToArray();
    }
}

internal readonly record struct WornClothSegment(ChainSegment Segment, ClothSupportIdentity Identity);

internal readonly record struct WornClothCapsule(WornCollider Collider, ClothSupportIdentity Identity)
{
    internal ClothSupport Pose(ReadOnlySpan<Matrix4x4> host, in Matrix4x4 model)
    {
        Matrix4x4 joint = WornMotion.PoseJoint(Collider.Bone, host, model);
        Matrix4x4 world = Collider.InverseBind * joint;
        Quaternion rotation = ChainPose.ReadRotation(world);
        Matrix4x4 basis = world;
        basis.Translation = Vector3.Zero;
        basis *= Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(rotation));
        ColliderCapsule capsule = Collider.Pose(joint);
        var local = new ColliderCapsule(Vector3.TransformNormal(Collider.RestCapsule.Start, basis),
            Vector3.TransformNormal(Collider.RestCapsule.End, basis), capsule.Radius);
        return ClothSupport.PosedCapsule(local, rotation, world.Translation, Identity);
    }
}

internal readonly record struct WornClothHull(BoundClothHull Hull, ClothSupportIdentity Identity)
{
    internal Matrix4x4 Pose(ReadOnlySpan<Matrix4x4> host, in Matrix4x4 model)
    {
        _ = WornMotion.PoseJoint(Hull.HostBone, host, model);
        try
        {
            return Hull.Compose(host[Hull.HostBone], model);
        }
        catch (ArgumentException error) when (error.ParamName == "jointModel")
        {
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.HostJointPoseInvalid,
                "The joint cannot form a rigid support pose for the cloth hull.", "hostPalette", error)
            { BoneIndex = Hull.HostBone, PartId = Hull.PartId };
        }
    }
}
