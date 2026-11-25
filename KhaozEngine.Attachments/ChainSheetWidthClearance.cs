using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// Width recovery may turn both guides together. Certify a separating plane for
// every affected face over those arcs, including between their endpoints.
internal static class ChainSheetWidthClearance
{
    internal static bool Preserves(in ChainSheetWorkspace.Corners before, in Axis axis, double radius,
        in Turn left, in Turn right)
    {
        if (left.Stationary && right.Stationary)
            return true;

        var diagonal = default(ChainContactTriangle.Diagonal);
        var firstUpper = new Offset(before.FirstUpper);
        var secondUpper = new Offset(before.SecondUpper);
        var first = before.Triangle(false).Closest(axis, false, ref diagonal, out _);
        if (!Separates(first, axis, radius, firstUpper, left, right))
            return false;
        if (right.Stationary)
            return true;

        var second = before.Triangle(true).Closest(axis, true, ref diagonal, out _);
        return Separates(second, axis, radius, firstUpper, new Turn(secondUpper), right);
    }

    private static bool Separates(in ChainContactTriangle.Contact hit, in Axis axis, double radius,
        Offset fixedCorner, in Turn first, in Turn second)
    {
        double squared = hit.Residual.LengthSquared;
        if (squared == 0)
            return false;

        double distance = Math.Sqrt(squared);
        var normal = hit.Residual * (1 / distance);
        double farAxis = Math.Max(0, Offset.Dot(normal, axis.Direction));
        double lowFace = Math.Min(Offset.Dot(normal, fixedCorner - axis.Start),
            Math.Min(first.MinimumProjection(normal, axis.Start), second.MinimumProjection(normal, axis.Start)));
        return lowFace - farAxis >= Math.Min(radius, distance) - ChainWidthClearance.PositionTolerance;
    }

    internal readonly struct Turn
    {
        private readonly Offset _first, _last;
        private readonly ChainWidthArc _arc;

        internal Turn(Offset point)
        {
            _first = _last = point;
            Stationary = true;
        }

        internal Turn(Vector3 parent, Vector3 first, Vector3 last, float length)
        {
            _first = new Offset(first);
            _last = new Offset(last);
            Stationary = first == last || length <= 0;
            if (!Stationary)
                _arc = new ChainWidthArc(parent, first, last, length);
        }

        internal bool Stationary { get; }

        internal double MinimumProjection(Offset normal, Offset origin)
        {
            double endpoints = Math.Min(Offset.Dot(normal, _first - origin), Offset.Dot(normal, _last - origin));
            return Stationary ? endpoints : Math.Min(endpoints, _arc.MinimumProjection(normal, origin));
        }
    }
}
