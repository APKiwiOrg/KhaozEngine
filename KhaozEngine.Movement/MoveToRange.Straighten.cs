namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    internal RouteStraightener? Straightener => _straightener;

    // A step the guard refuses on a straightened segment replans once on the raw cell route, whose centre to centre
    // edges the bake proved, and the plan after it straightens again. A refused step on a raw route holds as before.
    private void FallBackToRawRoute()
    {
        if (_straightener?.LastStraightened is not { } straightened ||
            !ReferenceEquals(straightened, _follower.ActivePath)) return;
        _straightener.FallBackOnce();
        _follower.Reset();
    }
}
