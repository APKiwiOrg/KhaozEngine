using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    // Friend tests decorate the real guarded planner without mutating a live follower.
    internal static MoveToRange CreateForTests(IRegionPathPlanner planner, GroundNavigation navigation, PathFollowConfig? follow)
        => new(planner, navigation, follow);

    private MoveToRange(IRegionPathPlanner planner, GroundNavigation navigation, PathFollowConfig? follow)
        : this(planner, navigation.Space, navigation.AllowsSegment, follow)
    {
        _navigation = navigation;
    }

    internal NavPath? ActivePathForTests => _follower.ActivePath;

    internal int ActiveWaypointIndexForTests => _follower.ActiveWaypointIndex;
}
