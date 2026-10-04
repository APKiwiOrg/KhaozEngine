using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    // Feet samples of counted ticks under RouteApproachOptions.Stall. Only the stall half of the ring is read.
    private readonly RangeProgressRing? _stallRing;
    private bool _stalled;

    // Records a tick that returns Following or WaitingForPath and latches Blocked when the window shows no ground made.
    private RangeSteering Counted(in MoveState body, RangeSteering steering)
    {
        if (_stallRing is null) return steering;
        _stallRing.Record(new Vector2(body.Position.X, body.Position.Z), 0f);
        RouteStallOptions stall = _options.Stall!;
        if (!_stallRing.StallBreached(stall.TravelMetres, stall.WindowTicks)) return steering;
        _stalled = true;
        return Hold(RangeMoveStatus.Blocked);
    }

    private void ClearStall()
    {
        _stallRing?.ClearAll();
        _stalled = false;
    }
}
