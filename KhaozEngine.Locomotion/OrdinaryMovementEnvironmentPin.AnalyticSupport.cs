using System.Numerics;

namespace KhaozEngine.Locomotion;

internal sealed partial class OrdinaryMovementEnvironmentPin : IMovementAnalyticSupportPin
{
    public MovementAvailability DescribeAnalyticSupport(Vector2 axis, out MovementSurfaceFeature feature)
    {
        feature = default;
        AssertCurrent();
        var source = AnalyticSource();
        if (source is null) return MovementAvailability.Unresolved;
        Vector3 origin = Frame.PhysicsOrigin;
        var absolute = new Vector2(axis.X + origin.X, axis.Y + origin.Z);
        MovementAvailability result = source.Describe(absolute, out feature);
        AssertCurrent();
        if (result != MovementAvailability.Known) return result;
        if (!feature.IsValid || feature.Owner.WorldId != _owner.WorldId) return MovementAvailability.Invalid;
        return MovementAvailability.Known;
    }

    public bool AnalyticSupportsAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second)
    {
        AssertCurrent();
        bool result = AnalyticSource()?.AreAdjacent(first, second) == true;
        AssertCurrent();
        return result;
    }

    IMovementAnalyticSupportSource? AnalyticSource()
    {
        var source = _owner.Registration.AnalyticSupport;
        return source?.Matches(_owner.GroundHeight, _owner.GroundNormal, _owner.GroundSpace) == true ? source : null;
    }
}
