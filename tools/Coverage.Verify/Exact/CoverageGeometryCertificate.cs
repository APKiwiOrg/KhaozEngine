using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>CPU/world geometry only. This result is never permission to draw. FullCoreClearance holds only at full
/// transition weight. ConstructionErrorBound retains construction uncertainty in world lengths.</summary>
public readonly record struct CoverageGeometryCertificate(bool FullCoreClearance, double MinimumCoreClearance,
    double ConstructionErrorBound, int DisplacedVertices);
