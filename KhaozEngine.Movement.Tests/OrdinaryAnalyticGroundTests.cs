using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryAnalyticGroundTests
{
    [Fact]
    public void AnalyticLandingAboveStepHeightMatchesDirectDryInOneTick()
    {
        using var scene = new OrdinaryContactScene();
        var start = new MoveState { Position = new(0, 2, 0), VerticalVelocity = -20 };
        Assert.True(start.Position.Y - scene.Tuning.CapsuleHalfHeight > scene.Tuning.StepHeight);
        var dry = CharacterMovement.StepTowards(start, Vector2.Zero, false, 0.1f, scene.Height,
            scene.Tuning, world: scene.Physics);
        using var lease = scene.Acquire();
        var ordinary = scene.Binding.StepTowards(new(start, scene.Frame, null), Vector2.Zero, false, 0.1f, lease);
        Assert.Equal(dry, ordinary.State.State);
        Assert.True(ordinary.State.State.Grounded);
        Assert.Equal(0f, ordinary.State.State.Position.Y - scene.Tuning.CapsuleHalfHeight);
    }

    [Fact]
    public void AnalyticBarrierAboveStepHeightBlocksAsTerrainWall()
    {
        const float barrierX = 0.1f;
        using var scene = new OrdinaryContactScene(height: (x, _) => x >= barrierX ? 1f : 0f,
            extent: new(new(-20, -4, -20), new(20, 4, 20), 0, 1));
        var start = scene.State(new(0, 0.75f, 0));
        var dry = CharacterMovement.StepTowards(start.State, Vector2.UnitX, false, 0.1f, scene.Height,
            scene.Tuning, world: scene.Physics);
        using var lease = scene.Acquire();
        var ordinary = scene.Binding.StepTowards(start, Vector2.UnitX, false, 0.1f, lease);
        Assert.Equal(dry, ordinary.State.State);
        Assert.True(ordinary.State.State.Position.X < barrierX);
    }

    [Fact]
    public void AnalyticScopeFactsIgnoreRequestReachAndCertifyOnlyTheirDomain()
    {
        float height = 0;
        using var scene = new OrdinaryContactScene(height: (_, _) => height);
        MovementAvailability Read(float reach, out MovementAnalyticGround? fact)
        {
            using var lease = scene.Acquire();
            var state = scene.State(new(0, 0.75f, 0));
            Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(state, out var selection));
            var body = new MovementBodyQuery(state.State.Position, 0.25f, 0.75f, selection.Space, null);
            return lease.SampleAnalyticGround(new(body, reach, reach, 0,
                new(selection.Space, body.Feet)), out fact);
        }
        Assert.Equal(MovementAvailability.Known, Read(0, out var narrow));
        Assert.Equal(MovementAvailability.Known, Read(2, out var wide));
        Assert.Equal(narrow, wide);
        height = scene.Scope.EnvelopeMin.Y - 1;
        Assert.Equal(MovementAvailability.Known, Read(0, out var below));
        Assert.Null(below);
        height = scene.Scope.EnvelopeMax.Y + 1;
        Assert.Equal(MovementAvailability.Unresolved, Read(0, out _));
        height = float.NaN;
        Assert.Equal(MovementAvailability.Unresolved, Read(0, out _));
    }

    [Fact]
    public void DeclaredResidentHolesHoldOnlyBodiesWhoseFootprintNeedsMissingGround()
    {
        using var scene = new OrdinaryContactScene(height: (x, _) => x >= 1 ? float.NaN : 0);
        using var lease = scene.Acquire();
        var island = scene.State(new(0, 0.75f, 0));
        var moving = scene.Binding.StepTowards(island, Vector2.UnitX, false, 1f / 30f, lease);
        Assert.Equal(MovementStepOutcome.Advanced, moving.Outcome);
        Assert.True(moving.State.State.Position.X > island.State.Position.X);
        var edge = scene.State(new(0.9f, 0.75f, 0));
        var held = scene.Binding.StepTowards(edge, Vector2.UnitX, false, 1f / 30f, lease);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, held.Outcome);
        Assert.Equal(edge.State, held.State.State);
    }

    [Fact]
    public void AnalyticAxesThatCannotBeConvertedExactlyRefuse()
    {
        using var scene = new OrdinaryContactScene(origin: new(16777216, 0, 0),
            extent: new(new(16777196, -4, -20), new(16777236, 4, 20), 0, 0));
        using var lease = scene.Acquire();
        var state = scene.State(new(1, 0.75f, 0));
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(state, out var selection));
        var body = new MovementBodyQuery(state.State.Position, 0.25f, 0.75f, selection.Space, null);
        Assert.Equal(MovementAvailability.Unresolved, lease.SampleAnalyticGround(new(body, 0, 0, 0,
            new(selection.Space, body.Feet)), out _));
    }
}
