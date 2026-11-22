using System.Numerics;
using KhaozEngine.Navigation;
using static KhaozEngine.Navigation.GridPathPlanner;

namespace KhaozEngine.Movement;

/// <summary>Extends the profile search with resumable straightening under the same quota and workspace lease.
/// The inner route stays private until the final result is ready.</summary>
internal sealed class RouteStraighteningRequest(RouteStraightener straightener, GroundNavigation navigation,
    GridPlanningWorkspaces pool, Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget)
    : GroundNavigation.ProfilePathRequest(navigation, pool, start, goal, radius, budget)
{
    readonly bool _fallBack = straightener.BeginStraightening();
    RouteStraighteningCursor _cursor;
    bool _straightening;

    internal RouteStraighteningOperation NextOperationForTests => _cursor.NextOperation;

    internal bool HasStagedStraightenedArrayForTests => _cursor.HasStagedArray;

    private protected override NavPath? RunSlice(int quota, ref int spent)
    {
        if (!_straightening)
        {
            if (base.RunSlice(quota, ref spent) is not { } raw) return null;
            _cursor = new RouteStraighteningCursor(straightener, Navigation, Start, StartLayer, raw, _fallBack, Scratch.Waypoints);
            _straightening = true;
        }

        _cursor.RunSlice(quota, ref spent);
        if (_cursor.Result is not { } result) return null;
        // Only the paid allocation creates a changed route. An unchanged result may be published directly.
        straightener.RecordStraightened(_cursor.HasStagedArray ? result : null);
        return result;
    }

    private protected override void ClearProfileStages() => _cursor = default;
}
