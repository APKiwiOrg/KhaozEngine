using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// An exterior surface borrows the shared chain clock. The callbacks do not retain the simulation arrays.
internal interface IChainExternalStep
{
    ChainExclusion Excluded { get; }
    void OnDroppedTime();
    void OnSubstep(ChainSimulation simulation, ChainParticles? particles, float fraction);
}

// Links created for one worn part have no endpoints outside the part's contiguous chain range.
internal readonly record struct ChainExclusion(int First, int Count)
{
    internal bool Includes(int chain) => (uint)(chain - First) >= (uint)Count;

    internal (int First, int Count) ParticleRange(ChainRig rig) => Count == 0 ? default :
        (rig.FirstJoint(First) + First,
            (First + Count == rig.Chains.Count ? rig.JointCount + rig.Chains.Count :
                rig.FirstJoint(First + Count) + First + Count) - (rig.FirstJoint(First) + First));
}

/// <summary>The fixed-step capsules of one lower chain selected by the exterior binding.</summary>
internal readonly record struct ChainSegment(ChainRig Rig, int Chain, int Index)
{
    internal ColliderCapsule Sample(ChainParticles particles, float radius, bool opening)
    {
        var positions = opening ? particles.OpeningPositions : particles.Positions;
        int start = Rig.FirstJoint(Chain) + Chain + Index;
        return new(positions[start], positions[start + 1], radius);
    }

    internal ColliderCapsule Pose(ReadOnlySpan<Matrix4x4> frames, in Matrix4x4 anchor, float radius) =>
        Pose(frames, anchor, radius, out _);

    internal HeldChainSegment PoseHeld(ReadOnlySpan<Matrix4x4> frames, in ChainPose anchor, float radius)
    {
        ColliderCapsule capsule = Pose(frames, anchor.Matrix, MathF.Max(1e-10f, radius), out Matrix4x4 start);
        Vector3 root = Vector3.Transform(frames[Rig.FirstJoint(Chain)].Translation, anchor.Matrix);
        return new HeldChainSegment(capsule, ChainPose.ReadRotation(start), anchor, root);
    }

    private ColliderCapsule Pose(ReadOnlySpan<Matrix4x4> frames, in Matrix4x4 anchor, float radius, out Matrix4x4 start)
    {
        int joint = Rig.FirstJoint(Chain) + Index;
        int last = Rig.FirstJoint(Chain) + Rig.Chains[Chain].Joints.Count - 1;
        start = frames[joint] * anchor;
        Vector3 end = joint == last ? Vector3.Transform(Rig.Chains[Chain].Tip, start) :
            (frames[joint + 1] * anchor).Translation;
        return new(start.Translation, end, radius);
    }
}

internal readonly record struct HeldChainSegment(ColliderCapsule Capsule, Quaternion Rotation, ChainPose Anchor,
    Vector3 ChainRoot)
{
    internal Vector3 Start => Capsule.Start;
    internal Vector3 End => Capsule.End;
}
