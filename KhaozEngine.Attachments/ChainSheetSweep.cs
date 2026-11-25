using System;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// A witness uses the same triangle weights and capsule-axis fraction at both
// ends of the unprojected step. A changing closest feature alone is not a crossing.
internal readonly struct ChainSheetSweep(
    ChainContactTriangle opening, ChainContactTriangle prediction, Axis previous, Axis current)
{
    private const int IntermediateQueries = 8;

    internal double FindSide(in ChainContactTriangle.Contact firstHit, Offset openingNormal, double openingRadius,
        double radius, double tolerance)
    {
        // The position solver leaves a touch margin at rest. A shallow
        // contact still has an opening side when the obstacle moves again.
        double required = Math.Max(0, openingRadius - TouchTolerance);
        if (openingNormal.LengthSquared == 0 || firstHit.Residual.LengthSquared < required * required)
            return 0;

        var lastNormal = prediction.Normal;
        int firstSide = opening.AxisSide(previous, openingNormal, tolerance);
        if (firstSide != 0 && firstSide == prediction.AxisSide(current, lastNormal, tolerance))
            return 0;

        double travel = opening.RelativeTravel(prediction, previous, current);
        double reach = radius + travel;
        if (firstHit.Residual.LengthSquared > reach * reach)
            return 0;

        var side = AtFeature(firstHit, openingNormal, lastNormal, radius, tolerance);
        if (side != 0)
            return side;

        var diagonal = default(ChainContactTriangle.Diagonal);
        ChainContactCounts.Active?.Record(ChainContactQuery.SheetOpening);
        var lastHit = prediction.Closest(current, false, ref diagonal, out _);
        side = AtFeature(lastHit, openingNormal, lastNormal, radius, tolerance);
        if (side != 0)
            return side;

        int budget = IntermediateQueries;
        return Search(0, 1, travel, openingNormal, lastNormal, radius, tolerance, ref budget);
    }

    private double Search(double start, double end, double travel, Offset firstNormal, Offset lastNormal,
        double radius, double tolerance, ref int budget)
    {
        if (budget == 0)
            return 0;

        budget--;
        double middle = 0.5 * (start + end);
        var axis = new Axis(previous.Start + (current.Start - previous.Start) * middle,
            previous.End + (current.End - previous.End) * middle);
        var triangle = opening.Interpolate(prediction, middle);
        var diagonal = default(ChainContactTriangle.Diagonal);
        ChainContactCounts.Active?.Record(ChainContactQuery.SheetOpening);
        var hit = triangle.Closest(axis, false, ref diagonal, out _);
        double reach = radius + (end - middle) * travel;
        if (hit.Residual.LengthSquared > reach * reach)
            return 0;

        double side = AtFeature(hit, firstNormal, lastNormal, radius, tolerance);
        if (side != 0)
            return side;

        side = Search(start, middle, travel, firstNormal, lastNormal, radius, tolerance, ref budget);
        return side != 0 ? side : Search(middle, end, travel, firstNormal, lastNormal, radius, tolerance, ref budget);
    }

    private double AtFeature(in ChainContactTriangle.Contact hit, Offset firstNormal, Offset lastNormal,
        double radius, double tolerance)
    {
        var first = opening.MaterialResidual(previous, hit.Weights, hit.Along);
        var last = prediction.MaterialResidual(current, hit.Weights, hit.Along);
        double side = Offset.Dot(firstNormal, first);
        if (Math.Abs(side) <= tolerance || side * Offset.Dot(lastNormal, last) >= 0)
            return 0;

        var travel = last - first;
        double squared = travel.LengthSquared;
        if (squared == 0)
            return 0;

        // The closest point of the relative material chord must be within the
        // inflated capsule. This lets a capsule change sides around a free edge.
        double time = Math.Clamp(-Offset.Dot(first, travel) / squared, 0, 1);
        return (first + travel * time).LengthSquared < radius * radius ? Math.Sign(side) : 0;
    }
}
