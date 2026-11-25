using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;
using static KhaozEngine.Attachments.ChainSheetWorkspace;

namespace KhaozEngine.Attachments;

internal static class ChainThickSheetClearance
{
    internal static double HalfGapSquared(in Corners corners) =>
        0.25 * Math.Max((new Offset(corners.SecondUpper) - new Offset(corners.FirstUpper)).LengthSquared,
            (new Offset(corners.SecondLower) - new Offset(corners.FirstLower)).LengthSquared);

    internal static bool GapEligible(double halfGapSquared, float bandRadius, float obstacleRadius)
    {
        double inflated = (double)obstacleRadius + bandRadius;
        double halfThickness = 0.5 * bandRadius;
        // Equivalent to R >= w/2 and R - sqrt(R² - w²/4) <= halfThickness.
        return bandRadius > 0 && halfGapSquared <= halfThickness * (2 * inflated - halfThickness);
    }

    internal static bool CanSkip(in Corners current, in Corners opening, double halfGapSquared, float bandRadius,
        float obstacleRadius, in Axis axis, in Axis previous)
    {
        if (!GapEligible(halfGapSquared, bandRadius, obstacleRadius))
            return false;

        double inflated = (double)obstacleRadius + bandRadius;
        double halfThickness = 0.5 * bandRadius;

        // A point on either face lies within |lower - upper| / 4 of a
        // corresponding-depth chord, even when the band warps across the diagonal.
        var a = new Offset(current.FirstUpper);
        var b = new Offset(current.SecondUpper);
        var c = new Offset(current.FirstLower);
        var d = new Offset(current.SecondLower);
        double warp = 0.25 * Math.Sqrt(((d - c) - (b - a)).LengthSquared);
        double margin = 1e-9 * (MaxAbs(a) + MaxAbs(b) + MaxAbs(c) + MaxAbs(d) +
            MaxAbs(axis.Start) + MaxAbs(axis.End) + inflated);
        double reach = obstacleRadius + halfThickness + warp + margin;
        double requiredSquared = reach * reach + halfGapSquared;
        if (!(requiredSquared <= inflated * inflated) ||
            !KeepsSide(current.Points(false), opening.Points(false), axis, previous) ||
            !KeepsSide(current.Points(true), opening.Points(true), axis, previous))
            return false;

        // The corresponding-depth chord has squared distance at least the
        // smaller edge distance squared minus w²/4. Check the actual edges because
        // the local solves can leave an overlap or use different chain radii.
        return ClearsEdge(a, c, axis, requiredSquared) && ClearsEdge(b, d, axis, requiredSquared);
    }

    private static bool KeepsSide((Vector3 A, Vector3 B, Vector3 C) current,
        (Vector3 A, Vector3 B, Vector3 C) opening, in Axis axis, in Axis previous)
    {
        int side = Side(current, axis);
        return side != 0 && side == Side(opening, previous);
    }

    private static int Side((Vector3 A, Vector3 B, Vector3 C) triangle, in Axis axis)
    {
        var root = new Offset(triangle.A);
        var b = new Offset(triangle.B);
        var c = new Offset(triangle.C);
        var normal = Offset.Cross(b - root, c - root);
        double margin = 1e-9 * Math.Sqrt(normal.LengthSquared) *
            (MaxAbs(root) + MaxAbs(b) + MaxAbs(c) + MaxAbs(axis.Start) + MaxAbs(axis.End));
        double start = Offset.Dot(root - axis.Start, normal);
        double end = Offset.Dot(root - axis.End, normal);
        if (start > margin && end > margin)
            return 1;
        return start < -margin && end < -margin ? -1 : 0;
    }

    private static bool ClearsEdge(Offset start, Offset end, in Axis axis, double requiredSquared)
    {
        ChainContactCounts.Record(ChainContactQuery.ThickEdgeQueries);
        return axis.ClosestResidual(new ChainSegmentContact.Segment(start, end - start), out _, out _)
            .LengthSquared >= requiredSquared;
    }

    private static double MaxAbs(Offset offset) => Math.Max(Math.Abs(offset.X), Math.Max(Math.Abs(offset.Y), Math.Abs(offset.Z)));
}
