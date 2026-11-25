using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// An impossible band must yield to the held parents, local bends and contact.
// Use support points on the actual length spheres rather than trying to pull
// the whole infeasible error through the width lever.
internal static class ChainWidthReach
{
    private const float PositionTolerance = 0.000001f;
    private const float MaximumTurn = 0.25f;
    private static readonly float MaximumTurnCosine = MathF.Cos(MaximumTurn);

    internal static bool ProjectIfOutsideReach(ChainParticles particles, in ChainWidthBounds.Pair pair,
        (float Minimum, float Maximum) band, ReadOnlySpan<ChainConstraints.JointStep> placed,
        ReadOnlySpan<ColliderCapsule> obstacles, ReadOnlySpan<ChainSettings> settings)
    {
        var first = new Corner(particles, pair.First, placed, settings);
        var second = new Corner(particles, pair.Second, placed, settings);
        var restLine = second.RestPoint - first.RestPoint;
        var parents = second.Parent - first.Parent;
        var separation = second.Point - first.Point;
        var axis = ChainMath.Direction(separation, ChainMath.Direction(restLine, Vector3.UnitX));

        float minimum = MathF.Max(parents.Length() - first.Length - second.Length,
            MinimumAlong(first, second, axis));
        var restAxis = ChainMath.Direction(restLine, axis);
        minimum = MathF.Max(minimum, MinimumAlong(first, second, restAxis));
        float maximum = MathF.Min(parents.Length() + first.Length + second.Length,
            restLine.Length() + first.RestTravel + second.RestTravel);

        bool tooWide = minimum > band.Maximum + PositionTolerance;
        if (!tooWide && maximum >= band.Minimum - PositionTolerance)
            return false;

        if (!tooWide)
            axis = -axis;

        var left = first.Offer(particles, obstacles, axis);
        var right = second.Offer(particles, obstacles, -axis);
        float initial = separation.LengthSquared();
        float candidate = Vector3.DistanceSquared(left, right);
        if (tooWide ? candidate < initial : candidate > initial)
        {
            particles.Positions[pair.First.Particle] = left;
            particles.Positions[pair.Second.Particle] = right;
        }
        return true;
    }

    private static float MinimumAlong(in Corner first, in Corner second, Vector3 axis) =>
        Vector3.Dot(second.Support(-axis) - first.Support(axis), axis);

    private readonly struct Corner
    {
        private readonly Vector3 _direction, _rest;
        private readonly float _bendCosine, _radius;
        private readonly bool _held;
        internal readonly Vector3 Point, Parent;
        internal readonly float Length;

        internal Corner(ChainParticles particles, in ChainWidthBounds.Member member,
            ReadOnlySpan<ChainConstraints.JointStep> placed, ReadOnlySpan<ChainSettings> settings)
        {
            Point = particles.Positions[member.Particle];
            Parent = Point;
            if (member.Anchored)
            {
                _held = true;
                return;
            }

            int joint = member.Particle - member.Chain - 1;
            var config = settings.IsEmpty ? particles.Rig.Chains[member.Chain].Settings : settings[member.Chain];
            _radius = config.Radius;
            _bendCosine = placed.IsEmpty ? (config.AngleLimit == MathF.PI ? -1 : MathF.Cos(config.AngleLimit)) :
                placed[member.Chain].BendCosine;
            Length = particles.Lengths[joint];
            Parent = particles.Positions[member.Particle - 1];
            _rest = placed.IsEmpty
                ? ChainMath.Direction(Vector3.TransformNormal(particles.Rig.SegmentOffset(joint), particles.JointWorlds[joint]), Vector3.UnitY)
                : placed[member.Chain].Rest;
            _direction = ChainMath.Direction(Point - Parent, _rest);
            // An already-cleared solid can legitimately hold this corner past the
            // bend wall. Do not drag that accepted shape back through the solid.
            _held = Length <= 0 || Vector3.Dot(_direction, _rest) < _bendCosine - 0.000001f;
        }

        internal Vector3 RestPoint => _held ? Point : Parent + Length * _rest;
        internal float RestTravel => _held ? 0 : Length * MathF.Sqrt(2 * (1 - _bendCosine));

        internal Vector3 Support(Vector3 direction) => _held ? Point :
            Parent + Length * ChainConstraints.ClampBend(direction, _rest, _bendCosine);

        internal Vector3 Offer(ChainParticles particles, ReadOnlySpan<ColliderCapsule> obstacles, Vector3 axis)
        {
            if (_held)
                return Point;

            var target = ChainConstraints.ClampBend(axis, _rest, _bendCosine);
            target = ChainConstraints.ClampBend(target, _direction, MaximumTurnCosine);
            target = ChainConstraints.ClampBend(target, _rest, _bendCosine);
            return ChainWidthClearance.Clip(particles, obstacles, Parent, Point, Parent + Length * target, Length, _radius);
        }
    }
}
