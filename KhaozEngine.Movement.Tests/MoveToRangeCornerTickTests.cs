using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeCarryTests;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public class MoveToRangeCornerTickTests
{
    const float Dt = 1f / 30f;
    // A context guards against overlapping steps and test classes run in parallel, so this class keeps its own.
    static readonly GroundMoveContext Flat = new((_, _) => 0f);

    [Fact]
    public void StaircaseRouteMovesOnEveryTickAtLargeCoordinates()
    {
        // The corner stall lead is ruled out: the partial landing tick on a corner still moves. A diagonal partial
        // is 0.0203 m and a straight partial 0.05 m, both well above a stall.
        NavSpace space = Surfaces(146f, 146f);
        var mover = new MoveToRange(new GridPathPlanner(space), space, (_, _) => true, null, RouteApproachOptions.Default);
        ReachTarget target = ReachTarget.Point(new(152.625f, 0.75f, 149.625f));
        MoveState body = Body(148.125f, 148.125f);
        float bound = Tuning.WalkSpeed * Dt;
        var travelBeforeTheInRangeTick = new List<double>();
        RangeMoveStatus last = RangeMoveStatus.Following;
        for (int tick = 0; tick < 300; tick++)
        {
            RangeSteering steering = mover.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            last = steering.Status;
            if (last == RangeMoveStatus.InRange) break;
            Assert.Equal(RangeMoveStatus.Following, last);
            MoveState next = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, Flat);
            double dx = (double)next.Position.X - body.Position.X, dz = (double)next.Position.Z - body.Position.Z;
            travelBeforeTheInRangeTick.Add(Math.Sqrt(dx * dx + dz * dz));
            body = next;
        }
        Assert.All(travelBeforeTheInRangeTick, t => Assert.True(t >= 0.019, $"tick moved {t} m"));
        Assert.Contains(travelBeforeTheInRangeTick, t => t < bound - 1e-3);
        Assert.Equal(RangeMoveStatus.InRange, last);
    }
}
