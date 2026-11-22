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
        Previous = new Vector3[count];
        RestPositions = new Vector3[count];
        Lengths = new float[rig.JointCount];
        JointWorlds = new Matrix4x4[rig.JointCount];
        WorldSwings = new Quaternion[rig.JointCount];
    }

    internal ChainRig Rig { get; }
    internal Vector3[] Positions { get; }
    private Vector3[] Previous { get; }
    internal Vector3[] RestPositions { get; }
    internal float[] Lengths { get; }
    internal Matrix4x4[] JointWorlds { get; }
    internal Quaternion[] WorldSwings { get; }

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
                }
            }

            RestPositions[particle + definition.Joints.Count] = Vector3.Transform(definition.Tip, world);
        }

        if (reset)
        {
            RestPositions.CopyTo(Positions, 0);
            RestPositions.CopyTo(Previous, 0);
        }
    }

    internal void ZeroVelocity() => Positions.CopyTo(Previous, 0);

    internal void Predict(int root, int jointCount, in ChainSettings settings, Vector3 gravity, float seconds,
        float previousSeconds, Vector3 oldWearer, Vector3 newWearer, Quaternion wearerTurn)
    {
        Positions[root] = Previous[root] = RestPositions[root];
        float carry = 1 - settings.Trail;
        var carriedTurn = carry == 0 ? Quaternion.Identity : Quaternion.Slerp(Quaternion.Identity, wearerTurn, carry);
        var carriedOffset = carry * (newWearer - oldWearer);
        double velocityFactor = previousSeconds > 0 && seconds > 0
            ? (double)seconds / previousSeconds * MathF.Exp(-settings.Damping * seconds)
            : 0;
        var acceleration = gravity * (settings.Gravity * seconds * seconds);
        for (int particle = root + 1; particle <= root + jointCount; particle++)
        {
            var current = Positions[particle];
            var previous = Previous[particle];
            if (carry > 0)
            {
                current = Carry(current);
                previous = Carry(previous);
            }

            var displacement = current - previous;
            Positions[particle] = current + new Vector3(
                (float)(displacement.X * velocityFactor),
                (float)(displacement.Y * velocityFactor),
                (float)(displacement.Z * velocityFactor)) + acceleration;
            Previous[particle] = seconds > 0 ? current : previous;
        }

        Vector3 Carry(Vector3 position) => carriedTurn == Quaternion.Identity
            ? position + carriedOffset
            : oldWearer + carriedOffset + Vector3.Transform(position - oldWearer, carriedTurn);
    }

    internal void Constrain(int particle, Vector3 position, bool preserveVelocity)
    {
        if (preserveVelocity)
            Previous[particle] += position - Positions[particle];
        Positions[particle] = position;
    }
}
