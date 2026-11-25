using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;
using static KhaozEngine.Attachments.ChainSegmentContact;

namespace KhaozEngine.Attachments;

// Look for matching material points as the closest feature moves along the
// unprojected segment and obstacle. Solver corrections never enter this sweep.
internal struct ChainSweepWitness
{
    private const int IntermediateQueries = 12;
    private Offset _start, _end, _startMove, _endMove, _segment, _segmentMove;

    // Write the witness in the caller's storage so construction needs no struct copy.
    internal static void Create(in Segment opening, in Segment prediction, in Axis previous, in Axis current,
        out ChainSweepWitness witness)
    {
        witness._start = previous.Start - opening.Root;
        witness._end = previous.End - opening.Root;
        witness._startMove = (current.Start - prediction.Root) - (previous.Start - opening.Root);
        witness._endMove = (current.End - prediction.Root) - (previous.End - opening.Root);
        witness._segment = opening.Displacement;
        witness._segmentMove = prediction.Displacement - opening.Displacement;
    }

    internal readonly Vector3 Find(double radius, double closingFraction, double closingAlong, ChainContactCounts? counts)
    {
        var side = AtFeature(closingFraction, closingAlong, radius);
        if (side != Vector3.Zero)
            return side;

        if (_segmentMove.LengthSquared == 0 && _start.SameBits(_end) && _startMove.SameBits(_endMove))
        {
            // In the parent's frame a point sweeps a line against a fixed segment.
            // The closest pair yields an interior material fraction without sampling.
            var path = new Axis(_start, _start + _startMove);
            counts?.Record(ChainContactQuery.SegmentOpening);
            var residual = path.ClosestResidual(new Segment(default, _segment), out double fraction, out _);
            return residual.LengthSquared < radius * radius ? AtFeature(fraction, 0, radius) : Vector3.Zero;
        }

        double relativeTravel = Math.Sqrt(_segmentMove.LengthSquared) +
            Math.Sqrt(Math.Max(_startMove.LengthSquared, _endMove.LengthSquared));
        int budget = IntermediateQueries;
        return Search(0, 1, radius, relativeTravel, ref budget, counts);
    }

    private readonly Vector3 Search(double start, double end, double radius, double relativeTravel, ref int budget,
        ChainContactCounts? counts)
    {
        if (budget == 0)
            return Vector3.Zero;

        budget--;
        double middle = 0.5 * (start + end);
        var axis = new Axis(_start + _startMove * middle, _end + _endMove * middle);
        var segment = new Segment(default, _segment + _segmentMove * middle);
        counts?.Record(ChainContactQuery.SegmentOpening);
        var residual = axis.ClosestResidual(segment, out double fraction, out double along);

        // No material pair can close a gap larger than its remaining travel.
        double reach = radius + (end - middle) * relativeTravel;
        if (residual.LengthSquared >= reach * reach)
            return Vector3.Zero;

        var side = AtFeature(fraction, along, radius);
        if (side != Vector3.Zero)
            return side;

        side = Search(start, middle, radius, relativeTravel, ref budget, counts);
        return side != Vector3.Zero ? side : Search(middle, end, radius, relativeTravel, ref budget, counts);
    }

    internal readonly Vector3 AtFeature(double fraction, double along, double radius)
    {
        var opening = _segment * fraction - (_start + (_end - _start) * along);
        var motion = _segmentMove * fraction - (_startMove + (_endMove - _startMove) * along);
        var closing = opening + motion;
        if (Offset.Dot(opening, closing) >= 0)
            return Vector3.Zero;

        // Opposite residuals put the chord's nearest point strictly between its ends.
        var closest = opening - motion * (Offset.Dot(opening, motion) / motion.LengthSquared);
        return closest.LengthSquared < radius * radius ? (opening * (1 / Math.Sqrt(opening.LengthSquared))).ToVector3() :
            Vector3.Zero;
    }
}
