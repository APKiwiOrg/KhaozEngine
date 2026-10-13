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
    public void CapturedBoundaryIsLocalToItsFrameAndKeepsTheConfiguredIdentity()
    {
        var absolute = MovementBoundary.Rectangle(7, -17, 11, -13);
        using var exact = new OrdinaryContactScene(origin: new(8, 10, -16), boundary: absolute);
        var local = exact.Binding.CaptureOrdinaryBoundary(exact.Frame);
        Assert.Same(local, exact.Binding.CaptureOrdinaryBoundary(exact.Frame));
        Assert.Equal(absolute.SemanticIdentity, local.SemanticIdentity);
        Assert.Equal(MovementAvailability.Known, local.Classify(new(1, 0, 1), out bool inside));
        Assert.True(inside);
        Assert.Equal(MovementAvailability.Known, local.Constrain(new(1, 0, 1), new(4, 0, 0), out var clipped));
        Assert.Equal(new Vector3(2, 0, 0), clipped);
        Assert.Equal(MovementAvailability.Known, local.ProveSegment(new(1, 0, 1), clipped, new(3, 0, 1), out bool admitted));
        Assert.True(admitted);
        // A lossy origin no longer refuses. The local rectangle is far from this centre.
        using var lossy = new OrdinaryContactScene(origin: new(16777216, 0, 0), boundary: absolute,
            extent: new(new(16777196, -4, -20), new(16777236, 4, 20), 0, 0));
        var far = lossy.Binding.CaptureOrdinaryBoundary(lossy.Frame);
        Assert.Equal(absolute.SemanticIdentity, far.SemanticIdentity);
        Assert.Equal(MovementAvailability.Known, far.Classify(Vector3.One, out inside));
        Assert.False(inside);
        // A custom shape has no frame capture, so ordinary configuration refuses it.
        Assert.Throws<ArgumentException>(() => EffectiveMovementBinding.Ordinary(exact.Tuning, exact.Physics,
            exact.Registration, exact.Height, null, new PointOnlyBoundary()));
    }

    sealed class PointOnlyBoundary : MovementBoundary
    {
        public override string SemanticIdentity => "point-only";
        public override MovementAvailability Classify(Vector3 centre, out bool inside) { inside = true; return MovementAvailability.Known; }
        public override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained)
        { constrained = displacement; return MovementAvailability.Known; }
        public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted)
        { admitted = true; return MovementAvailability.Known; }
    }

    [Fact]
    public void CapturedBoundaryAtOrigin256AdmitsConstrainsAndRefusesAgainstTheTrueEdge()
    {
        const double maxX = 266.3;
        double edge = maxX - 256;
        var absolute = MovementBoundary.Rectangle(236, -20, maxX, 20);
        using var scene = new OrdinaryContactScene(origin: new(256, 0, 0), boundary: absolute,
            extent: new(new(236, -4, -20), new(276, 4, 20), 0, 0));
        var local = scene.Binding.CaptureOrdinaryBoundary(scene.Frame);
        // The explicit heads capture the same double rectangle with the configured identity.
        var explicitHead = MovementBoundary.Rectangle(236d - 256, -20, edge, 20, absolute.SemanticIdentity);
        Assert.Equal(absolute.SemanticIdentity, local.SemanticIdentity);

        Assert.Equal(MovementAvailability.Known, local.Constrain(new(0, 0.75f, 0), new(0.4f, 0, 0), out var step));
        Assert.Equal(new Vector3(0.4f, 0, 0), step);

        Assert.Equal(MovementAvailability.Known, local.Classify(new(10.29f, 0.75f, 0), out bool inside));
        Assert.True(inside);

        Vector3 start = new(10, 0.75f, 0);
        Assert.Equal(MovementAvailability.Known, local.Constrain(start, new(1, 0, 0), out var clipped));
        Assert.Equal(MovementAvailability.Known, explicitHead.Constrain(start, new(1, 0, 0), out var expected));
        Assert.Equal(expected, clipped);
        float end = start.X + clipped.X;
        Assert.True(end <= edge);
        Assert.True(end >= edge - 2d * MovementQueryLease.CoverageSkinMetres - 1e-6);
        Assert.Equal(MovementAvailability.Known, local.ProveSegment(start, clipped, start + clipped, out bool admitted));
        Assert.True(admitted);

        Vector3 outside = new(10.31f, 0.75f, 0);
        Assert.Equal(MovementAvailability.Known, local.Classify(outside, out inside));
        Assert.False(inside);
        Assert.Equal(MovementAvailability.Unresolved, local.Constrain(outside, new(0.1f, 0, 0), out _));
    }

    internal static MoveTuning FramedProofTuning => MoveTuning.Default with
    {
        Controller = MovementControllerKind.Contact,
        Water = new(WaterTraversalMode.DryOnly),
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        WalkSpeed = 4,
        StepHeight = 0.4f
    };

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(256f, 0f)]
    [InlineData(4096f, 0f)]
    [InlineData(256f, -512f)]
    public void FramedDryStepAdvancesAndEqualsTheDirectDryPath(float originX, float originZ)
    {
        using var scene = new OrdinaryContactScene(tuning: FramedProofTuning, origin: new(originX, 0, originZ),
            extent: new(new(originX - 20, -8, originZ - 20), new(originX + 20, 8, originZ + 20), 0, 0),
            boundary: MovementBoundary.Rectangle(originX - 20, originZ - 20, originX + 20, originZ + 20),
            install: (physics, builder) =>
            {
                var floor = physics.AddStatic(new BoxShape(new(20, 0.125f, 20)), Pose.At(new(originX, -0.125f, originZ)));
                builder.Add(floor, 0, int.MaxValue, new("world", "floor"),
                    new(originX - 20, -0.25f, originZ - 20), new(originX + 20, 0, originZ + 20));
            });
        Assert.Equal(new Vector3(originX, 0, originZ), scene.Physics.Origin);
        var state = new MoveState { Position = new(0, 0.75f, 0), Grounded = true };
        var command = new MoveCommand(Vector2.UnitX, false, 0);
        for (int tick = 0; tick < 3; tick++)
        {
            var direct = CharacterMovement.Step(state, command, 0.1f, (_, _) => 0, scene.Tuning, null, scene.Physics);
            using var lease = scene.Acquire();
            var ordinary = scene.Binding.Step(new(state, scene.Frame, null), command, 0.1f, lease);
            Assert.Equal(MovementStepOutcome.Advanced, ordinary.Outcome);
            Assert.Equal(direct, ordinary.State.State);
            Assert.True(direct.Position.X > state.Position.X);
            state = direct;
        }
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
