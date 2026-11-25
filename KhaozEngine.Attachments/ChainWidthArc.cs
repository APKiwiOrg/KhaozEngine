using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;
using static KhaozEngine.Attachments.ChainSegmentContact;

namespace KhaozEngine.Attachments;

// A fixed-parent turn on the length sphere. Each tested interval needs a
// separating plane for its entire arc, including between its endpoints.
internal readonly struct ChainWidthArc
{
    private const int QueryBudget = 64;
    private const double Resolution = 0.0000001;
    private readonly Offset _root, _initial, _tangent;
    private readonly double _length;

    internal ChainWidthArc(Vector3 parent, Vector3 before, Vector3 after, float length)
    {
        _length = length;
        _root = new Offset(parent);
        _initial = Unit(new Offset(before) - _root);
        var target = Unit(new Offset(after) - _root);
        var turn = Offset.Cross(_initial, target);
        double sine = Math.Sqrt(turn.LengthSquared);
        double cosine = Offset.Dot(_initial, target);
        Angle = Math.Atan2(sine, cosine);
        _tangent = sine == 0 ? new Offset(ChainMath.Perpendicular(_initial.ToVector3())) :
            Offset.Cross(turn, _initial) * (1 / sine);
    }

    internal double Angle { get; }

    internal Vector3 Endpoint(double angle) => (_root + Displacement(angle)).ToVector3();

    internal double ClearPrefix(in Axis axis, Offset initialResidual, bool tipOnly, double required, double end,
        ChainContactCounts? counts)
    {
        if (axis.IsPoint)
            return PointPrefix(axis.Start - _root, tipOnly, required, end);

        if (Separates(axis, initialResidual, tipOnly, required, 0, end))
            return end;

        int budget = QueryBudget;
        return Visit(axis, tipOnly, required, 0, end, ref budget, counts);
    }

    private double PointPrefix(Offset point, bool tipOnly, double required, double end)
    {
        // On the turning plane the capsule's forbidden cone is a sinusoidal
        // interval, including when the length sphere reaches only its tip circle.
        double distance = Math.Sqrt(point.LengthSquared);
        if (distance == 0)
            return end;

        double a = Offset.Dot(point, _initial);
        double b = Offset.Dot(point, _tangent);
        double peak = Math.Sqrt(a * a + b * b);
        double boundary = distance * ChainContactProjection.BoundaryCosine(_length, distance, required, !tipOnly);
        if (peak == 0 || boundary >= peak)
            return end;

        double center = Math.Atan2(b, a);
        double halfWidth = Math.Acos(Math.Clamp(boundary / peak, -1, 1));
        double entry = center - halfWidth;
        if (center + halfWidth <= 0)
            entry += Math.Tau;
        return Math.Clamp(entry, 0, end);
    }

    private double Visit(in Axis axis, bool tipOnly, double required, double start, double end, ref int budget,
        ChainContactCounts? counts)
    {
        if (budget == 0)
            return start;

        budget--;
        double middle = (start + end) * 0.5;
        var segment = new Segment(_root, Displacement(middle));
        counts?.Record(ChainContactQuery.WidthSafety);
        var residual = tipOnly ? axis.Residual(segment.Root, segment.Displacement, out _) :
            axis.ClosestResidual(segment, out _, out _);

        if (Separates(axis, residual, tipOnly, required, start, end))
            return end;

        if ((end - start) * _length <= Resolution)
            return start;

        double left = Visit(axis, tipOnly, required, start, middle, ref budget, counts);
        return left < middle ? left : Visit(axis, tipOnly, required, middle, end, ref budget, counts);
    }

    private bool Separates(in Axis axis, Offset residual, bool tipOnly, double required, double start, double end)
    {
        double squared = residual.LengthSquared;
        if (squared < required * required)
            return false;

        var normal = residual * (1 / Math.Sqrt(squared));
        double a = Offset.Dot(normal, _initial);
        double b = Offset.Dot(normal, _tangent);
        var (sinStart, cosStart) = Math.SinCos(start);
        var (sinEnd, cosEnd) = Math.SinCos(end);
        double minimum = Math.Min(a * cosStart + b * sinStart, a * cosEnd + b * sinEnd);
        // For an interval no longer than pi there is at most one interior
        // extremum. A derivative changing from negative to positive is a minimum.
        if (b * cosStart - a * sinStart < 0 && b * cosEnd - a * sinEnd > 0)
            minimum = -Math.Sqrt(a * a + b * b);

        double projection = _length * minimum;
        if (!tipOnly)
            projection = Math.Min(0, projection);
        double planeGap = Offset.Dot(normal, _root - axis.Start) - Math.Max(0, Offset.Dot(normal, axis.Direction));
        return planeGap + projection >= required;
    }

    private Offset Displacement(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return (_initial * cos + _tangent * sin) * _length;
    }

    private static Offset Unit(Offset vector) => vector * (1 / Math.Sqrt(vector.LengthSquared));
}
