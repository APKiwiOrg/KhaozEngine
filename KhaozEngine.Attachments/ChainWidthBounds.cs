using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainWidthBounds(ChainWidthBounds.Pair[] pairs)
{
    private const int MaximumSweeps = 8;
    private const float PositionTolerance = 0.000001f;
    private const float SweepTolerance = 0.0005f;
    private const float VelocityTolerance = 0.0005f;
    private readonly (float Minimum, float Maximum)[] _bands = new (float, float)[pairs.Length];

    internal readonly record struct Member(int Chain, int Particle, int Root)
    {
        internal bool Anchored => Particle == Root;
    }

    internal readonly record struct Pair(Member First, Member Second, float Stretch)
    {
        internal int Depth => Math.Max(First.Particle - First.Root, Second.Particle - Second.Root);
    }

    internal void ProjectPositions(ChainParticles particles)
    {
        for (int index = 0; index < pairs.Length; index++)
        {
            var pair = pairs[index];
            float rest = Vector3.Distance(particles.RestPositions[pair.First.Particle], particles.RestPositions[pair.Second.Particle]);
            _bands[index] = (rest * (1 - pair.Stretch), rest * (1 + pair.Stretch));
        }

        for (int sweeps = 0; sweeps < MaximumSweeps; sweeps++)
        {
            // Later sweeps tolerate a small residual from pairs that share a particle.
            // Use the same margin for checking and projecting so a sweep starts without a snap.
            float margin = sweeps == 0 ? PositionTolerance : SweepTolerance;
            if (!NeedsSweep(particles, margin))
                break;

            bool forward = (sweeps & 1) == 0;
            int direction = forward ? 1 : -1;
            int stop = forward ? pairs.Length : -1;
            for (int index = forward ? 0 : pairs.Length - 1; index != stop; index += direction)
                Constrain(particles, pairs[index], _bands[index], margin);
        }
    }

    private bool NeedsSweep(ChainParticles particles, float margin)
    {
        for (int index = 0; index < pairs.Length; index++)
        {
            var pair = pairs[index];
            float squared = Vector3.DistanceSquared(particles.Positions[pair.First.Particle], particles.Positions[pair.Second.Particle]);
            var (minimum, maximum) = _bands[index];
            float lower = MathF.Max(0, minimum - margin);
            float upper = maximum + margin;
            if (squared < lower * lower || squared > upper * upper)
                return true;
        }
        return false;
    }

    private static void Constrain(ChainParticles particles, in Pair pair, (float Minimum, float Maximum) band, float margin)
    {
        int first = pair.First.Particle;
        int second = pair.Second.Particle;
        var offset = particles.Positions[second] - particles.Positions[first];
        float spacing = offset.Length();
        float bound = Math.Clamp(spacing, band.Minimum, band.Maximum);
        float excess = spacing - bound;
        float violation = MathF.Abs(excess);
        if (violation <= margin)
            return;

        // Ease into a sweep at its margin and reach the authored band at twice that error.
        // Project inside the margin so rounding and shared pairs do not stall on its edge.
        bound += MathF.CopySign(MathF.Max(0, 2 * margin - violation), excess);

        var rest = particles.RestPositions[second] - particles.RestPositions[first];
        var axis = ChainMath.Direction(offset, ChainMath.Direction(rest, Vector3.UnitX));
        var left = new SphereMove(particles, pair.First, 0.5f * axis, axis);
        // Coincident radial endpoints must swing to opposite sides to create a gap.
        var right = new SphereMove(particles, pair.Second, -0.5f * axis, spacing == 0 ? -axis : axis);
        if (left.Tangent == Vector3.Zero && right.Tangent == Vector3.Zero)
            return;

        float leverage = Vector3.Dot(left.Tangent - right.Tangent, axis);
        float maximum = MathF.Min(left.MaximumTravel, right.MaximumTravel);
        float lower = spacing > bound ? 0 : -maximum;
        float upper = spacing > bound ? maximum : 0;
        // Collinear segments have no first-order leverage. Try a finite swing on their common side.
        float travel = leverage > 0 ? Math.Clamp((spacing - bound) / leverage, lower, upper)
            : spacing > bound ? maximum : -maximum;
        float bestError = MathF.Abs(spacing - bound);
        var bestLeft = particles.Positions[first];
        var bestRight = particles.Positions[second];

        // Refine the linearized tangent move on the two actual length spheres.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var (a, aSlope) = left.At(travel);
            var (b, bSlope) = right.At(travel);
            var delta = b - a;
            float distance = delta.Length();
            float error = distance - bound;
            if (MathF.Abs(error) < bestError)
            {
                bestError = MathF.Abs(error);
                (bestLeft, bestRight) = (a, b);
            }
            if (bestError <= PositionTolerance || distance == 0)
                break;

            leverage = Vector3.Dot(aSlope - bSlope, delta / distance);
            if (leverage <= 0)
                break;
            float next = Math.Clamp(travel + error / leverage, lower, upper);
            if (next == travel)
                break;
            travel = next;
        }

        particles.Positions[first] = bestLeft;
        particles.Positions[second] = bestRight;
    }

    private readonly struct SphereMove
    {
        private readonly Vector3 _point;
        private readonly Vector3 _parent;
        private readonly Vector3 _offset;
        private readonly float _length;

        internal SphereMove(ChainParticles particles, in Member member, Vector3 share, Vector3 pairAxis)
        {
            _point = particles.Positions[member.Particle];
            if (member.Anchored)
                return;

            _parent = particles.Positions[member.Particle - 1];
            _offset = _point - _parent;
            _length = particles.Lengths[member.Particle - member.Chain - 1];
            if (_length == 0)
                return;

            var direction = ChainMath.Direction(_offset, Vector3.Zero);
            Tangent = Vector3.Cross(direction, Vector3.Cross(share, direction));
            if (Tangent.LengthSquared() < 1e-12f)
                Tangent = 0.5f * ChainMath.Tangent(ChainMath.Perpendicular(pairAxis), direction);
        }

        internal Vector3 Tangent { get; }
        internal float MaximumTravel => Tangent == Vector3.Zero ? float.PositiveInfinity : _length / Tangent.Length();

        internal (Vector3 Point, Vector3 Slope) At(float travel)
        {
            if (Tangent == Vector3.Zero)
                return (_point, Vector3.Zero);

            var offset = _offset + travel * Tangent;
            float length = offset.Length();
            var direction = offset / length;
            var slope = (Tangent - Vector3.Dot(Tangent, direction) * direction) * (_length / length);
            return (_parent + _length * direction, slope);
        }
    }

    internal void StopVelocities(ChainParticles particles, float seconds)
    {
        for (int index = 0; index < pairs.Length; index++)
            StopAtBand(particles, index, seconds);
        for (int index = pairs.Length - 1; index >= 0; index--)
            StopAtBand(particles, index, seconds);
    }

    private void StopAtBand(ChainParticles particles, int index, float seconds)
    {
        var pair = pairs[index];
        var offset = particles.Positions[pair.Second.Particle] - particles.Positions[pair.First.Particle];
        float spacing = offset.Length();
        var (minimum, maximum) = _bands[index];
        bool stretching = spacing - minimum >= maximum - spacing;
        if (stretching ? spacing < maximum - VelocityTolerance : spacing > minimum + VelocityTolerance)
            return;

        var rest = particles.RestPositions[pair.Second.Particle] - particles.RestPositions[pair.First.Particle];
        var axis = ChainMath.Direction(offset, ChainMath.Direction(rest, Vector3.UnitX));
        StopEscape(particles, pair, axis, seconds, stretching);
    }

    private static void StopEscape(ChainParticles particles, in Pair pair, Vector3 axis, float seconds, bool stretching)
    {
        var relative = WorldVelocity(particles, pair.Second, seconds) - WorldVelocity(particles, pair.First, seconds);
        float separating = Vector3.Dot(relative, axis);
        if (stretching ? separating <= 0 : separating >= 0)
            return;

        var correction = separating * axis;
        if (!pair.First.Anchored)
            particles.Velocities[pair.First.Particle] += (pair.Second.Anchored ? 1 : 0.5f) * correction;
        if (!pair.Second.Anchored)
            particles.Velocities[pair.Second.Particle] -= (pair.First.Anchored ? 1 : 0.5f) * correction;
    }

    private static Vector3 WorldVelocity(ChainParticles particles, in Member member, float seconds)
    {
        var carry = particles.StepCarries[member.Chain];
        var displacement = carry.Offset;
        if (carry.Turn != Quaternion.Identity)
        {
            var after = particles.SubstepStarts[member.Particle] - (carry.Pivot + carry.Offset);
            displacement += after - Vector3.Transform(after, Quaternion.Conjugate(carry.Turn));
        }

        // Stored velocities already use world axes, with the carried displacement removed.
        return particles.Velocities[member.Particle] + displacement / seconds;
    }
}
