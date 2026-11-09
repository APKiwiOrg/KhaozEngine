using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Exact supporting line in XZ and height, shared by sheet joins and boundary cancellation.</summary>
internal readonly record struct MapTerrainLine(bool Vertical, MapWideRational Slope, MapWideRational Intercept,
    MapWideRational HeightSlope, MapWideRational HeightIntercept)
{
    internal MapWideRational Coordinate(MapWidePoint point) => Vertical ? point.Z : point.X;

    internal static MapTerrainLine Through(MapWidePoint a, MapWidePoint b)
    {
        bool vertical = a.X.Equals(b.X);
        var slope = vertical ? default : b.Z.Subtract(a.Z).Divide(b.X.Subtract(a.X));
        var first = vertical ? a.Z : a.X; var last = vertical ? b.Z : b.X;
        var heightSlope = b.Y.Subtract(a.Y).Divide(last.Subtract(first));
        return new(vertical, slope, vertical ? a.X : a.Z.Subtract(slope.Multiply(a.X)),
            heightSlope, a.Y.Subtract(heightSlope.Multiply(first)));
    }
}
