using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public class RangeTravelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversingTravelDoesNotLatchPhysicalStall(bool routed)
    {
        // #1713: a legitimate reversal travels almost a metre while its window chord is below 0.1 m.
        const int window = 15;
        const float dt = 1f / 30f, threshold = 0.1f;
        var context = new GroundMoveContext((_, _) => 0f);
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete,
            new(0.5f, 0f), new(-2f, 0f), new(-2f, 2f), new(4f, 2f)));
        var route = new MoveToRange(planner, Space, (_, _) => true, null,
            new RouteApproachOptions { Stall = new(window, threshold) });
        var direct = new DirectMoveToRange(new(window, threshold, 45, threshold));
        MoveState body = Body();
        var positions = new List<Vector2>();
        for (int tick = 0; tick < 100; tick++)
        {
            positions.Add(new(body.Position.X, body.Position.Z));
            ReachTarget target = routed ? ReachTarget.Point(new(4f, 0.75f, 2f))
                : ReachTarget.Point(new(tick < 8 ? 50f : -50f, 0.75f, 0f));
            RangeSteering steering = routed
                ? route.Tick(body, Tuning, target, 0f, false, dt, context)
                : direct.Tick(body, Tuning, target, 0f, false, true, dt, context);
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, dt, Tuning, context);
        }
        Assert.True(Vector2.Distance(positions[0], positions[window]) < threshold);
        Assert.True(WindowTravel(positions, 0, window) > threshold);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStationaryWindowCannotRetainRoundedTravel(bool routed)
    {
        // #1713: retiring unequal diagonal intervals must leave exactly zero physical travel.
        const float threshold = 1e-20f, dt = 1f / 30f;
        var context = new GroundMoveContext((_, _) => 0f);
        ReachTarget target = ReachTarget.Point(new(6f, 0.75f, 0f));
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(6f, 0f)));
        var route = new MoveToRange(planner, Space, (_, _) => true, null,
            new RouteApproachOptions { Stall = new(2, threshold) });
        var direct = new DirectMoveToRange(new(2, threshold, 45, threshold));
        Vector2[] samples = { new(0, 0), new(0.00390625f, 0.00390625f), new(0.0078125f, 0.015625f),
            new(0.0078125f, 0.015625f), new(0.0078125f, 0.015625f) };
        for (int i = 0; i < samples.Length; i++)
        {
            MoveState body = Body(samples[i].X, samples[i].Y);
            RangeSteering steering = routed
                ? route.Tick(body, Tuning, target, 0f, false, dt, context)
                : direct.Tick(body, Tuning, target, 0f, false, true, dt, context);
            Assert.Equal(i < 4 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
        }
    }

    // The direct physics fixture includes a 2 cm collision pushback. Count it as travel too.
    internal static double WindowTravel(IReadOnlyList<Vector2> samples, int first, int last)
    {
        double total = 0d;
        for (int i = first + 1; i <= last; i++)
        {
            double dx = (double)samples[i].X - samples[i - 1].X;
            double dz = (double)samples[i].Y - samples[i - 1].Y;
            total += Math.Sqrt(dx * dx + dz * dz);
        }
        return total;
    }
}
