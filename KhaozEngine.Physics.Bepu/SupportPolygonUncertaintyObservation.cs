using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Copied admitted-polygon state after uncertainty injection. Vertices belong to this observation.</summary>
internal readonly record struct SupportPolygonUncertaintyObservation(
    StaticHandle Static,
    int ElementId,
    FeaturePoint[] Vertices,
    FeaturePoint Normal,
    bool HasSource);
