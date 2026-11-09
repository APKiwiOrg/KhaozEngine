using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Optional immutable analytic-field capture for supported arrival trials. A Known capture covers
/// every represented absolute axis in the closed rectangle, with unchanged analytic ownership and the exact
/// height and normal returned by the matching delegates. It does not certify physics, medium or scope.</summary>
public interface IMovementSupportedAnalyticSupportSource : IMovementAnalyticSupportSource
{
    MovementAvailability CaptureSupportedRegion(Vector2 minimum, Vector2 maximum,
        out IMovementSupportedGroundRegion? region);
}

/// <summary>Bounded immutable candidates with the producer's original selection, tie and float evaluation
/// order. Bounds enclose every returned height and normal Y, including rounded boundaries. Sampling outside
/// the captured rectangle refuses. A capture never changes or borrows mutable caller storage.</summary>
public interface IMovementSupportedGroundRegion
{
    float MinimumHeight { get; }
    float MaximumHeight { get; }
    float MinimumNormalY { get; }
    MovementAvailability Sample(Vector2 absoluteAxis, out float height, out Vector3 normal);
}

internal interface IMovementSupportedRangeSupportPin
{
    MovementAvailability CaptureSupportedRegion(Vector2 minimum, Vector2 maximum,
        out SupportedGroundRegion? region);
}
