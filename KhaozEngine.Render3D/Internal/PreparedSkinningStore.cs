using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Internal;

/// <summary>An immutable reference to frame-owned palette words. Invalid after its store resets.</summary>
internal readonly struct PreparedSkinningPose
{
    internal PreparedSkinningStore Owner { get; }
    internal ulong Frame { get; }
    public SkinningProfile Profile { get; }
    public int Offset { get; }
    public int BoneCount { get; }
    public MeshBounds ModelBounds { get; }

    internal PreparedSkinningPose(PreparedSkinningStore owner, ulong frame, SkinningProfile profile,
        int offset, int boneCount, in MeshBounds modelBounds)
    {
        Owner = owner;
        Frame = frame;
        Profile = profile;
        Offset = offset;
        BoneCount = boneCount;
        ModelBounds = modelBounds;
    }
}

/// <summary>Prepares into bounded scratch before appending any frame payload. Returned spans are read-only
/// and retain their words across subsequent appends, until Reset. No caller-owned pose spans are retained.</summary>
internal sealed class PreparedSkinningStore
{
    readonly List<Matrix4x4> _lbs = new();
    readonly List<RigidDualQuaternion> _dq = new();
    ulong _frame;

    public int PoseCount { get; private set; }
    public int LbsCount => _lbs.Count;
    public int DqCount => _dq.Count;

    public PreparedSkinningPose Prepare(SkinnedGltfMesh mesh, ReadOnlySpan<Matrix4x4> joints,
        in SkinningValidationContext context) => Prepare(mesh, joints, context, null, null);

    // Scene admission supplies retained source metadata and validates placement before any words append.
    // Internal callers can still name an unavailable consumer. The three-argument entry prepares math only.
    internal PreparedSkinningPose Prepare(SkinnedGltfMesh mesh, ReadOnlySpan<Matrix4x4> joints,
        in SkinningValidationContext context, DqBoundsSource? sourceBounds, string? unavailableDqConsumer,
        Matrix4x4? world = null, string consumer = "ordinary skinning")
    {
        ArgumentNullException.ThrowIfNull(mesh);
        bool dq = mesh.Profile.Method == SkinningMethod.DualQuaternionRigid;
        if (joints.Length != mesh.BoneCount || joints.Length > SkinningMath.MaxBonesPerDraw)
        {
            string detail = $"Joint count {joints.Length} must match source count {mesh.BoneCount} and be at most {SkinningMath.MaxBonesPerDraw}.";
            if (dq) throw SkinningValidation.InvalidPose(context, -1, detail, nameof(joints));
            throw new ArgumentException(detail, nameof(joints));
        }

        if (!dq)
        {
            Span<Matrix4x4> scratch = stackalloc Matrix4x4[joints.Length];
            for (int joint = 0; joint < joints.Length; joint++)
                scratch[joint] = SkinningMath.Compose(joints[joint], mesh.InverseBind[joint]);
            int offset = _lbs.Count;
            _lbs.EnsureCapacity(checked(offset + scratch.Length));
            foreach (Matrix4x4 value in scratch) _lbs.Add(value);
            PoseCount++;
            return new PreparedSkinningPose(this, _frame, mesh.Profile, offset, joints.Length, default);
        }

        if (!sourceBounds.HasValue)
            SkinningValidation.ValidateSource(mesh.Vertices, mesh.BoneCount, mesh.Profile, context);
        DqBoundsSource boundsSource = sourceBounds ?? new DqBoundsSource(mesh.Vertices, mesh.BoneCount);
        Span<RigidDualQuaternion> prepared = stackalloc RigidDualQuaternion[joints.Length];
        for (int joint = 0; joint < joints.Length; joint++)
        {
            try
            {
                Matrix4x4 composed = DualQuaternionPalette.Compose(mesh.InverseBind[joint], joints[joint]);
                prepared[joint] = DualQuaternionPalette.FromComposed(composed, context, joint);
            }
            catch (ArgumentException error)
            {
                string name = "<unnamed>";
                if (mesh.Skeleton is Skeleton skeleton && joint < skeleton.JointToNode.Length &&
                    (uint)skeleton.JointToNode[joint] < (uint)skeleton.NodeNames.Count)
                    name = skeleton.NodeNames[skeleton.JointToNode[joint]];
                throw new ArgumentException($"{error.Message} Joint name '{name}'.", error.ParamName, error);
            }
        }
        if (!DualQuaternionBounds.TryBoundModel(boundsSource, prepared, out MeshBounds bounds))
            throw SkinningValidation.InvalidPose(context, -1,
                "Prepared pose must have a finite representable DQ bounds envelope.", nameof(joints));
        if (unavailableDqConsumer is not null)
            throw new NotSupportedException($"Asset '{context.Asset}', profile {mesh.Profile.Method} version " +
                $"{mesh.Profile.AlgorithmVersion}: DQ consumer '{unavailableDqConsumer}' is unavailable.");

        if (world.HasValue && !DualQuaternionBounds.TryBoundWorld(bounds, world.Value, out _))
            throw SkinningValidation.InvalidPose(context, -1,
                $"DQ consumer '{consumer}' requires a finite representable affine world envelope.", nameof(world));

        int start = _dq.Count;
        _dq.EnsureCapacity(checked(start + prepared.Length));
        foreach (RigidDualQuaternion value in prepared) _dq.Add(value);
        PoseCount++;
        return new PreparedSkinningPose(this, _frame, mesh.Profile, start, joints.Length, bounds);
    }

    public ReadOnlySpan<Matrix4x4> Lbs(in PreparedSkinningPose pose)
    {
        Require(pose, SkinningMethod.LinearBlend);
        return CollectionsMarshal.AsSpan(_lbs).Slice(pose.Offset, pose.BoneCount);
    }

    public ReadOnlySpan<RigidDualQuaternion> Dq(in PreparedSkinningPose pose)
    {
        Require(pose, SkinningMethod.DualQuaternionRigid);
        return CollectionsMarshal.AsSpan(_dq).Slice(pose.Offset, pose.BoneCount);
    }

    public void Reset()
    {
        _lbs.Clear();
        _dq.Clear();
        PoseCount = 0;
        _frame++;
    }

    void Require(in PreparedSkinningPose pose, SkinningMethod method)
    {
        if (!ReferenceEquals(pose.Owner, this) || pose.Frame != _frame || pose.Profile.Method != method)
            throw new InvalidOperationException("Prepared skinning pose belongs to another store, frame or method.");
    }
}
