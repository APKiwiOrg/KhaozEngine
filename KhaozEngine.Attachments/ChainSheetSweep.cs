using System;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// A witness uses the same triangle weights and capsule-axis fraction at both
// ends of the unprojected step. A changing closest feature alone is not a crossing.
internal readonly struct ChainSheetSweep(
    ChainContactTriangle opening, ChainContactTriangle prediction, Axis previous, Axis current)
{
    private const int IntermediateQueries = 8;

    internal bool CanCross(double tolerance, out Offset openingNormal, out Offset lastNormal)
    {
        openingNormal = opening.Normal;
        lastNormal = prediction.Normal;
        if (openingNormal.LengthSquared == 0)
            return false;

        int firstSide = opening.AxisSide(previous, openingNormal, tolerance);
        return firstSide == 0 || firstSide != prediction.AxisSide(current, lastNormal, tolerance);
    }

    internal double FindSide(in ChainContactTriangle.Contact firstHit, Offset openingNormal, Offset lastNormal,
        double openingRadius, double radius, double tolerance)
    {
        // The position solver leaves a touch margin at rest. A shallow
        // contact still has an opening side when the obstacle moves again.
        double required = Math.Max(0, openingRadius - TouchTolerance);
        if (firstHit.Residual.LengthSquared < required * required)
            return 0;

        double travel = opening.RelativeTravel(prediction, previous, current);
        double reach = radius + travel;
        if (firstHit.Residual.LengthSquared > reach * reach)
            return 0;

        var side = AtFeature(firstHit, openingNormal, lastNormal, radius, tolerance);
        if (side != 0)
            return side;

        ChainContactCounts.Active?.Record(ChainContactQuery.SheetOpening);
        var lastHit = prediction.Closest(current, out _);
        side = AtFeature(lastHit, openingNormal, lastNormal, radius, tolerance);
        if (side != 0)
            return side;

        if (previous.IsPoint && current.IsPoint && opening.TryTranslationTo(prediction, out var translation))
        {
            // In the triangle frame this is a point moving on a straight axis.
            // Its nearest triangle feature is an exact witness for that path.
            var path = new Axis(previous.Start, current.Start - translation);
            ChainContactCounts.Active?.Record(ChainContactQuery.SheetOpening);
            var hit = opening.Closest(path, out _);
            return AtFeature(hit with { Along = 0 }, openingNormal, lastNormal, radius, tolerance);
        }

        double start = 0, end = 1;
        if (!ClipTime(new Offset(1, 0, 0), radius, ref start, ref end) ||
            !ClipTime(new Offset(0, 1, 0), radius, ref start, ref end) ||
            !ClipTime(new Offset(0, 0, 1), radius, ref start, ref end) ||
            !ClipTime(openingNormal, radius, ref start, ref end) ||
            !ClipTime(lastNormal, radius, ref start, ref end))
            return 0;

        return Search(start, end, travel, openingNormal, lastNormal, radius, tolerance);
    }

    private bool ClipTime(Offset direction, double radius, ref double start, ref double end)
    {
        var first = opening.Projection(direction, previous.Start);
        var last = prediction.Projection(direction, current.Start);
        double firstAxis = Offset.Dot(direction, previous.Direction);
        double lastAxis = Offset.Dot(direction, current.Direction);
        // Minima are concave and maxima convex in time. Their endpoint
        // chords enclose the moving triangle and capsule along this axis.
        return ClipGap(first.Min - Math.Max(0, firstAxis), last.Min - Math.Max(0, lastAxis), radius, ref start, ref end) &&
            ClipGap(Math.Min(0, firstAxis) - first.Max, Math.Min(0, lastAxis) - last.Max, radius, ref start, ref end);
    }

    private static bool ClipGap(double first, double last, double radius, ref double start, ref double end)
    {
        // Pad the contact radius before clipping so rounding at an endpoint
        // or division near the window boundary cannot discard a witness.
        double padded = radius + 1e-12 * (1 + Math.Max(Math.Abs(first), Math.Abs(last)));
        first -= padded;
        last -= padded;
        if (first > 0)
        {
            if (last > 0)
                return false;
            start = Math.Max(start, first / (first - last));
        }
        else if (last > 0)
        {
            end = Math.Min(end, first / (first - last));
        }

        return start <= end;
    }

    private double Search(double start, double end, double travel, Offset firstNormal, Offset lastNormal,
        double radius, double tolerance)
    {
        Span<(double Start, double End)> pending = stackalloc (double, double)[1 + 2 * IntermediateQueries];
        pending[0] = (start, end);
        int pendingCount = 1;
        // Narrow contact can be near either end of the window. Refine both
        // halves before spending further queries on an earlier half.
        for (int read = 0; read < pendingCount && read < IntermediateQueries; read++)
        {
            (start, end) = pending[read];
            double middle = 0.5 * (start + end);
            var axis = new Axis(previous.Start + (current.Start - previous.Start) * middle,
                previous.End + (current.End - previous.End) * middle);
            var triangle = opening.Interpolate(prediction, middle);
            ChainContactCounts.Active?.Record(ChainContactQuery.SheetOpening);
            var hit = triangle.Closest(axis, out _);
            double reach = radius + (end - middle) * travel;
            if (hit.Residual.LengthSquared > reach * reach)
                continue;

            double side = AtFeature(hit, firstNormal, lastNormal, radius, tolerance);
            if (side != 0)
                return side;
            if (middle == start || middle == end)
                continue;

            pending[pendingCount++] = (start, middle);
            pending[pendingCount++] = (middle, end);
        }
        return 0;
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
