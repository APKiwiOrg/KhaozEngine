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

    internal ColliderCapsule Pose(ReadOnlySpan<Matrix4x4> frames, in Matrix4x4 anchor, float radius)
    {
        int joint = Rig.FirstJoint(Chain) + Index;
        int last = Rig.FirstJoint(Chain) + Rig.Chains[Chain].Joints.Count - 1;
        Matrix4x4 start = frames[joint] * anchor;
        Vector3 end = joint == last ? Vector3.Transform(Rig.Chains[Chain].Tip, start) :
            (frames[joint + 1] * anchor).Translation;
        return new(start.Translation, end, radius);
    }
}
