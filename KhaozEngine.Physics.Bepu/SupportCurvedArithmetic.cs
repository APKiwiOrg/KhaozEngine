namespace KhaozEngine.Physics.Bepu;

/// <summary>Shared outward vector operations. The released tangent path preserves its operation order.</summary>
internal static class SupportCurvedArithmetic
{
    internal static GeometryVector Add(GeometryVector a, GeometryVector b) => new(a.X.Add(b.X), a.Y.Add(b.Y), a.Z.Add(b.Z));
    internal static GeometryVector Subtract(GeometryVector a, GeometryVector b) =>
        new(a.X.Subtract(b.X), a.Y.Subtract(b.Y), a.Z.Subtract(b.Z));
    internal static GeometryVector Scale(GeometryVector a, GeometryInterval s) =>
        new(a.X.Multiply(s), a.Y.Multiply(s), a.Z.Multiply(s));
    internal static GeometryVector Divide(GeometryVector a, GeometryInterval s) =>
        new(a.X.Divide(s), a.Y.Divide(s), a.Z.Divide(s));
    internal static GeometryVector Hull(GeometryVector a, GeometryVector b) => new(Hull(a.X, b.X), Hull(a.Y, b.Y), Hull(a.Z, b.Z));
    internal static GeometryInterval Hull(GeometryInterval a, GeometryInterval b) =>
        GeometryInterval.Enclose(System.Math.Min(a.Lower, b.Lower), System.Math.Max(a.Upper, b.Upper));
}
