using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainConstraints
{
    internal readonly record struct JointStep(int Joint, int Particle, Vector3 Start, float Length,
        Matrix4x4 Unswung, Vector3 Rest, Vector3 Direction, bool Clamped, Vector3 End);

    internal static void FollowChain(ChainParticles particles, int chain, in Matrix4x4 anchor,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        in ChainSettings settings, float seconds, bool pullTowardRest)
    {
        var rig = particles.Rig;
        int first = rig.FirstJoint(chain);
        int last = first + rig.Chains[chain].Joints.Count - 1;
        float springWeight = pullTowardRest ? ChainMath.MidpointSpringWeight(settings.Stiffness, seconds) : 0;
        float cosine = BendCosine(settings);
        var parent = anchor;
        var velocity = new ChainVelocityConstraints(particles, first + chain, particles.StepCarries[chain],
            colliders, previousColliders, settings.Radius, seconds);

        for (int joint = first; joint <= last; joint++)
        {
            var step = PlaceJoint(particles, chain, joint, parent, springWeight, cosine, settings.Radius, seconds, colliders);
            if (joint == first)
                velocity.Start(particles.PreviousRestDirections[joint], step.Rest);
            velocity.Apply(step.Particle, step.Length, step.Direction, step.Rest, step.Clamped);
            parent = WriteSwing(particles, step);
        }
    }

    internal static float BendCosine(in ChainSettings settings) =>
        settings.AngleLimit == MathF.PI ? -1 : MathF.Cos(settings.AngleLimit);

    internal static JointStep PlaceJoint(ChainParticles particles, int chain, int joint, in Matrix4x4 parent,
        float springWeight, float cosine, float radius, float seconds, ReadOnlySpan<ColliderCapsule> colliders)
    {
        var rig = particles.Rig;
        int particle = joint + chain;
        var start = particles.Positions[particle];
        float length = particles.Lengths[joint];
        var world = rig.RestLocal(joint) * parent;
        var restOffset = Vector3.TransformNormal(rig.SegmentOffset(joint), world);
        var rest = ChainMath.Direction(restOffset, Vector3.UnitY);
        particles.StepRestDirections[joint] = rest;

        int end = particle + 1;
        var predicted = particles.Positions[end] - start;
        if (springWeight > 0)
        {
            var started = particles.SubstepStarts[end] - particles.SubstepStarts[particle];
            var correction = springWeight * (length * rest - 0.5f * (started + predicted));
            particles.ApplySpring(end, correction, seconds);
            predicted += correction;
        }

        var step = new JointStep(joint, particle, start, length, world, rest, default, false, default);
        return PlaceOnSphere(particles, step, predicted, cosine, radius, colliders);
    }

    internal static JointStep ReapplyLimits(ChainParticles particles, in JointStep step,
        float cosine, float radius, ReadOnlySpan<ColliderCapsule> colliders) =>
        PlaceOnSphere(particles, step, particles.Positions[step.Particle + 1] - step.Start, cosine, radius, colliders);

    private static JointStep PlaceOnSphere(ChainParticles particles, in JointStep step, Vector3 predicted,
        float cosine, float radius, ReadOnlySpan<ColliderCapsule> colliders)
    {
        var direction = ChainMath.Direction(predicted, step.Rest);
        bool clamped = false;
        if (step.Length > 0)
        {
            var limited = ClampBend(direction, step.Rest, cosine);
            clamped = limited != direction;
            direction = limited;
            direction = ChainCapsuleCollision.Clear(direction, step.Start, step.Length, radius, colliders);
        }

        var end = step.Start + step.Length * direction;
        particles.Positions[step.Particle + 1] = end;
        return step with { Direction = direction, Clamped = clamped, End = end };
    }

    internal static Matrix4x4 WriteSwing(ChainParticles particles, in JointStep step)
    {
        var swing = step.Length > 0 ? ChainMath.Swing(step.Rest, step.Direction) : Quaternion.Identity;
        var world = step.Unswung * Matrix4x4.CreateFromQuaternion(swing);
        world.Translation = step.Start;
        particles.JointWorlds[step.Joint] = world;
        return world;
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
