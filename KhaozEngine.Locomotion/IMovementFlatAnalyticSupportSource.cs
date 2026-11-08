using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Optional immutable-field proof for exact flat movement previews. Known certifies that every
/// represented axis in the closed absolute XZ rectangle returns the same finite height bit pattern and
/// exactly UnitY from the matching height and normal delegates. It also certifies unchanged analytic ownership.
/// This proves neither selected physical support, clearance, medium nor scope admission.</summary>
public interface IMovementFlatAnalyticSupportSource : IMovementAnalyticSupportSource
{
    MovementAvailability CertifyFlatRegion(Vector2 minimum, Vector2 maximum, out float height);
}

internal interface IMovementFlatRangeSupportPin
{
    MovementAvailability CertifyFlatRegion(Vector2 minimum, Vector2 maximum, out float localHeight);
}
