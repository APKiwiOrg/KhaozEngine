using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Per-channel inverse-metre coefficients and dimensionless single-scattering albedo.</summary>
internal readonly record struct WaterColumnCoefficients(
    Vector3 Extinction, Vector3 Scattering, Vector3 Absorption, Vector3 Albedo);
