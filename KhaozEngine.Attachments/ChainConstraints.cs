using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainConstraints
{
    internal readonly record struct JointStep(int Joint, int Particle, Vector3 Start, float Length,
        Matrix4x4 Unswung, Vector3 Rest, float BendCosine, Vector3 Direction, bool Clamped, Vector3 End, bool ReplayedAll, int PlacementCount);

    internal static void FollowChain(ChainParticles particles, int chain, in Matrix4x4 anchor,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        in ChainSettings settings, float seconds, bool pullTowardRest, ChainFirstPassContacts? firstPass = null)
    {
        var rig = particles.Rig;
        int first = rig.FirstJoint(chain);
        int last = first + rig.Chains[chain].Joints.Count - 1;
        float springWeight = pullTowardRest ? ChainMath.MidpointSpringWeight(settings.Stiffness, seconds) : 0;
        if (!colliders.IsEmpty)
            particles.SweepPrediction?.Capture(particles, chain, anchor, springWeight);
        bool reuseCosine = ChainContactOptimizations.Use(ChainContactOptimization.BendCosine);
        float cosine = !pullTowardRest && reuseCosine && firstPass?.BendCosine is { } previousCosine
            ? previousCosine : BendCosine(settings);
        if (pullTowardRest && reuseCosine && firstPass is not null)
            firstPass.BendCosine = cosine;
        var parent = anchor;
        var capsules = FollowCandidates(particles, chain, settings.Radius, colliders, pullTowardRest, firstPass);
        var velocity = new ChainVelocityConstraints(particles, first + chain, particles.StepCarries[chain],
            previousColliders, settings.Radius, seconds);

        for (int joint = first; joint <= last; joint++)
        {
            var step = PlaceJoint(particles, chain, joint, parent, springWeight, cosine, settings.Radius, seconds, capsules);
            if (step.ReplayedAll)
                capsules = capsules.All;
            if (joint == first)
                velocity.Start(rig.Chains[chain].Anchor);
            var placement = particles.PlacementCull.Selected(0, capsules, step.PlacementCount);
            velocity.Apply(step.Particle, step.Length, step.Direction, step.Rest, step.Clamped, placement);
            parent = WriteSwing(particles, step);
        }
    }

    private static ChainCapsuleCandidates FollowCandidates(ChainParticles particles, int chain, float radius,
        ReadOnlySpan<ColliderCapsule> colliders, bool first, ChainFirstPassContacts? firstPass)
    {
        if (firstPass is not null && ChainContactOptimizations.Use(ChainContactOptimization.FirstPassCandidates))
        {
            if (first)
                return firstPass.Select(particles, chain, radius, colliders);
            if (firstPass.TryReuse(particles, chain, radius, colliders.Length, out int count))
            {
                ChainContactCounts.Active?.Record(ChainContactQuery.FirstPassReuses);
                return firstPass.Cull.Selected(colliders, count);
            }
        }

        return SelectCapsules(particles.CapsuleCull, particles, chain, radius, colliders);
    }

    internal static ChainCapsuleCandidates SelectCapsules(ChainCapsuleCull cull, ChainParticles particles,
        int chain, float radius, ReadOnlySpan<ColliderCapsule> colliders)
    {
        int first = particles.Rig.FirstJoint(chain);
        return cull.Select(particles.Positions[first + chain],
            particles.Lengths.AsSpan(first, particles.Rig.Chains[chain].Joints.Count), radius, colliders, particles.CapsuleAxes, particles.PlacementCull);
    }

    internal static float BendCosine(in ChainSettings settings)
    {
        ChainContactCounts.Active?.Record(ChainContactQuery.BendCosines);
        return settings.AngleLimit == MathF.PI ? -1 : MathF.Cos(settings.AngleLimit);
    }

    internal static JointStep PlaceJoint(ChainParticles particles, int chain, int joint, in Matrix4x4 parent,
        float springWeight, float cosine, float radius, float seconds, in ChainCapsuleCandidates capsules, int placementRow = 0)
    {
        var rig = particles.Rig;
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
            var started = particles.SubstepStarts[end] - particles.SubstepStarts[particle];
            var correction = SpringCorrection(springWeight, length, rest, started, predicted);
            particles.ApplySpring(end, correction, seconds);
            predicted += correction;
        }

        var step = new JointStep(joint, particle, start, length, world, rest, cosine, default, false, default, false, capsules.Count);
        return PlaceOnSphere(particles, placementRow, step, predicted, cosine, radius, capsules);
    }

    internal static Vector3 SpringCorrection(float weight, float length, Vector3 rest, Vector3 started, Vector3 predicted) =>
        weight * (length * rest - 0.5f * (started + predicted));

    internal static JointStep ReapplyLimits(ChainParticles particles, int placementRow, in JointStep step,
        float cosine, float radius, in ChainCapsuleCandidates capsules) =>
        PlaceOnSphere(particles, placementRow, step, particles.Positions[step.Particle + 1] - step.Start, cosine, radius, capsules);

    private static JointStep PlaceOnSphere(ChainParticles particles, int placementRow, in JointStep step, Vector3 predicted,
        float cosine, float radius, in ChainCapsuleCandidates capsules)
    {
        var direction = ChainMath.Direction(predicted, step.Rest);
        bool clamped = false, replayedAll = false;
        int placementCount = capsules.Count;
        if (step.Length > 0)
        {
            var limited = ClampBend(direction, step.Rest, cosine);
            clamped = limited != direction;
            direction = limited;
            direction = ChainSegmentContact.Clear(particles, placementRow, step.Particle, direction, step.Length, radius,
                capsules, out replayedAll, out placementCount, step.Particle - step.Joint, step.Rest, cosine);
        }

        var end = step.Start + step.Length * direction;
        particles.Positions[step.Particle + 1] = end;
        return new JointStep(step.Joint, step.Particle, step.Start, step.Length, step.Unswung, step.Rest,
            cosine, direction, clamped, end, replayedAll, placementCount);
    }

    internal static Matrix4x4 WriteSwing(ChainParticles particles, in JointStep step)
    {
        var swing = step.Length > 0 ? ChainMath.Swing(step.Rest, step.Direction) : Quaternion.Identity;
        var world = step.Unswung * Matrix4x4.CreateFromQuaternion(swing);
        world.Translation = step.Start;
        particles.JointWorlds[step.Joint] = world;
        particles.JointSwings[step.Joint] = swing;
        return world;
    }

    internal static Vector3 ClampBend(Vector3 direction, Vector3 rest, float cosine)
    {
        if (Vector3.Dot(direction, rest) >= cosine)
            return direction;
        var tangent = ChainMath.Tangent(direction, rest);
        if (tangent == Vector3.Zero)
            tangent = ChainMath.Perpendicular(rest);
        return cosine * rest + MathF.Sqrt(1 - cosine * cosine) * tangent;
    }
}
