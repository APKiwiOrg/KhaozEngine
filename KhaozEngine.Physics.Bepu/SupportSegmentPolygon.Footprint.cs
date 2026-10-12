namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportSegmentPolygon
{
    /// <summary>Measures a downward proposal's finite contact. An approached closest pair has its segment
    /// point above the polygon point within the contact band, even when it is below the extended face plane.
    /// The retained sweep and the proposal-height certificate keep their own back-face policies.</summary>
    internal SupportDistance MeasureFootprintContact(FeaturePoint start, FeaturePoint direction,
        SupportPolygon polygon, double contactBand)
    {
        SupportDistance distance = Measure(start, direction, polygon, contactBand);
        if (!distance.IsResolved) return default;
        bool approached = false;
        foreach (SupportCandidate candidate in _candidates)
        {
            if (candidate.Distance.Lower > distance.Upper) continue;
            GeometryInterval above = candidate.Axis.Y.Subtract(candidate.Geometry.Y).Bounds;
            if (!above.IsResolved) return default;
            approached |= above.Upper >= -contactBand;
        }
        return distance with { Front = approached };
    }
}
