using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainConstraints
{
    private const float LinkRate = 20;

    internal static void FollowChain(ChainParticles particles, int chain, in Matrix4x4 anchor,
        ReadOnlySpan<ColliderCapsule> colliders, in ChainSettings settings, float seconds, bool pullTowardRest)
    {
        var rig = particles.Rig;
        int first = rig.FirstJoint(chain);
        int last = first + rig.Chains[chain].Joints.Count - 1;
        float pull = pullTowardRest ? ChainMath.RateWeight(settings.Stiffness, seconds) : 0;
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
            var predicted = particles.Positions[particle + 1] - start;
            var direction = ChainMath.Direction(Vector3.Lerp(predicted, length * rest, pull), rest);
            if (length > 0)
            {
                direction = ClampBend(direction, rest, cosine);
                direction = ChainCapsuleCollision.Clear(direction, rest, cosine, start, length, settings.Radius, colliders);
            }

            particles.Constrain(particle + 1, start + length * direction, preserveVelocity: seconds == 0);
            var swing = length > 0 ? ChainMath.Swing(rest, direction) : Quaternion.Identity;
            parent = world * Matrix4x4.CreateFromQuaternion(swing);
            parent.Translation = start;
            particles.JointWorlds[joint] = parent;
            particles.WorldSwings[joint] = swing;
        }
    }

    internal static bool ApplyLinks(ChainParticles particles, float seconds)
    {
        var rig = particles.Rig;
        float halfWeight = 0.5f * ChainMath.RateWeight(LinkRate, seconds);
        bool moved = false;
        for (int index = 0; index < rig.Links.Count; index++)
        {
            var link = rig.Links[index];
            int count = Math.Min(rig.Chains[link.First].Joints.Count, rig.Chains[link.Second].Joints.Count);
            int first = rig.FirstJoint(link.First) + link.First;
            int second = rig.FirstJoint(link.Second) + link.Second;
            // The first pair is anchored and the virtual tips are outside the authored joint lists.
            for (int joint = 1; joint < count; joint++)
            {
                int a = first + joint;
                int b = second + joint;
                var rest = particles.RestPositions[b] - particles.RestPositions[a];
                var offset = particles.Positions[b] - particles.Positions[a];
                float error = offset.Length() - rest.Length();
                var direction = ChainMath.Direction(offset, ChainMath.Direction(rest, Vector3.UnitX));
                var correction = halfWeight * error * direction;
                particles.Positions[a] += correction;
                particles.Positions[b] -= correction;
                moved |= correction != Vector3.Zero;
            }
        }

        return moved;
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
