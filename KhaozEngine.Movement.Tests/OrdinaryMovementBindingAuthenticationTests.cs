using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementBindingAuthenticationTests
{
    [Theory]
    [InlineData(false, "Step")]
    [InlineData(false, "StepTowards")]
    [InlineData(false, "ValidatePlacement")]
    [InlineData(false, "SettlePlacement")]
    [InlineData(false, "ReclassifyContinuation")]
    [InlineData(true, "Step")]
    [InlineData(true, "StepTowards")]
    [InlineData(true, "ValidatePlacement")]
    [InlineData(true, "SettlePlacement")]
    [InlineData(true, "ReclassifyContinuation")]
    public void SharedViewBindingsAuthenticateEnvironmentAtEveryDoor(bool publicExplicit, string door)
    {
        using var scene = CreateScene();
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        using var dry = CreateBinding(scene, view, null, publicExplicit, out var dryScope);
        using var wet = CreateBinding(scene, view, MovementWaterField.Level("world", 0, -4), publicExplicit, out var wetScope);
        var state = scene.State(new(0, -1, 0), false);
        Assert.Equal(MovementAvailability.Known, dry.TryAcquire(dryScope, out var dryRead));
        using (dryRead)
            Assert.Equal(MovementStepOutcome.Advanced, Execute(dry, door, state, dryRead!).Outcome);
        Assert.Equal(MovementAvailability.Known, wet.TryAcquire(wetScope, out var wetRead));
        using (wetRead)
        {
            Assert.True(wetRead!.UsesPhysicsView(view));
            Assert.Equal(MovementStepOutcome.PlacementRefused, wet.ValidatePlacement(state, wetRead).Outcome);
            Assert.Throws<ArgumentException>(() => Execute(dry, door, state, wetRead));
            wetRead.AssertCurrent();
        }
    }

    [Theory]
    [InlineData(false, "Step")]
    [InlineData(false, "StepTowards")]
    [InlineData(false, "ValidatePlacement")]
    [InlineData(false, "SettlePlacement")]
    [InlineData(false, "ReclassifyContinuation")]
    [InlineData(true, "Step")]
    [InlineData(true, "StepTowards")]
    [InlineData(true, "ValidatePlacement")]
    [InlineData(true, "SettlePlacement")]
    [InlineData(true, "ReclassifyContinuation")]
    public void ReconfiguredBindingRefusesStillLivePreviousContext(bool publicExplicit, string door)
    {
        using var scene = CreateScene();
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        using var previous = CreateBinding(scene, view, null, publicExplicit, out var previousScope);
        var state = scene.State(new(0, -1, 0), false);
        Assert.Equal(MovementAvailability.Known, previous.TryAcquire(previousScope, out var previousRead));
        using (previousRead)
        {
            previous.Dispose();
            using var current = CreateBinding(scene, view, null, publicExplicit, out var currentScope);
            Assert.Equal(previousScope, currentScope);
            previousRead!.AssertCurrent();
            Assert.True(previousRead.UsesPhysicsView(view));
            Assert.Throws<ArgumentException>(() => Execute(current, door, state, previousRead));
            previousRead.AssertCurrent();
        }
        using var replacement = CreateBinding(scene, view, null, publicExplicit, out var replacementScope);
        Assert.Equal(MovementAvailability.Known, replacement.TryAcquire(replacementScope, out var replacementRead));
        using (replacementRead)
            Assert.Equal(MovementStepOutcome.Advanced, Execute(replacement, door, state, replacementRead!).Outcome);
    }

    static OrdinaryContactScene CreateScene() => new(height: (_, _) => -4,
        extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4));

    static EffectiveMovementBinding CreateBinding(OrdinaryContactScene scene, IPhysicsWorldQueryView view,
        IMovementWaterField? field, bool publicExplicit, out MovementQueryScope scope)
    {
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        if (publicExplicit)
        {
            var provider = new OrdinaryMovementEnvironment(view, scene.Registration, scene.Tuning, scene.Height, null, boundary, field);
            var context = new MovementEnvironmentContext(view, provider, provider.Identity);
            scope = provider.CreateScope(scene.Frame);
            return EffectiveMovementBinding.PublicExplicit(scene.Tuning with { Controller = MovementControllerKind.Legacy },
                context, scene.Tuning.Water);
        }
        var binding = EffectiveMovementBinding.Ordinary(scene.Tuning, scene.Physics, scene.Registration,
            scene.Height, null, boundary, field, movementQueries: view);
        scope = binding.CreateOrdinaryScope(scene.Frame);
        return binding;
    }

    static MovementStepResult Execute(EffectiveMovementBinding binding, string door,
        FramedMovementState state, MovementQueryLease lease) => door switch
        {
            "Step" => binding.Step(state, new(Vector2.Zero, false, 0), 1f / 30f, lease),
            "StepTowards" => binding.StepTowards(state, Vector2.Zero, false, 1f / 30f, lease),
            "ValidatePlacement" => binding.ValidatePlacement(state, lease),
            "SettlePlacement" => binding.SettlePlacement(state, lease),
            "ReclassifyContinuation" => binding.ReclassifyContinuation(state, lease),
            _ => throw new ArgumentException("Unknown execution door.", nameof(door))
        };
}
