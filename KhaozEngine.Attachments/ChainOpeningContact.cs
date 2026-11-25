using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// A placement can revisit a capsule as the segment turns. Its swept side stays fixed.
internal readonly ref struct ChainOpeningContact
{
    private readonly ChainSegmentContact.Segment _segment;
    private readonly ChainSegmentContact.Segment? _prediction;
    private readonly double _predictionTravel;
    private readonly ReadOnlySpan<Axis> _axes;
    private readonly ReadOnlySpan<float> _radii;
    private readonly Span<Vector3?> _sides;
    private readonly Span<int> _stamps;
    private readonly int _stamp;

    internal ChainSegmentContact.Segment Segment => _segment;
    internal ChainSegmentContact.Segment Prediction => _prediction ?? _segment;

    internal ChainOpeningContact(ChainParticles particles, int chain, int parent, int obstacleCount)
    {
        var root = new Offset(particles.OpeningPositions[parent]);
        var tip = new Offset(particles.OpeningPositions[parent + 1]);
        _segment = new ChainSegmentContact.Segment(root, tip - root);
        _prediction = particles.SweepPrediction?.Read(chain, parent);
        if (_prediction is { } prediction)
        {
            var startMove = prediction.Root - root;
            var endMove = startMove + prediction.Displacement - _segment.Displacement;
            _predictionTravel = Math.Sqrt(Math.Max(startMove.LengthSquared, endMove.LengthSquared));
        }
        _axes = particles.PreviousCapsuleAxes;
        _radii = particles.PreviousCapsuleRadii;
        _sides = particles.OpeningSides.AsSpan(0, obstacleCount);
        _stamps = particles.OpeningStamps.AsSpan(0, obstacleCount);
        if (ChainContactOptimizations.Use(ChainContactOptimization.OpeningStamp))
            _stamp = particles.NextOpeningStamp();
        else
        {
            _stamp = 0;
            _sides.Clear();
            ChainContactCounts.Record(ChainContactQuery.OpeningSlotsCleared, obstacleCount);
        }
    }

    internal Vector3 Side(int index, in Axis current, double radius, float chainRadius)
    {
        if (_prediction is not { } prediction)
            return Vector3.Zero;

        if (_stamp == 0 ? !_sides[index].HasValue : _stamps[index] != _stamp)
        {
            _sides[index] = FindSide(index, current, prediction, radius, chainRadius);
            _stamps[index] = _stamp;
        }

        return _sides[index].GetValueOrDefault();
    }

    private Vector3 FindSide(int index, in Axis current, in ChainSegmentContact.Segment prediction,
        double radius, float chainRadius)
    {
        ref readonly var previous = ref _axes[index];
        double openingRadius = (double)_radii[index] + chainRadius;
        double axisTravel = Math.Sqrt(Math.Max((current.Start - previous.Start).LengthSquared,
            (current.End - previous.End).LengthSquared));

        // Relative material travel must exceed the opening clearance for its
        // residual to enter the opposite hemisphere at all.
        if (axisTravel + _predictionTravel <= openingRadius)
            return Vector3.Zero;

        ChainContactCounts.Record(ChainContactQuery.SegmentOpening);
        var opening = previous.ClosestResidual(_segment, out double fraction, out double along);
        if (opening.LengthSquared < openingRadius * openingRadius)
            return Vector3.Zero;

        double radiusSquared = radius * radius;
        var matchingPrediction = MaterialResidual(prediction, current, fraction, along);
        if (Crossed(opening, matchingPrediction, radiusSquared))
            return Unit(opening);

        // A different feature can be closest at the other end of the motion.
        // Test that feature against its own opening material point as well.
        ChainContactCounts.Record(ChainContactQuery.SegmentOpening);
        var predicted = current.ClosestResidual(prediction, out fraction, out along);
        var matchingOpening = MaterialResidual(_segment, previous, fraction, along);
        return Crossed(matchingOpening, predicted, radiusSquared) ? Unit(matchingOpening) : Vector3.Zero;
    }

    private static Offset MaterialResidual(in ChainSegmentContact.Segment segment, in Axis axis, double fraction, double along) =>
        segment.Root - axis.Start + segment.Displacement * fraction - axis.Direction * along;

    private static Vector3 Unit(Offset direction) => (direction * (1 / Math.Sqrt(direction.LengthSquared))).ToVector3();

    private static bool Crossed(Offset opening, Offset current, double radiusSquared)
    {
        if (Offset.Dot(opening, current) >= 0)
            return false;

        // Opposite residuals put the chord's nearest point strictly between its ends.
        var chord = current - opening;
        var closest = opening - chord * (Offset.Dot(opening, chord) / chord.LengthSquared);
        return closest.LengthSquared < radiusSquared;
    }
}
