using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public partial class OrdinaryContactLeaseLifetimeTests
{
    [Fact]
    public void UnreadyTickUsesReconstructedSpaceInsteadOfTheCarriedSpace()
    {
        using var scene = new SelectionScene();
        FramedMovementState state = scene.State(new("world", "discarded"));
        MovementScopeWitness witness = scene.Lease.Witness;
        MovementStepResult result = scene.Tick(state);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(SelectionScene.Room, result.State.Selection!.Value.Space);
        Assert.Equal(SelectionScene.Room, scene.Environment.SampleSpaceObserved);
        Assert.Equal(1, scene.Environment.RebuildCalls);
        Assert.Same(witness, scene.Lease.Witness);
        Assert.Null(scene.Lease.Witness.Scope.CurrentSpace);
        Assert.True(result.State.State.Position.X > state.State.Position.X);
    }

    [Theory]
    [InlineData(MovementAvailability.Invalid, MovementStepOutcome.EnvironmentInvalid)]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.CapacityExceeded, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Stale, MovementStepOutcome.EnvironmentUnresolved)]
    public void TickPropagatesReconstructionRefusalAndKeepsDirectQueriesGuarded(
        MovementAvailability availability, MovementStepOutcome expected)
    {
        using var scene = new SelectionScene();
        FramedMovementState state = scene.State(SelectionScene.Room);
        scene.Environment.OnRebuild = (in FramedMovementState input, out MovementSelection selection) =>
        {
            selection = default;
            return availability;
        };
        MovementStepResult result = scene.Tick(state);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Null(result.State.Selection);
        Assert.Equal(1, scene.Environment.RebuildCalls);
        Assert.Equal(0, scene.Environment.CoverageCalls);
        Assert.Equal(MovementAvailability.Unresolved, scene.Lease.SampleCentreWater(new(
            state.State.Position, 0.25f, 0.75f, SelectionScene.Room, null)).Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadyHeldAndKnownHintTicksDoNotReconstructAgain(bool knownHint)
    {
        using var scene = new SelectionScene(knownHint);
        FramedMovementState state = scene.State(SelectionScene.Room);
        if (!knownHint) Assert.Equal(MovementAvailability.Known, scene.Lease.RebuildSelection(state, out _));
        scene.Environment.OnRebuild = (in FramedMovementState input, out MovementSelection selection) =>
        {
            selection = default;
            return MovementAvailability.Invalid;
        };
        for (int tick = 0; tick < 2; tick++)
        {
            MovementStepResult result = scene.Tick(state);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            state = result.State;
        }
        Assert.Equal(knownHint ? 0 : 1, scene.Environment.RebuildCalls);
    }

    [Theory]
    [InlineData("identity", MovementStepOutcome.EnvironmentInvalid)]
    [InlineData("hint", MovementStepOutcome.EnvironmentInvalid)]
    [InlineData("airborne-support", MovementStepOutcome.EnvironmentInvalid)]
    [InlineData("frame", MovementStepOutcome.FrameMismatch)]
    public void InvalidCarriedSelectionOrFrameCannotBeLaunderedByReconstruction(string fault, MovementStepOutcome expected)
    {
        using var scene = new SelectionScene(knownHint: fault == "hint");
        FramedMovementState basis = scene.State(fault == "hint" ? new("world", "other") : SelectionScene.Room);
        MovementSelection selection = basis.Selection!.Value;
        if (fault == "identity") selection = new(selection.Space, null, new("closure", 1, "wrong"));
        if (fault == "airborne-support") selection = new(selection.Space, new("world", "floor"), selection.Identity);
        MovementFrameDescriptor frame = fault == "frame"
            ? new(basis.Frame.Frame, basis.Frame.PhysicsOrigin, basis.Frame.Epoch + 1) : basis.Frame;
        MovementStepResult result = scene.Tick(new(basis.State, frame, selection));
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(basis.State.Position, result.State.State.Position);
        Assert.Equal(0, scene.Environment.RebuildCalls);
    }

    sealed class SelectionScene : IDisposable
    {
        internal static readonly MovementSpaceKey Room = new("world", "room");
        readonly BepuPhysicsWorld _world = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView _view;
        internal EnvironmentAcquisitionFixture Environment { get; }
        internal MovementQueryLease Lease { get; }
        static MoveTuning Tuning => MoveTuning.Default with { CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.75f };

        internal SelectionScene(bool knownHint = false)
        {
            _view = _world.CreateQueryViewExcludingStatics([]);
            var identity = new MovementQueryIdentity("closure", 1, "scope");
            var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
            var scope = new MovementQueryScope(new(-16), new(16), 4, 4, "world",
                knownHint ? Room : null, identity, frame);
            Environment = new(_view, scope) { ContactSupportEnabled = true };
            Environment.OnRebuild = (in FramedMovementState state, out MovementSelection selection) =>
            {
                Assert.Null(state.Selection);
                selection = new(Room, null, identity);
                return MovementAvailability.Known;
            };
            Environment.Point = new(MovementAvailability.Known, Room, null, false, 1, null);
            Environment.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
                Span<MovementDomainContact> contacts) =>
            {
                Assert.Equal(Room, query.Body.CurrentSpace);
                spans[0] = new(0, 1, 0, 0, true);
                return new(MovementAvailability.Known, 1, 1, 0, 0, 0, identity);
            };
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        }

        internal FramedMovementState State(MovementSpaceKey space) =>
            new(new MoveState { Position = new(0, 2, 0) }, Lease.Frame, new(space, null, Lease.Identity));

        internal MovementStepResult Tick(FramedMovementState state) => ExplicitCharacterMovement.StepTowards(
            state, Vector2.UnitX, false, 1f / 30f, Tuning, new(WaterTraversalMode.DryOnly), Lease);

        public void Dispose() { Lease.Dispose(); _view.Dispose(); _world.Dispose(); }
    }
}
