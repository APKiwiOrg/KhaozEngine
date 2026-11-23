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
        WorldSwings = new Quaternion[rig.JointCount];
        PreviousRestDirections = new Vector3[rig.JointCount];
        StepRestDirections = new Vector3[rig.JointCount];
        StepCarries = new ChainCarry[rig.Chains.Count];
    }

    internal ChainRig Rig { get; }
    internal Vector3[] Positions { get; }
    internal Vector3[] Velocities { get; }
    internal Vector3[] RestPositions { get; }
    internal Vector3[] SubstepStarts { get; }
    internal float[] Lengths { get; }
    internal Matrix4x4[] JointWorlds { get; }
    internal Quaternion[] WorldSwings { get; }
    internal Vector3[] PreviousRestDirections { get; private set; }
    internal Vector3[] StepRestDirections { get; private set; }
    internal ChainCarry[] StepCarries { get; }
    internal ChainCapsuleCull CapsuleCull { get; } = new();

    internal void SetRest(ReadOnlySpan<Matrix4x4> anchors, bool reset)
    {
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
                {
                    JointWorlds[joint] = world;
                    WorldSwings[joint] = Quaternion.Identity;
                    PreviousRestDirections[joint] = ChainMath.Direction(offset, Vector3.UnitY);
                }
            }

            RestPositions[particle + definition.Joints.Count] = Vector3.Transform(definition.Tip, world);
        }

        if (reset)
        {
            RestPositions.CopyTo(Positions, 0);
            ZeroVelocity();
        }
    }

    internal void ZeroVelocity() => Array.Clear(Velocities);

    internal void RememberRestDirections() =>
        (PreviousRestDirections, StepRestDirections) = (StepRestDirections, PreviousRestDirections);

    internal void Predict(int chain, in ChainSettings settings, Vector3 gravity, float seconds,
        Vector3 oldWearer, Vector3 newWearer, Quaternion wearerTurn)
    {
        float carry = 1 - settings.Trail;
        var carriedTurn = carry == 0 ? Quaternion.Identity : Quaternion.Slerp(Quaternion.Identity, wearerTurn, carry);
        var carryMotion = new ChainCarry(oldWearer, carry * (newWearer - oldWearer), carriedTurn);
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
