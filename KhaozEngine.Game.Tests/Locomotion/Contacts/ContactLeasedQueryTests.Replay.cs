using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Fact]
    public void LongAnalyticReplayUnderOneLeaseDoesNotExhaustPriorTickFacts()
    {
        using var scene = new QueryScene(_ => 0);
        var state = new FramedMovementState(new MoveState { Position = new(0, 0.751f, 0), Grounded = true },
            scene.Lease.Frame, new(Room, new MovementSupportKey("world", "floor"), scene.Lease.Identity));
        for (int tick = 0; tick < 192; tick++)
        {
            MovementStepResult result = ContactController.StepLeased(state, Vector2.UnitX, 1, false, false,
                1f / 30, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease);
            Assert.True(result.Outcome == MovementStepOutcome.Advanced, $"tick {tick}, {result.Outcome}");
            state = result.State;
        }
        Assert.InRange(state.State.Position.X, 38.3995f, 38.4005f);
        Assert.True(state.State.Grounded);
        Assert.Equal(new MovementSupportKey("world", "floor"), state.Selection!.Value.Support);
    }
}
