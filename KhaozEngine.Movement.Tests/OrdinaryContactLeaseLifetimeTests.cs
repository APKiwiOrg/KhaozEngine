using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryContactLeaseLifetimeTests
{
    [Theory]
    [InlineData(false, "held")]
    [InlineData(true, "held")]
    [InlineData(false, "rebuild")]
    [InlineData(true, "rebuild")]
    [InlineData(false, "unselected")]
    [InlineData(true, "unselected")]
    public void TicksAdvanceWithHeldLeaseOrReconstructionOnEachFreshLease(bool swimming, string strategy)
    {
        using var scene = CreateScene(swimming);
        FramedMovementState state = Start(scene, swimming);
        using var held = strategy == "held" ? scene.Acquire() : null;
        for (int tick = 1; tick <= 6; tick++)
        {
            // Fresh intervals also permit intervening simulation writes without reinstalling the registration.
            if (held is null && tick > 1) scene.Physics.Step(1f / 30f);
            using var fresh = held is null ? scene.Acquire() : null;
            MovementQueryLease lease = held ?? fresh!;
            if (strategy == "rebuild")
            {
                Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(state, out var selection));
                state = new(state.State, state.Frame, selection);
            }
            else if (strategy == "unselected") state = new(state.State, state.Frame, null);

            MovementStepResult result = scene.Binding.StepTowards(state, Vector2.UnitX, false, 1f / 30f, lease);
            Assert.True(result.Outcome == MovementStepOutcome.Advanced,
                $"tick {tick}, swimming {swimming}, strategy {strategy}, outcome {result.Outcome}");
            Assert.True(result.State.State.Position.X > state.State.Position.X);
            Assert.Equal(swimming, result.State.State.Swimming);
            Assert.Equal(!swimming, result.State.State.Grounded);
            Assert.NotNull(result.State.Selection);
            state = result.State;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshNullHintLeaseRefusesCarriedSelectionUntilItReconstructs(bool swimming)
    {
        using var scene = CreateScene(swimming);
        FramedMovementState state = Start(scene, swimming);
        MovementQueryLeaseId previousId;
        using (var first = scene.Acquire())
        {
            previousId = first.Id;
            MovementStepResult initial = scene.Binding.StepTowards(state, Vector2.UnitX, false, 1f / 30f, first);
            Assert.Equal(MovementStepOutcome.Advanced, initial.Outcome);
            state = initial.State;
        }

        using var fresh = scene.Acquire();
        Assert.Equal(previousId, fresh.Id);
        MovementScopeWitness witness = fresh.Witness;
        Assert.Null(witness.Scope.CurrentSpace);
        var body = new MovementBodyQuery(state.State.Position, scene.Tuning.CapsuleRadius,
            scene.Tuning.CapsuleHalfHeight, state.Selection!.Value.Space, state.Selection.Value.Support);
        Assert.Equal(MovementAvailability.Unresolved, fresh.SampleCentreWater(body).Availability);
        MovementStepResult refused = scene.Binding.StepTowards(state, Vector2.UnitX, false, 1f / 30f, fresh);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, refused.Outcome);
        Assert.Equal(state.State.Position, refused.State.State.Position);
        fresh.AssertCurrent();

        Assert.Equal(MovementAvailability.Known, fresh.RebuildSelection(state, out var rebuilt));
        Assert.Same(witness, fresh.Witness);
        Assert.Null(fresh.Witness.Scope.CurrentSpace);
        Assert.Equal(MovementAvailability.Known, fresh.SampleCentreWater(body).Availability);
        MovementStepResult resumed = scene.Binding.StepTowards(new(state.State, state.Frame, rebuilt),
            Vector2.UnitX, false, 1f / 30f, fresh);
        Assert.Equal(MovementStepOutcome.Advanced, resumed.Outcome);
        Assert.True(resumed.State.State.Position.X > state.State.Position.X);
    }

    static OrdinaryContactScene CreateScene(bool swimming)
    {
        var tuning = OrdinaryContactScene.DefaultTuning with
        {
            Water = new(swimming ? WaterTraversalMode.SurfaceSwimmer : WaterTraversalMode.DryOnly)
        };
        float floor = swimming ? -4 : 0;
        return new(tuning, (_, _) => floor,
            extent: new(new(-20, -4, -20), new(20, 4, 20), floor, floor),
            water: swimming ? MovementWaterField.Level("world", 0, -4) : null);
    }

    static FramedMovementState Start(OrdinaryContactScene scene, bool swimming) => new(new MoveState
    {
        Position = new(0, swimming ? -0.15f : 0.75f, 0),
        Grounded = !swimming,
        Swimming = swimming,
        WaterExcursion = swimming ? WaterExcursionState.Surface : WaterExcursionState.None,
    }, scene.Frame, null);
}
