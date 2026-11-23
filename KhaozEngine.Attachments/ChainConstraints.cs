using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainConstraints
{
    internal static void FollowChain(ChainParticles particles, int chain, in Matrix4x4 anchor,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        in ChainSettings settings, float seconds, bool pullTowardRest)
    {
        var rig = particles.Rig;
        int first = rig.FirstJoint(chain);
        int last = first + rig.Chains[chain].Joints.Count - 1;
        float springWeight = pullTowardRest ? ChainMath.MidpointSpringWeight(settings.Stiffness, seconds) : 0;
        float cosine = settings.AngleLimit == MathF.PI ? -1 : MathF.Cos(settings.AngleLimit);
        var parent = anchor;
        var velocity = new ChainVelocityConstraints(particles, first + chain, particles.StepCarries[chain],
            colliders, previousColliders, settings.Radius, seconds);

        for (int joint = first; joint <= last; joint++)
        {
            int particle = joint + chain;
            var start = particles.Positions[particle];
            float length = particles.Lengths[joint];
            var world = rig.RestLocal(joint) * parent;
            var restOffset = Vector3.TransformNormal(rig.SegmentOffset(joint), world);
            var rest = ChainMath.Direction(restOffset, Vector3.UnitY);
            particles.StepRestDirections[joint] = rest;
            if (joint == first)
                velocity.Start(particles.PreviousRestDirections[joint], rest);

            int end = particle + 1;
            var predicted = particles.Positions[end] - start;
            if (springWeight > 0)
            {
                var started = particles.SubstepStarts[end] - particles.SubstepStarts[particle];
                var correction = springWeight * (length * rest - 0.5f * (started + predicted));
                particles.ApplySpring(end, correction, seconds);
                predicted += correction;
            }

            var direction = ChainMath.Direction(predicted, rest);
            bool clamped = false;
            if (length > 0)
            {
                var limited = ClampBend(direction, rest, cosine);
                clamped = limited != direction;
                direction = limited;
                direction = ChainCapsuleCollision.Clear(direction, start, length, settings.Radius, colliders);
            }

            particles.Positions[end] = start + length * direction;
            velocity.Apply(particle, length, direction, rest, clamped);
            var swing = length > 0 ? ChainMath.Swing(rest, direction) : Quaternion.Identity;
            parent = world * Matrix4x4.CreateFromQuaternion(swing);
            parent.Translation = start;
            particles.JointWorlds[joint] = parent;
        }
    }

    private static Vector3 ClampBend(Vector3 direction, Vector3 rest, float cosine)
    {
        if (Vector3.Dot(direction, rest) >= cosine)
            return direction;
        var tangent = ChainMath.Tangent(direction, rest);
        if (tangent == Vector3.Zero)
            tangent = ChainMath.Perpendicular(rest);
        return cosine * rest + MathF.Sqrt(1 - cosine * cosine) * tangent;
    }
}
