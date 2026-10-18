using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

internal readonly record struct CoverageVertexEnclosure(CoverageFloatInterval X, CoverageFloatInterval Y, CoverageFloatInterval Z)
{
    internal bool Contains(Vector3 value) => X.Contains(value.X) && Y.Contains(value.Y) && Z.Contains(value.Z);
}

/// <summary>NC1's finite-domain device precision contract and outward world-space separation allowance.</summary>
internal static class CoverageReferencePrecision
{
    internal const int BasicUlps = 1, DivideUlps = 4, SqrtUlps = 16, StoreUlps = 1;
    internal const double MinNormal32 = 1.17549435082228750796873653722224568e-38; // 2^-126
    internal const double MinSubnormal32 = 1.40129846432481707092372958328991613e-45; // 2^-149
    internal const double MaxDivisor32 = 8.5070591730234615865843651857942052864e37; // 2^126

    internal static double Budget(float clearance) => .25 * (double)clearance;

    internal static double Radius(in CoverageVertexEnclosure device, in CoverageVertexEnclosure exactCenter)
    {
        if (!device.X.IsFinite || !device.Y.IsFinite || !device.Z.IsFinite
            || !exactCenter.X.IsFinite || !exactCenter.Y.IsFinite || !exactCenter.Z.IsFinite) return double.PositiveInfinity;
        double x = Component(device.X, exactCenter.X), y = Component(device.Y, exactCenter.Y), z = Component(device.Z, exactCenter.Z);
        double xy = Math.BitIncrement(Math.BitIncrement(x * x) + Math.BitIncrement(y * y));
        double squared = Math.BitIncrement(xy + Math.BitIncrement(z * z));
        return Math.BitIncrement(Math.Sqrt(squared));
    }

    internal static double ComposeTolerance(double hostRadius, double equipmentRadius)
    {
        if (!double.IsFinite(hostRadius) || !double.IsFinite(equipmentRadius) || hostRadius < 0 || equipmentRadius < 0) return double.PositiveInfinity;
        if (hostRadius == 0 && equipmentRadius == 0) return 0;
        return Math.BitIncrement(hostRadius + equipmentRadius);
    }

    static double Component(CoverageFloatInterval device, CoverageFloatInterval center)
    {
        double a = device.Min - center.Max, b = device.Max - center.Min;
        // Both signs need outward absolute values, including disjoint device and centre intervals.
        return Math.Max(Math.BitIncrement(Math.Abs(a)), Math.BitIncrement(Math.Abs(b)));
    }
}
