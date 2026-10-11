using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Adapts the shared dependency-free admission envelope to the backend's interval vectors.</summary>
internal static class SupportFootprintVolume
{
    internal static bool TryBounds(in SupportFootprintRequest request, out GeometryVector minimum,
        out GeometryVector maximum)
    {
        minimum = maximum = default;
        if (!SupportFootprintBounds.TryCreate(request, out SupportFootprintBounds bounds)) return false;
        minimum = FeaturePoint.Exact(bounds.Minimum).Bounds;
        maximum = FeaturePoint.Exact(bounds.Maximum).Bounds;
        return true;
    }
}
