using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// One placement can revisit a capsule as the segment turns, but its opening residual stays fixed.
internal readonly ref struct ChainOpeningContact
{
    private readonly ChainSegmentContact.Segment _segment;
    private readonly ReadOnlySpan<Axis> _axes;
    private readonly Span<Offset?> _residuals;

    internal ChainOpeningContact(ChainParticles particles, int parent, int obstacleCount)
    {
        var root = new Offset(particles.OpeningPositions[parent]);
        var tip = new Offset(particles.OpeningPositions[parent + 1]);
        _segment = new ChainSegmentContact.Segment(root, tip - root);
        _axes = particles.PreviousCapsuleAxes;
        _residuals = particles.OpeningResiduals.AsSpan(0, obstacleCount);
        _residuals.Clear();
    }

    internal bool Crossed(int index, Offset current, double radiusSquared, out Vector3 side)
    {
        var opening = _residuals[index] ??= _axes[index].ClosestResidual(_segment, out _, out _);
        bool crossed = Crossed(opening, current, radiusSquared);
        side = crossed ? (opening * (1 / Math.Sqrt(opening.LengthSquared))).ToVector3() : Vector3.Zero;
        return crossed;
    }

    internal static bool Crossed(Offset opening, Offset current, double radiusSquared)
    {
        if (Offset.Dot(opening, current) >= 0)
            return false;

        // Opposite residuals put the chord's nearest point strictly between its ends.
        var chord = current - opening;
        var closest = opening - chord * (Offset.Dot(opening, chord) / chord.LengthSquared);
        return closest.LengthSquared < radiusSquared;
    }
}
