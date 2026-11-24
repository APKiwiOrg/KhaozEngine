using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// A closest pair is an axis-face intersection, an axis end against the face, or
// an axis-edge pair. Collapsed triangles still have their three edges.
internal readonly struct ChainContactTriangle(Vector3 first, Vector3 second, Vector3 third)
{
    private readonly Offset _root = new(first);
    private readonly Offset _ab = new Offset(second) - new Offset(first);
    private readonly Offset _ac = new Offset(third) - new Offset(first);

    internal readonly record struct Contact(Offset Residual, Offset Weights, double Along);

    internal Contact Closest(in Axis axis, out Offset normal)
    {
        if (ChainSheetContact.ClosestPointEvaluationsForTests is { } count)
            ChainSheetContact.ClosestPointEvaluationsForTests = count + 1;

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
                    axis.Residual(_root, intersection, out double along);
                    if (along is > 0 and < 1)
                        return new Contact(default, weights, along);
                }
            }
        }

        var ab = axis.ClosestResidualUncounted(new ChainSegmentContact.Segment(_root, _ab),
            out double abFraction, out double abAlong);
        var ac = axis.ClosestResidualUncounted(new ChainSegmentContact.Segment(_root, _ac),
            out double acFraction, out double acAlong);
        var bc = axis.ClosestResidualUncounted(new ChainSegmentContact.Segment(_root + _ab, _ac - _ab),
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
