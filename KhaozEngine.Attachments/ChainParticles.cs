using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainParticles
{
    internal ChainParticles(ChainRig rig)
    {
        Rig = rig;
        int count = checked(rig.JointCount + rig.Chains.Count);
        Positions = new Vector3[count];
        Velocities = new Vector3[count];
        SubstepStarts = new Vector3[count];
        RestPositions = new Vector3[count];
        Lengths = new float[rig.JointCount];
        JointWorlds = new Matrix4x4[rig.JointCount];
        JointSwings = new Quaternion[rig.JointCount];
        PreviousAnchorRotations = new Quaternion[rig.AnchorCount];
        StepAnchorRotations = new Quaternion[rig.AnchorCount];
        StepCarries = new ChainCarry[rig.Chains.Count];
    }

    internal ChainRig Rig { get; }
    internal Vector3[] Positions { get; }
    internal Vector3[] Velocities { get; }
    internal Vector3[] RestPositions { get; }
    internal Vector3[] SubstepStarts { get; }
    internal float[] Lengths { get; }
    internal Matrix4x4[] JointWorlds { get; }
    internal Quaternion[] JointSwings { get; }
    internal Quaternion[] PreviousAnchorRotations { get; private set; }
    internal Quaternion[] StepAnchorRotations { get; private set; }
    internal ChainCarry[] StepCarries { get; }
    internal ChainCapsuleCull CapsuleCull { get; } = new();

    internal void SetRest(ReadOnlySpan<Matrix4x4> anchors, bool reset)
    {
        for (int anchor = 0; anchor < anchors.Length; anchor++)
            StepAnchorRotations[anchor] = new ChainPose(anchors[anchor]).Rotation;

        for (int chain = 0; chain < Rig.Chains.Count; chain++)
        {
            var definition = Rig.Chains[chain];
            int first = Rig.FirstJoint(chain);
            int particle = first + chain;
            var anchor = anchors[definition.Anchor];
            var world = anchor;
            for (int index = 0; index < definition.Joints.Count; index++)
            {
                int joint = first + index;
                world = Rig.RestFromAnchor[joint] * anchor;
                RestPositions[particle + index] = world.Translation;
                // Transform the segment vector directly so length is independent of world translation.
                var offset = Vector3.TransformNormal(Rig.SegmentOffset(joint), world);
                Lengths[joint] = (float)Math.Sqrt(ChainMath.DistanceSquared(offset, Vector3.Zero));
                if (reset)
                    JointWorlds[joint] = world;
            }

            RestPositions[particle + definition.Joints.Count] = Vector3.Transform(definition.Tip, world);
        }

        if (reset)
        {
            RestPositions.CopyTo(Positions, 0);
            StepAnchorRotations.CopyTo(PreviousAnchorRotations, 0);
            Array.Clear(Velocities);
        }
    }

    internal void SeedFromShown(ReadOnlySpan<ChainPose> anchors, ReadOnlySpan<Matrix4x4> frames)
    {
        for (int anchor = 0; anchor < anchors.Length; anchor++)
            PreviousAnchorRotations[anchor] = anchors[anchor].Rotation;

        for (int chain = 0; chain < Rig.Chains.Count; chain++)
        {
            var definition = Rig.Chains[chain];
            var anchor = anchors[definition.Anchor].Matrix;
            var parent = anchor;
            int first = Rig.FirstJoint(chain);
            int end = first + definition.Joints.Count;
            for (int joint = first; joint < end; joint++)
            {
                parent = frames[joint] * anchor;
                Positions[joint + chain] = parent.Translation;
            }
            Positions[end + chain] = Vector3.Transform(definition.Tip, parent);
        }

        Array.Clear(Velocities);
    }

    internal void RememberAnchorRotations() =>
        (PreviousAnchorRotations, StepAnchorRotations) = (StepAnchorRotations, PreviousAnchorRotations);

    internal void CarryAcrossDroppedTime(int chain, float trail, Vector3 oldWearer, Vector3 newWearer, Quaternion wearerTurn)
    {
        var carry = ChainCarry.Between(trail, oldWearer, newWearer, wearerTurn);
        int root = Rig.FirstJoint(chain) + chain;
        Positions[root] = RestPositions[root];
        int end = root + Rig.Chains[chain].Joints.Count;
        for (int particle = root + 1; particle <= end; particle++)
            Positions[particle] = carry.TransformPoint(Positions[particle]);
    }

    internal void Predict(int chain, in ChainSettings settings, Vector3 gravity, float seconds,
        Vector3 oldWearer, Vector3 newWearer, Quaternion wearerTurn)
    {
        var carryMotion = ChainCarry.Between(settings.Trail, oldWearer, newWearer, wearerTurn);
        StepCarries[chain] = carryMotion;

        int root = Rig.FirstJoint(chain) + chain;
        int jointCount = Rig.Chains[chain].Joints.Count;
        SubstepStarts[root] = carryMotion.TransformPoint(Positions[root]);
        Positions[root] = RestPositions[root];
        Velocities[root] = seconds > 0 ? (Positions[root] - SubstepStarts[root]) / seconds : Vector3.Zero;

        float decay = MathF.Exp(-settings.Damping * seconds);
        var gravityVelocity = gravity * (settings.Gravity * seconds);
        var gravityDisplacement = gravityVelocity * (0.5f * seconds);
        for (int particle = root + 1; particle <= root + jointCount; particle++)
        {
            var current = carryMotion.TransformPoint(Positions[particle]);
            SubstepStarts[particle] = current;
            if (seconds == 0)
            {
                Positions[particle] = current;
                continue;
            }

            var damped = carryMotion.TransformDirection(Velocities[particle]) * decay;
            Positions[particle] = current + damped * seconds + gravityDisplacement;
            Velocities[particle] = damped + gravityVelocity;
        }
    }

    internal void ApplySpring(int particle, Vector3 correction, float seconds)
    {
        // Midpoint moves the position by half the velocity impulse times the timestep.
        Positions[particle] += correction;
        Velocities[particle] += correction / seconds * 2;
    }
}
