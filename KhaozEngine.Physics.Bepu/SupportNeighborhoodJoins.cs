namespace KhaozEngine.Physics.Bepu;

/// <summary>The convex join test. Two polygons are joined when they may meet within the band and every vertex of
/// each certainly lies at most the band above the other's plane. It is geometric for every pair, inside one static
/// and across statics, so a scene's partition into statics cannot change it. Polygons that cross each other's
/// planes, such as a ramp sunk into a floor, are not joined.</summary>
internal static class SupportNeighborhoodJoins
{
    /// <summary>Positive when joined, negative when not, unresolved when the bounded arithmetic fails.</summary>
    internal static GeometrySign Joined(SupportPolygon first, SupportPolygon second, double band,
        SupportSegmentPolygon kernel)
    {
        if (SupportGeometry.BoxGap(first.Low, first.High, second.Low, second.High) > band)
            return GeometrySign.Negative;
        GeometrySign below = Below(first, second, band);
        if (below != GeometrySign.Positive) return below;
        below = Below(second, first, band);
        if (below != GeometrySign.Positive) return below;
        // The distance between two convex polygons is attained with a point on an edge of one of them.
        GeometrySign meets = Meets(first, second, band, kernel);
        return meets != GeometrySign.Negative ? meets : Meets(second, first, band, kernel);
    }

    static GeometrySign Below(SupportPolygon vertices, SupportPolygon plane, double band)
    {
        foreach (FeaturePoint vertex in vertices.Vertices)
        {
            GeometryInterval height = plane.Height(vertex);
            if (!height.IsResolved) return GeometrySign.Unresolved;
            if (height.Upper > band) return GeometrySign.Negative;
        }
        return GeometrySign.Positive;
    }

    static GeometrySign Meets(SupportPolygon edges, SupportPolygon polygon, double band, SupportSegmentPolygon kernel)
    {
        for (int i = 0; i < edges.Edges.Length; i++)
        {
            SupportDistance distance = kernel.Measure(edges.Vertices[i], edges.Edges[i], polygon, band);
            if (!distance.IsResolved) return GeometrySign.Unresolved;
            if (distance.Lower <= band) return GeometrySign.Positive;
        }
        return GeometrySign.Negative;
    }
}
