using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A sufficient resolution certificate for every released first-edge meeting evaluator.
/// Bounds outside the certificate keep the full predicate, including its old refusal.</summary>
internal sealed class SupportMeetingDomain
{
    // This is the existing expansion input domain, not a geometric error or reach threshold.
    static readonly double MinimumDenominator = Math.ScaleB(1, -200);
    FeatureNumber[] _squared = [];
    internal bool Valid { get; private set; }

    internal void Prepare(SupportPolygon polygon)
    {
        Valid = false;
        if (!polygon.IsResolved || !Bounded(polygon.Normal, 524288) ||
            !Denominator(polygon.NormalLength) || !Denominator(FeaturePoint.Dot(polygon.Normal, polygon.Normal).Bounds))
            return;
        foreach (FeaturePoint vertex in polygon.Vertices)
            if (!Bounded(vertex, 256)) return;
        if (_squared.Length < polygon.Edges.Length) _squared = new FeatureNumber[polygon.Edges.Length];
        for (int edge = 0; edge < polygon.Edges.Length; edge++)
        {
            FeaturePoint direction = polygon.Edges[edge];
            if (!Bounded(direction, 512)) return;
            FeatureNumber squared = FeaturePoint.Dot(direction, direction);
            if (!Denominator(squared.Bounds) || !Denominator(SupportGeometry.Length(direction.Bounds))) return;
            _squared[edge] = squared;
        }
        Valid = true;
    }

    internal static bool Proves(SupportPolygon first, SupportPolygon second,
        SupportMeetingDomain a, SupportMeetingDomain b)
    {
        for (int i = 0; i < first.Edges.Length; i++)
        {
            FeaturePoint direction = first.Edges[i];
            FeatureNumber rate = FeaturePoint.Dot(second.Normal, direction);
            if (!rate.IsResolved) return false;
            GeometrySign sign = rate.Sign;
            if ((sign == GeometrySign.Positive && rate.Bounds.Lower < MinimumDenominator) ||
                (sign == GeometrySign.Negative && -rate.Bounds.Upper < MinimumDenominator)) return false;
            for (int j = 0; j < second.Edges.Length; j++)
            {
                FeatureNumber dot = FeaturePoint.Dot(direction, second.Edges[j]);
                FeatureNumber determinant = a._squared[i].Multiply(b._squared[j]).Subtract(dot.Multiply(dot));
                if (!determinant.IsResolved ||
                    (determinant.Sign == GeometrySign.Positive && determinant.Bounds.Lower < MinimumDenominator))
                    return false;
            }
        }
        return true;
    }

    // Vertices, edges and normals bound the original numerators below 2^44. Positive
    // denominators bound ratios below 2^244, along/projection points below 2^264,
    // squared distances below 2^532 and normalized heights below 2^488. Every original
    // candidate and height therefore resolves. Uncertain determinants and rates keep
    // the released nondividing branch. Vertex strata always provide a valid upper
    // witness. Proving every first-edge solve resolves prevents an incidence shortcut
    // from hiding a refusal before the edge carrying the offered point is visited.
    static bool Denominator(GeometryInterval value) => value.IsResolved && value.Lower >= MinimumDenominator;
    static bool Bounded(FeaturePoint point, double maximum) =>
        Bounded(point.X.Bounds, maximum) && Bounded(point.Y.Bounds, maximum) && Bounded(point.Z.Bounds, maximum);
    static bool Bounded(GeometryInterval value, double maximum) => value.IsResolved &&
        value.Lower >= -maximum && value.Upper <= maximum;
}
