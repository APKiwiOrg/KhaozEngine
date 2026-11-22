using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainConstraints
{
    internal static void FollowChain(ChainParticles particles, int chain, in Matrix4x4 anchor,
        ReadOnlySpan<ColliderCapsule> colliders, in ChainSettings settings, float seconds, bool pullTowardRest)
    {
        var rig = particles.Rig;
        int first = rig.FirstJoint(chain);
        int last = first + rig.Chains[chain].Joints.Count - 1;
        float springWeight = pullTowardRest ? ChainMath.MidpointSpringWeight(settings.Stiffness, seconds) : 0;
        float cosine = settings.AngleLimit == MathF.PI ? -1 : MathF.Cos(settings.AngleLimit);
        var parent = anchor;

        for (int joint = first; joint <= last; joint++)
        {
            int particle = joint + chain;
            var start = particles.Positions[particle];
            float length = particles.Lengths[joint];
            var world = rig.RestLocal(joint) * parent;
            var restOffset = Vector3.TransformNormal(rig.SegmentOffset(joint), world);
            var rest = ChainMath.Direction(restOffset, Vector3.UnitY);
            int end = particle + 1;
            var predicted = particles.Positions[end] - start;
            if (springWeight > 0)
            {
                var started = particles.SubstepStarts[end] - start;
                var correction = springWeight * (length * rest - 0.5f * (started + predicted));
                particles.ApplySpring(end, correction);
                predicted += correction;
            }

            var direction = ChainMath.Direction(predicted, rest);
            if (length > 0)
            {
                direction = ClampBend(direction, rest, cosine);
                direction = ChainCapsuleCollision.Clear(direction, start, length, settings.Radius, colliders);
            }

            particles.Constrain(particle, start + length * direction, length > 0 ? direction : Vector3.Zero, preserveVelocity: seconds == 0);
            var swing = length > 0 ? ChainMath.Swing(rest, direction) : Quaternion.Identity;
            parent = world * Matrix4x4.CreateFromQuaternion(swing);
            parent.Translation = start;
            particles.JointWorlds[joint] = parent;
            particles.WorldSwings[joint] = swing;
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
