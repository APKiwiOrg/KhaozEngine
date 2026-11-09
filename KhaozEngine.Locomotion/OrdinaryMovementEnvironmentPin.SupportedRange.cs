using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

internal sealed partial class OrdinaryMovementEnvironmentPin : IMovementSupportedRangeSupportPin
{
    public MovementAvailability CaptureSupportedRegion(Vector2 minimum, Vector2 maximum, out SupportedGroundRegion? region)
    {
        region = null;
        AssertCurrent();
        if (_owner.GroundSpace != OrdinaryGroundSpace.World ||
            AnalyticSource() is not IMovementSupportedAnalyticSupportSource source) return MovementAvailability.Unresolved;
        Vector3 origin = Frame.PhysicsOrigin;
        var status = source.CaptureSupportedRegion(new(minimum.X + origin.X, minimum.Y + origin.Z),
            new(maximum.X + origin.X, maximum.Y + origin.Z), out var captured);
        AssertCurrent();
        if (status != MovementAvailability.Known) return status;
        if (captured is null || !float.IsFinite(captured.MinimumHeight) || !float.IsFinite(captured.MaximumHeight) ||
            captured.MinimumHeight > captured.MaximumHeight || !float.IsFinite(captured.MinimumNormalY) ||
            captured.MinimumNormalY < 0 || captured.MinimumNormalY > 1) return MovementAvailability.Unresolved;
        var extent = _owner.Registration.Extent;
        float low = SupportedCapsulePose.LocalSupport(captured.MinimumHeight, origin.Y);
        float high = SupportedCapsulePose.LocalSupport(captured.MaximumHeight, origin.Y);
        if (extent.AnalyticMinimumY is not { } lower || extent.AnalyticMaximumY is not { } upper ||
            captured.MinimumHeight < lower || captured.MaximumHeight > upper ||
            low < Witness.Scope.EnvelopeMin.Y || high > Witness.Scope.EnvelopeMax.Y)
            return MovementAvailability.Unresolved;
        region = new(captured, origin, minimum, maximum, low, high);
        return MovementAvailability.Known;
    }
}
