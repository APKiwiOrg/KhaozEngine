using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

internal sealed partial class OrdinaryMovementEnvironmentPin : IMovementFlatRangeSupportPin
{
    public MovementAvailability CertifyFlatRegion(Vector2 minimum, Vector2 maximum, out float localHeight)
    {
        localHeight = default;
        AssertCurrent();
        if (_owner.GroundSpace != OrdinaryGroundSpace.World ||
            AnalyticSource() is not IMovementFlatAnalyticSupportSource source) return MovementAvailability.Unresolved;
        Vector3 origin = Frame.PhysicsOrigin;
        // Ordinary analytic reads use these same rounded translations. Their monotonicity encloses every
        // intermediate represented axis, without assuming that world and local subtraction cancel.
        var status = source.CertifyFlatRegion(new(minimum.X + origin.X, minimum.Y + origin.Z),
            new(maximum.X + origin.X, maximum.Y + origin.Z), out float height);
        AssertCurrent();
        if (status != MovementAvailability.Known) return status;
        if (!float.IsFinite(height)) return MovementAvailability.Invalid;
        localHeight = SupportedCapsulePose.LocalSupport(height, origin.Y);
        return float.IsFinite(localHeight) ? MovementAvailability.Known : MovementAvailability.Unresolved;
    }
}
