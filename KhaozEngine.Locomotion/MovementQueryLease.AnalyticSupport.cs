using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    internal MovementAvailability DescribeSelectedAnalytic(Vector3 feet, in SupportSample sample,
        MovementSpaceKey space, out MovementSurfaceFeature feature)
    {
        feature = default;
        AssertUsable();
        AssertCurrent();
        if (sample.Static.HasValue || _pin is not IMovementAnalyticSupportPin source ||
            !Workspace.TryGetAnalytic(space, new(feet.X, feet.Z), out var selected) || selected is not { } ground ||
            ground.Identity != Identity || ground.Height != sample.Height || ground.Normal != sample.Normal)
            return MovementAvailability.Unresolved;
        MovementAvailability status = source.DescribeAnalyticSupport(new(feet.X, feet.Z), out feature);
        AssertCurrent();
        if (status == MovementAvailability.Known && (!feature.IsValid || feature.Owner.WorldId != space.WorldId))
            return MovementAvailability.Invalid;
        return status;
    }

    internal bool AnalyticSupportsAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second)
    {
        AssertUsable();
        AssertCurrent();
        bool adjacent = _pin is IMovementAnalyticSupportPin source && source.AnalyticSupportsAdjacent(first, second);
        AssertCurrent();
        return adjacent;
    }
}
