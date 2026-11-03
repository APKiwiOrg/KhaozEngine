using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>An internal accepted-route certificate in addition to a boundary's speculative query certificate.
/// Collision and support queries may look beyond a vertical permission before finding the accepted endpoint.</summary>
internal interface IMovementRouteBoundary
{
    MovementAvailability ProveRouteSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted);
    void RecordSupportedCentre(Vector3 centre);
}
