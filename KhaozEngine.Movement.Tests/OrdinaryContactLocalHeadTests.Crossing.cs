using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public partial class OrdinaryContactLocalHeadTests
{
    [Theory]
    [InlineData("context", false, 0f, 0f)]
    [InlineData("npc", false, 0f, 0f)]
    [InlineData("range", false, 0f, 0f)]
    [InlineData("direct", false, 0f, 0f)]
    [InlineData("context", true, 0f, 0f)]
    [InlineData("npc", true, 0f, 0f)]
    [InlineData("range", true, 0f, 0f)]
    [InlineData("direct", true, 0f, 0f)]
    [InlineData("context", false, 256f, 3.3f)]
    [InlineData("npc", false, 256f, 3.3f)]
    [InlineData("range", false, 256f, 3.3f)]
    [InlineData("direct", false, 256f, 3.3f)]
    [InlineData("context", true, 256f, 3.3f)]
    [InlineData("npc", true, 256f, 3.3f)]
    [InlineData("range", true, 256f, 3.3f)]
    [InlineData("direct", true, 256f, 3.3f)]
    public void LocalHeadWaterEntryAndExitMatchEveryCarriedOrdinarySignal(string head, bool exit, float originX, float originY)
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.WadeOnly) };
        var field = MovementWaterField.Level("world", 0, -4);
        var origin = new Vector3(originX, originY, 0);
        var boundary = MovementBoundary.Rectangle(originX - 20, -20, originX + 20, 20);
        using var scene = new OrdinaryContactScene(tuning, (x, _) => -0.1f * (x - originX),
            (_, _) => Vector3.Normalize(new(0.1f, 1, 0)),
            new(new(originX - 20, -4, -20), new(originX + 20, 4, 20), -2, 2), field, origin, boundary: boundary);
        using var context = new GroundMoveContext(scene.Height, scene.Normal, scene.Physics, null, null, null,
            field, scene.Registration, boundary);
        float startX = originX + (exit ? 1 : -1);
        var state = new MoveState { Position = new(startX, scene.Height(startX, 0) + tuning.CapsuleHalfHeight, 0), Grounded = true };
        Vector2 direction = exit ? -Vector2.UnitX : Vector2.UnitX;
        var target = ReachTarget.Point(new(originX + (exit ? -4 : 4), tuning.CapsuleHalfHeight, 0));
        var space = NavSpace.Single(NavGrid.FromWalkable(32, 32, 1, originX - 16, -16, (_, _) => true));
        var ranged = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete,
            new Vector2(target.Centre.X, target.Centre.Z))), space, (_, _) => true);
        var direct = new DirectMoveToRange(new(20, 0.01f, 20, 0.01f));
        Assert.Equal(exit, Wet(state));
        bool crossed = false;
        for (int tick = 0; tick < 8; tick++)
        {
            var steering = head switch
            {
                "range" => ranged.Tick(state, tuning, target, 0, false, Dt, context),
                "direct" => direct.Tick(state, tuning, target, 0, false, false, Dt, context),
                _ => new RangeSteering(direction, RangeMoveStatus.Following)
            };
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            Assert.Equal(direction, steering.WorldDirection);
            var next = head == "context" ? context.Step(state, direction, false, Dt, tuning)
                : NpcGroundMovement.Step(state, steering, false, Dt, tuning, context);
            using var lease = scene.Acquire();
            var local = state with { Position = state.Position - origin };
            var reference = scene.Binding.StepTowards(new(local, scene.Frame, null), direction, false, Dt, lease);
            Assert.Equal(MovementStepOutcome.Advanced, reference.Outcome);
            Assert.Equal(reference.State.State with { Position = reference.State.State.Position + origin }, next);
            Assert.True(next.Grounded);
            Assert.False(next.Swimming);
            crossed |= Wet(next) != Wet(state);
            state = next;
        }
        Assert.True(crossed);
        Assert.Equal(!exit, Wet(state));

        bool Wet(MoveState body) => field.SampleCentreWater(new(body.Position - origin, tuning.CapsuleRadius,
            tuning.CapsuleHalfHeight, new("world", "ordinary"), null), origin).InWater;
    }
}
