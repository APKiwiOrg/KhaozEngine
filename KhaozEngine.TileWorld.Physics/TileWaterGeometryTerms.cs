using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using B = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Basis;

namespace KhaozEngine.TileWorld.Physics;

// The complete original geometry subexpressions. Entries are immutable before atomic publication.
internal static class TileWaterGeometryTerms
{
    internal readonly record struct Value(I Aa, I Bb, I Ab, V Cross, V Bc, V Ca, I Determinant);
    internal sealed class Entry(ulong token, Value value)
    {
        internal readonly ulong Token = token;
        internal readonly Value Geometry = value;
    }

    internal static Value Compute(TileWaterConvexShape shape, B basis)
    {
        if (basis.A < 0) return default;
        var a = shape.Planes[basis.A];
        I aa = V.Dot(a.Normal, a.Normal);
        I bb = default, ab = default, determinant = default;
        V cross = default, bc = default, ca = default;
        if (basis.B >= 0)
        {
            var b = shape.Planes[basis.B];
            cross = V.Cross(a.Normal, b.Normal);
            if (basis.C < 0)
            {
                bb = V.Dot(b.Normal, b.Normal);
                ab = V.Dot(a.Normal, b.Normal);
                determinant = V.Dot(cross, cross);
            }
            else
            {
                var c = shape.Planes[basis.C];
                bc = V.Cross(b.Normal, c.Normal);
                ca = V.Cross(c.Normal, a.Normal);
                determinant = V.Dot(a.Normal, bc);
            }
        }
        return new(aa, bb, ab, cross, bc, ca, determinant);
    }
}
