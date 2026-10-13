using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementBindingTests
{
    [Fact]
    public void OwnedBorrowedAndFailedFactoryViewsHaveExactDisposeCounts()
    {
        using var world = new ObservedPhysicsWorld();
        OrdinaryMovementRegistration registration;
        using (var read = world.AcquireQueryReadLease()) registration = OrdinaryMovementRegistration.CreateBuilder(
            "world", world, new(new(-20, -4, -20), new(20, 4, 20), 0, 0)).Build(read);
        var tuning = OrdinaryContactScene.DefaultTuning;
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        using var borrowed = world.CreateQueryViewExcludingStatics([]);
        var borrower = EffectiveMovementBinding.Ordinary(tuning, world, registration, (_, _) => 0, null,
            boundary, movementQueries: borrowed);
        borrower.Dispose();
        borrower.Dispose();
        Assert.Equal(0, world.ViewDisposals);
        var owned = EffectiveMovementBinding.Ordinary(tuning, world, registration, (_, _) => 0, null, boundary);
        owned.Dispose();
        owned.Dispose();
        Assert.Equal(1, world.ViewDisposals);
        Assert.Equal(0, world.SourceDisposals);
        Assert.Throws<ArgumentException>(() => EffectiveMovementBinding.Ordinary(tuning, world, registration,
            (_, _) => 0, null, boundary, medium: (_, _, _) => MovementMedium.Dry));
        Assert.Equal(2, world.ViewDisposals);
        Assert.Throws<ArgumentException>(() => EffectiveMovementBinding.Ordinary(tuning, world, registration,
            (_, _) => 0, null, boundary, medium: (_, _, _) => MovementMedium.Dry, movementQueries: borrowed));
        Assert.Equal(2, world.ViewDisposals);
        Assert.Equal(3, world.ViewCreations);
        using var stillLive = ((IPhysicsQueryLeaseSource)borrowed).AcquireQueryReadLease();
        stillLive.AssertCurrent();
    }

    [Fact]
    public void OrdinaryBindingUsesZeroOffsetOnAllFiveDoorsAndRefusesForeignReads()
    {
        using var scene = new OrdinaryContactScene();
        using var other = new OrdinaryContactScene();
        using var lease = scene.Acquire();
        var state = scene.State(new(0, 0.75f, 0));
        var settled = scene.Binding.SettlePlacement(state, lease);
        Assert.Equal(MovementStepOutcome.Advanced, settled.Outcome);
        Assert.Equal(0.75f, settled.State.State.Position.Y);
        Assert.Equal(settled.State.State, scene.Binding.ValidatePlacement(settled.State, lease).State.State);
        Assert.Equal(settled.State.State, scene.Binding.ReclassifyContinuation(settled.State, lease).State.State);
        Assert.Equal(scene.Binding.StepTowards(state, Vector2.UnitX, false, 1f / 30f, lease).State.State,
            scene.Binding.Step(state, new(Vector2.UnitX, false, 0), 1f / 30f, lease).State.State);
        Assert.Throws<ArgumentException>(() => other.Binding.ValidatePlacement(state, lease));
    }

    [Fact]
    public void CapturedBoundaryConvertsExactlyAndRefusesLossyTranslation()
    {
        var absolute = MovementBoundary.Rectangle(7, -17, 11, -13);
        var local = new OrdinaryMovementBoundary(absolute, new(WorldFrame.Origin, new(8, 10, -16), 1));
        Assert.Equal(MovementAvailability.Known, local.Classify(new(1, 0, 1), out bool inside));
        Assert.True(inside);
        Assert.Equal(MovementAvailability.Known, local.Constrain(new(1, 0, 1), new(4, 0, 0), out var clipped));
        Assert.Equal(new Vector3(2, 0, 0), clipped);
        Assert.Equal(MovementAvailability.Known, local.ProveSegment(new(1, 0, 1), clipped, new(3, 0, 1), out bool admitted));
        Assert.True(admitted);
        var lossy = new OrdinaryMovementBoundary(absolute, new(WorldFrame.Origin, new(16777216, 0, 0), 2));
        Assert.Equal(MovementAvailability.Unresolved, lossy.Classify(Vector3.One, out _));
    }

    [Fact]
    public void FactoriesKeepLegacyViewFreeAndPublicExplicitContextBorrowed()
    {
        using var legacy = EffectiveMovementBinding.Legacy(MoveTuning.Default);
        Assert.Equal(EffectiveMovementBindingKind.Legacy, legacy.Kind);
        Assert.False(legacy.UsesLease);
        Assert.Equal(default, legacy.Water);
        using var scene = new OrdinaryContactScene();
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        var provider = new OrdinaryMovementEnvironment(view, scene.Registration, scene.Tuning, scene.Height, null, boundary);
        var context = new MovementEnvironmentContext(view, provider, provider.Identity);
        var dry = new WaterTraversalPolicy(WaterTraversalMode.DryOnly);
        Assert.Throws<ArgumentException>(() => EffectiveMovementBinding.PublicExplicit(scene.Tuning, context, dry));
        using var binding = EffectiveMovementBinding.PublicExplicit(MoveTuning.Default, context, dry);
        Assert.Equal(EffectiveMovementBindingKind.PublicExplicit, binding.Kind);
        Assert.True(binding.UsesLease);
        Assert.Equal(dry, binding.Water);
        Assert.Throws<InvalidOperationException>(() => binding.CreateOrdinaryScope(scene.Frame));
        binding.Dispose();
        binding.Dispose();
        Assert.Equal(MovementAvailability.Known, context.TryAcquire(provider.CreateScope(scene.Frame), out var read));
        using (read) read!.AssertCurrent();
        Assert.Throws<ArgumentException>(() => EffectiveMovementBinding.Ordinary(scene.Tuning, scene.Physics, scene.Registration,
            scene.Height, null, boundary, waterField: MovementWaterField.Level("world", 10, -4)));
        using var other = new OrdinaryContactScene();
        Assert.Throws<ArgumentException>(() => EffectiveMovementBinding.Ordinary(scene.Tuning, scene.Physics, other.Registration,
            scene.Height, null, boundary));
    }
}
