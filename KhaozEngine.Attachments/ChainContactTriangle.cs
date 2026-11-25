using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// A closest pair is an axis-face intersection, an axis end against the face, or
// an axis-edge pair. Collapsed triangles still have their three edges.
internal readonly struct ChainContactTriangle
{
    private readonly Offset _root;
    private readonly Offset _ab;
    private readonly Offset _ac;

    internal ChainContactTriangle(Vector3 first, Vector3 second, Vector3 third)
        : this(new Offset(first), new Offset(second) - new Offset(first), new Offset(third) - new Offset(first)) { }

    private ChainContactTriangle(Offset root, Offset ab, Offset ac) => (_root, _ab, _ac) = (root, ab, ac);

    internal Offset Normal
    {
        get
        {
            var normal = Offset.Cross(_ab, _ac);
            double squared = normal.LengthSquared;
            return squared == 0 ? default : normal * (1 / Math.Sqrt(squared));
        }
    }

    internal int AxisSide(in Axis axis, Offset normal, double tolerance)
    {
        double a = Offset.Dot(normal, _root - axis.Start);
        double b = Offset.Dot(normal, _root - axis.End);
        if (a > tolerance && b > tolerance)
            return 1;
        return a < -tolerance && b < -tolerance ? -1 : 0;
    }

    internal bool TryTranslationTo(in ChainContactTriangle next, out Offset translation)
    {
        translation = next._root - _root;
        return _ab == next._ab && _ac == next._ac;
    }

    internal (double Min, double Max) Projection(Offset direction, Offset origin)
    {
        double a = Offset.Dot(direction, _root - origin);
        double b = a + Offset.Dot(direction, _ab);
        double c = a + Offset.Dot(direction, _ac);
        return (Math.Min(a, Math.Min(b, c)), Math.Max(a, Math.Max(b, c)));
    }

    internal Offset MaterialResidual(in Axis axis, Offset weights, double along) =>
        (_root - axis.Start) + _ab * weights.Y + _ac * weights.Z - axis.Direction * along;

    internal ChainContactTriangle Interpolate(in ChainContactTriangle target, double time) =>
        new(_root + (target._root - _root) * time, _ab + (target._ab - _ab) * time, _ac + (target._ac - _ac) * time);

    internal double RelativeTravel(in ChainContactTriangle target, in Axis firstAxis, in Axis lastAxis)
    {
        var aStart = (target._root - lastAxis.Start) - (_root - firstAxis.Start);
        var aEnd = (target._root - lastAxis.End) - (_root - firstAxis.End);
        var b = target._ab - _ab;
        var c = target._ac - _ac;
        return Math.Sqrt(Math.Max(Bound(aStart), Bound(aEnd)));

        double Bound(Offset a) => Math.Max(a.LengthSquared, Math.Max((a + b).LengthSquared, (a + c).LengthSquared));
    }

    internal readonly record struct Contact(Offset Residual, Offset Weights, double Along);

    internal Contact Closest(in Axis axis, out Offset normal)
    {
        var diagonal = default(Diagonal);
        return Closest(axis, false, ref diagonal, out normal);
    }

    internal Contact Closest(in Axis axis, bool second, ref Diagonal diagonal, out Offset normal)
    {
        var areaNormal = Offset.Cross(_ab, _ac);
        double areaSquared = areaNormal.LengthSquared;
        normal = areaSquared == 0 ? default : areaNormal * (1 / Math.Sqrt(areaSquared));

        if (areaSquared > 0)
        {
            double axisSlope = Offset.Dot(axis.Direction, normal);
            if (axisSlope != 0)
            {
                // Start at the closest axis point to the first corner. This keeps the
                // construction local even when the axis endpoints are very far away.
                var fromAxis = axis.Residual(_root, default, out _);
                var intersection = axis.Direction * (Offset.Dot(fromAxis, normal) / axisSlope) - fromAxis;
                if (FaceWeights(intersection, areaNormal, areaSquared, out var weights))
                {
                    double along = axis.Along(_root, intersection);
                    if (along is > 0 and < 1)
                        return new Contact(default, weights, along);
                }
            }
        }

        var edgeAB = new ChainSegmentContact.Segment(_root, _ab);
        var edgeAC = new ChainSegmentContact.Segment(_root, _ac);
        double abFraction, abAlong, acFraction, acAlong;
        var ab = second ? diagonal.Read(axis, edgeAB, out abFraction, out abAlong)
            : axis.ClosestResidual(edgeAB, out abFraction, out abAlong);
        var ac = second ? axis.ClosestResidual(edgeAC, out acFraction, out acAlong)
            : diagonal.Read(axis, edgeAC, out acFraction, out acAlong);
        var bc = axis.ClosestResidual(new ChainSegmentContact.Segment(_root + _ab, _ac - _ab),
            out double bcFraction, out double bcAlong);
        var best = new Contact(ab, new Offset(1 - abFraction, abFraction, 0), abAlong);
        KeepCloser(ref best, new Contact(ac, new Offset(1 - acFraction, 0, acFraction), acAlong));
        KeepCloser(ref best, new Contact(bc, new Offset(0, 1 - bcFraction, bcFraction), bcAlong));

        if (areaSquared > 0)
        {
            TestAxisEnd(axis.Start, 0, normal, areaNormal, areaSquared, ref best);
            if (!axis.IsPoint)
                TestAxisEnd(axis.End, 1, normal, areaNormal, areaSquared, ref best);
        }

        return best;
    }

    internal struct Diagonal
    {
        private bool _valid;
        private ChainSegmentContact.Segment _segment;
        private Offset _residual;
        private double _fraction, _along;

        internal Offset Read(in Axis axis, in ChainSegmentContact.Segment segment, out double fraction, out double along)
        {
            if (!_valid || !_segment.Root.SameBits(segment.Root) || !_segment.Displacement.SameBits(segment.Displacement))
            {
                var hit = axis.ClosestResidual(segment, out fraction, out along);
                _segment = segment;
                _residual = hit;
                _fraction = fraction;
                _along = along;
                _valid = true;
                return hit;
            }

            (fraction, along) = (_fraction, _along);
            return _residual;
        }
    }

    private void TestAxisEnd(Offset end, double along, Offset normal, Offset areaNormal, double areaSquared,
        ref Contact best)
    {
        var fromRoot = end - _root;
        double height = Offset.Dot(fromRoot, normal);
        if (height * height >= best.Residual.LengthSquared)
            return;

        if (FaceWeights(fromRoot, areaNormal, areaSquared, out var weights))
            best = new Contact(normal * -height, weights, along);
    }

    private bool FaceWeights(Offset fromRoot, Offset areaNormal, double areaSquared, out Offset weights)
    {
        // Crossing with an in-plane edge discards the offset normal to the face.
        double b = Offset.Dot(Offset.Cross(fromRoot, _ac), areaNormal) / areaSquared;
        double c = Offset.Dot(Offset.Cross(_ab, fromRoot), areaNormal) / areaSquared;
        double a = 1 - b - c;
        weights = new Offset(a, b, c);
        return a >= 0 && b >= 0 && c >= 0;
    }

    private static void KeepCloser(ref Contact best, in Contact candidate)
    {
        if (candidate.Residual.LengthSquared < best.Residual.LengthSquared)
            best = candidate;
    }
}
