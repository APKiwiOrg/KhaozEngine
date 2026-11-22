using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    Dictionary<AttachmentMeshRef, IReadOnlyList<GarmentChainJoint>>? _chainJoints;

    internal IReadOnlyList<GarmentChainJoint>? CachedChainJointsForTests(AttachmentMeshRef reference) =>
        _chainJoints?.GetValueOrDefault(reference);

    internal IReadOnlyList<GarmentChainJoint> ReadChainJoints(AttachmentMeshRef reference,
        ref Dictionary<AttachmentMeshRef, IReadOnlyList<GarmentChainJoint>>? pending)
    {
        if (_chainJoints?.TryGetValue(reference, out IReadOnlyList<GarmentChainJoint>? cached) == true) return cached;
        pending ??= [];
        if (pending.TryGetValue(reference, out IReadOnlyList<GarmentChainJoint>? staged)) return staged;

        Skeleton skeleton = GltfLoader.LoadAttachmentSkeleton(_meshes[new MeshIdentity(reference, true)].Bytes, reference.Key);
        var joints = new GarmentChainJoint[skeleton.BoneCount];
        for (int bone = 0; bone < joints.Length; bone++)
            joints[bone] = GarmentChainJoint.Capture(skeleton, skeleton.JointToNode[bone]);
        var captured = Array.AsReadOnly(joints);
        pending.Add(reference, captured);
        return captured;
    }

    internal void PublishChainJoints(Dictionary<AttachmentMeshRef, IReadOnlyList<GarmentChainJoint>> pending)
    {
        if (_chainJoints is null)
        {
            _chainJoints = pending;
            return;
        }
        foreach (var entry in pending) _chainJoints.Add(entry.Key, entry.Value);
    }
}
