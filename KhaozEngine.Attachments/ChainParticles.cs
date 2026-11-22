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
        SubstepStarts = new Vector3[count];
        RestPositions = new Vector3[count];
        Lengths = new float[rig.JointCount];
        JointWorlds = new Matrix4x4[rig.JointCount];
        WorldSwings = new Quaternion[rig.JointCount];
    }

    internal ChainRig Rig { get; }
    internal Vector3[] Positions { get; }
    private Vector3[] Previous { get; }
    internal Vector3[] RestPositions { get; }
    internal Vector3[] SubstepStarts { get; }
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
        Positions[root] = Previous[root] = SubstepStarts[root] = RestPositions[root];
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

            SubstepStarts[particle] = current;
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

    internal void ApplySpring(int particle, Vector3 correction)
    {
        // Midpoint moves the position by half the velocity impulse times the timestep.
        // Shifting both Verlet samples encodes the full impulse for the next substep.
        Positions[particle] += correction;
        Previous[particle] -= correction;
    }

    internal void Constrain(int parent, Vector3 position, Vector3 normal, bool preserveVelocity)
    {
        int particle = parent + 1;
        if (preserveVelocity)
        {
            Previous[particle] += position - Positions[particle];
        }
        else
        {
            // The link to the parent permits tangential motion only. Carry end-of-step
            // velocity forward without the radial component introduced by the projection.
            var parentDisplacement = Positions[parent] - Previous[parent];
            var relativeDisplacement = position - Previous[particle] - parentDisplacement;
            Previous[particle] += normal == Vector3.Zero
                ? relativeDisplacement
                : Vector3.Dot(relativeDisplacement, normal) * normal;
        }

        Positions[particle] = position;
    }
}
