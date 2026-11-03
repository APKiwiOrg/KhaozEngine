using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    // Friend tests decorate the real guarded planner without mutating a live follower.
    internal MoveToRange(IRegionPathPlanner planner, GroundNavigation navigation, PathFollowConfig? follow)
        : this(planner, navigation.Space, navigation.AllowsSegment, follow)
    {
        _navigation = navigation;
    }

    internal PathFollower Follower => _follower;
}
