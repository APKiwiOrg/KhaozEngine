using KhaozEngine.Navigation;
using RequestAdmission = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces.RequestAdmission;

namespace KhaozEngine.Movement;

/// <summary>Extends the profile search with resumable straightening under the same quota and workspace lease.
/// The inner route stays private until the final result is ready.</summary>
internal sealed class RouteStraighteningRequest : GroundNavigation.ProfilePathRequest
{
    readonly RouteStraightener _straightener;
    bool _fallBack;
    RouteStraighteningCursor _cursor;
    bool _straightening;

    internal RouteStraighteningRequest(RouteStraightener straightener, RequestAdmission admission)
        : base(straightener, admission)
        => _straightener = straightener;

    private protected override void OnBegin() => _fallBack = _straightener.BeginStraightening();

    internal RouteStraighteningOperation NextOperationForTests => _cursor.NextOperation;

    internal bool HasStagedStraightenedArrayForTests => _cursor.HasStagedArray;

    private protected override NavPath? RunSlice<TRecorder>(int quota, ref int spent, TRecorder recorder)
    {
        if (!_straightening)
        {
            if (base.RunSlice(quota, ref spent, recorder) is not { } raw) return null;
            _cursor = new RouteStraighteningCursor(_straightener, Start, StartLayer, raw, _fallBack, Scratch.Waypoints);
            _straightening = true;
        }

        _cursor.RunSlice(quota, ref spent, recorder);
        if (_cursor.Result is not { } result) return null;
        // Only the paid allocation creates a changed route. An unchanged result may be published directly.
        _straightener.RecordStraightened(_cursor.HasStagedArray ? result : null);
        return result;
    }

    private protected override void ClearProfileStages() => _cursor = default;
}
