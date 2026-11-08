using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Optional same-pin access to the installed analytic source. Addresses are copied while the selected
/// physical interval is current. This capability never selects or changes movement support.</summary>
public interface IMovementAnalyticSupportPin
{
    MovementAvailability DescribeAnalyticSupport(Vector2 axis, out MovementSurfaceFeature feature);
    bool AnalyticSupportsAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second);
}
