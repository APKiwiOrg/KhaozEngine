using System;
using System.Numerics;
using R = KhaozEngine.Render3D.Internal.WaterOpticsExact;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Construction-only rational forward-error proof for the ordered binary32 sampler.</summary>
internal static class WaterOpticsTriangleError
{
    // |fl(x)-x| <= 2^-24 |x| + 2^-126 also covers flushing a subnormal operation result to zero.
    // Constant nonzero divisors must be normal. All bound arithmetic itself is exact rational arithmetic.
    private static R Round(R magnitude) => magnitude / 16777216 + R.From(1.17549435E-38f);

    internal static float Validate(ReadOnlySpan<WaterOpticsExactTriangle> triangles, Vector2 size, R discontinuity)
    {
        R maximumArithmetic = 0, maximumMapping = 0;
        foreach (WaterOpticsExactTriangle triangle in triangles)
        {
            R arithmetic = Arithmetic(triangle);
            R mapping = R.Abs(triangle.SlopeX) * Round(R.From(size.X))
                + R.Abs(triangle.SlopeZ) * Round(R.From(size.Y));
            maximumArithmetic = R.Max(maximumArithmetic, arithmetic);
            maximumMapping = R.Max(maximumMapping, mapping);
        }
        if ((maximumArithmetic + maximumMapping).CompareTo((R)1 / 10000) > 0)
            Refuse("The slope-amplified coordinate and interpolation error exceeds 0.0001 metres.");
        // A coincident-boundary discontinuity is certified within the same ceiling, never forgiven.
        R consumed = maximumArithmetic + maximumMapping + discontinuity;
        if (consumed.CompareTo((R)1 / 10000) > 0)
            throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.AmbiguousSurface,
                "The shared-boundary depth discontinuity with sampler error exceeds 0.0001 metres.");
        // Return the least binary32 upper bound. Later producers must charge this consumed budget
        // before adding source conversion and render-origin error, rather than spending it twice.
        int lo = 0, hi = BitConverter.SingleToInt32Bits(.0001f) + 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (R.From(BitConverter.Int32BitsToSingle(mid)).CompareTo(consumed) < 0) lo = mid + 1;
            else hi = mid;
        }
        return BitConverter.Int32BitsToSingle(lo);
    }

    private static R Arithmetic(WaterOpticsExactTriangle t)
    {
        Vector3 a = t.Source.A, b = t.Source.B, c = t.Source.C;
        if (a.Y == b.Y && a.Y == c.Y) return 0;
        float dx = b.X - a.X, dz = b.Z - a.Z, ex = c.X - a.X, ez = c.Z - a.Z;
        float left = dx * ez, right = dz * ex;
        float det = left - right;
        float db = b.Y - a.Y, dc = c.Y - a.Y;
        if (!float.IsFinite(det) || !float.IsFinite(db) || !float.IsFinite(dc)
            || MathF.Abs(det) < 1.17549435E-38f)
            Refuse("A binary32 interpolation intermediate is not representable.");
        foreach (float constant in new[] { dx, dz, ex, ez, left, right, db, dc })
        {
            if (constant != 0 && MathF.Abs(constant) < 1.17549435E-38f)
                Refuse("A nonzero interpolation constant is subnormal.");
        }

        R boundX = R.Max(R.Abs(t.B.X - t.A.X), R.Abs(t.C.X - t.A.X));
        R boundZ = R.Max(R.Abs(t.B.Z - t.A.Z), R.Abs(t.C.Z - t.A.Z));
        R pxError = Round(boundX), pzError = Round(boundZ);
        R dxError = R.Abs(R.From(dx) - (t.B.X - t.A.X));
        R dzError = R.Abs(R.From(dz) - (t.B.Z - t.A.Z));
        R exError = R.Abs(R.From(ex) - (t.C.X - t.A.X));
        R ezError = R.Abs(R.From(ez) - (t.C.Z - t.A.Z));
        R bError = NumeratorError(boundX, pxError, ez, ezError, boundZ, pzError, ex, exError);
        R cError = NumeratorError(boundZ, pzError, dx, dxError, boundX, pxError, dz, dzError);
        R detMagnitude = R.Abs(t.Determinant);
        R roundedDet = R.Abs(R.From(det));
        R detError = R.Abs(R.From(det) - t.Determinant);
        if (((detMagnitude + R.Max(bError, cError)) / roundedDet).CompareTo(R.From(float.MaxValue) / 2) > 0)
            Refuse("A binary32 barycentric division may overflow.");
        // For an exactly contained point, both exact barycentric numerators have magnitude <= |D|.
        R wbError = (bError + detError) / roundedDet + Round((detMagnitude + bError) / roundedDet);
        R wcError = (cError + detError) / roundedDet + Round((detMagnitude + cError) / roundedDet);
        R qa = R.From(a.Y);
        (R bTermError, R bTermBound) = Term(wbError, db, R.From(b.Y) - qa);
        (R cTermError, R cTermBound) = Term(wcError, dc, R.From(c.Y) - qa);
        R nearBound = R.Abs(qa) + bTermBound;
        R nearRound = Round(nearBound);
        R resultBound = nearBound + nearRound + cTermBound;
        if (resultBound.CompareTo(R.From(float.MaxValue)) > 0)
            Refuse("A binary32 interpolation sum may overflow.");
        return bTermError + nearRound + cTermError + Round(resultBound);
    }

    private static R NumeratorError(R p, R pError, float k, R kError, R q, R qError, float l, R lError)
    {
        R leftBound = (p + pError) * R.Abs(R.From(k));
        R rightBound = (q + qError) * R.Abs(R.From(l));
        R leftRound = Round(leftBound), rightRound = Round(rightBound);
        if ((leftBound + leftRound + rightBound + rightRound).CompareTo(R.From(float.MaxValue)) > 0)
            Refuse("A binary32 barycentric numerator may overflow.");
        return pError * R.Abs(R.From(k)) + p * kError + leftRound
            + qError * R.Abs(R.From(l)) + q * lError + rightRound
            + Round(leftBound + leftRound + rightBound + rightRound);
    }

    private static (R Error, R Bound) Term(R weightError, float roundedDelta, R exactDelta)
    {
        R delta = R.From(roundedDelta);
        R bound = (1 + weightError) * R.Abs(delta);
        R rounding = Round(bound);
        return (weightError * R.Abs(delta) + R.Abs(delta - exactDelta) + rounding, bound + rounding);
    }

    private static void Refuse(string detail) => throw new WaterOpticsSurfaceException(
        WaterOpticsSurfaceFailure.UnrepresentableSurface, detail);
}
