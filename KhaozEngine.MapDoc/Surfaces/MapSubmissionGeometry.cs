using System.Numerics;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Shared whole-metre anchors and exact subtraction before submission rounding.</summary>
internal static class MapSubmissionGeometry
{
    internal static MapSubmissionAnchor Anchor(MapExactXz first, MapExactXz last, MapExactValue middleHeight) =>
        new((first.X.CompareTo(last.X) < 0 ? first.X : last.X).Floor(), middleHeight.Floor(),
            (first.Z.CompareTo(last.Z) < 0 ? first.Z : last.Z).Floor());

    internal static Vector3 Offset(MapExactPoint point, MapSubmissionAnchor anchor) => new(
        point.X.Subtract(new(anchor.X, 1)).ToSingle(),
        point.Y.Subtract(new(anchor.Y, 1)).ToSingle(),
        point.Z.Subtract(new(anchor.Z, 1)).ToSingle());
}
