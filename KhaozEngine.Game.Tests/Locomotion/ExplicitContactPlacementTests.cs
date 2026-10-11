using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class ExplicitContactPlacementTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementSupportKey Floor = new("world", "floor");
    static readonly MoveTuning Tuning = MoveTuning.Default with { CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.75f };
    static readonly WaterTraversalPolicy Policy = new(WaterTraversalMode.SurfaceSwimmer, 7);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Q3CorrectedPortalPoseBindsOnlyTheReconstructedDestination(int door)
    {
        using var scene = new Scene();
        var predicted = new MovementSelection(new("world", "predicted-room"), new("world", "predicted-floor"), scene.Lease.Identity);
        var input = new FramedMovementState(new MoveState { Position = new(0, 0.751f, 0), Grounded = true }, scene.Lease.Frame, predicted);
        MovementStepResult result = Admit(door, input, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(Room, result.State.Selection!.Value.Space);
        Assert.Equal(Floor, result.State.Selection.Value.Support);
        Assert.True(scene.Environment.ContactSupportCalls > 0);
        Assert.Equal(input.State.Position, result.State.State.Position);
    }

    [Theory]
    [InlineData(0, "owner")]
    [InlineData(1, "owner")]
    [InlineData(2, "owner")]
    [InlineData(0, "missing")]
    [InlineData(1, "missing")]
    [InlineData(2, "missing")]
    [InlineData(0, "link")]
    [InlineData(1, "link")]
    [InlineData(2, "link")]
    public void Q4AdmissionCannotPublishAChangedMissingOrUnlinkedFiniteOwner(int door, string fault)
    {
        using var scene = new Scene();
        MovementContactSupportBinding known = default;
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            known = binding = new(Floor, Room, null, scene.Lease.Identity);
            return MovementAvailability.Known;
        };
        var body = new MovementBodyQuery(new(0, 0.751f, 0), 0.25f, 0.75f, Room, null);
        var query = new KhaozEngine.Locomotion.Contacts.FootSupportQuery(Vector2.Zero, 0, 0.125f, 0.4f, 0.4f, 0.7f);
        Assert.Equal(MovementAvailability.Known, scene.Lease.RebuildSelection(scene.State(), out _));
        Assert.Equal(MovementAvailability.Known, scene.Lease.FindContactSupport(
            new(body, 0.4f, 0.4f, Tuning.MaxSlopeRadians, new(Room, Vector3.Zero)), query, out _, out _));
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            binding = fault switch
            {
                "owner" => new(new("world", "changed-floor"), known.Space, null, known.Identity),
                "link" => new(known.Owner, new("world", "other-room"), null, known.Identity),
                _ => default,
            };
            return fault == "missing" ? MovementAvailability.Unresolved : MovementAvailability.Known;
        };
        var input = scene.State();
        MovementStepResult result = Admit(door, input, scene.Lease);
        Assert.Equal(fault == "missing" ? MovementStepOutcome.EnvironmentUnresolved : MovementStepOutcome.EnvironmentInvalid, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
        Assert.Null(result.State.Selection);
    }

    [Fact]
    public void LiveLandContinuationPreservesEveryAcceptedTimerAndEventWithoutSeating()
    {
        using var scene = new Scene();
        var basis = scene.State();
        var input = new FramedMovementState(basis.State with
        {
            Position = new(0, 0.761f, 0),
            JumpBufferRemaining = 0.08f,
            TimeSinceGrounded = 0.03f,
            StepDeltaY = 0.2f,
            ClimbRate = 0.6f,
            ClimbRateEwma = 0.5f,
            LandingImpactSpeed = 3,
            SupportGranted = true,
            CommandedVelocity = new(4, 0),
        }, basis.Frame, null);
        var result = ExplicitCharacterMovement.ReclassifyContinuation(input, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(input.State, result.State.State);
        Assert.Equal(Floor, result.State.Selection!.Value.Support);
        Assert.True(scene.Environment.ContactSupportCalls > 0);
    }

    [Fact]
    public void LateBindingFailureAfterAColdSeatPublishesNoPrefixOrTickEffects()
    {
        using var scene = new Scene();
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            binding = new(Floor, Room, null, scene.Lease.Identity);
            return request.Body.Centre.Y < 0.76f ? MovementAvailability.Unresolved : MovementAvailability.Known;
        };
        var basis = scene.State();
        var input = new FramedMovementState(basis.State with
        {
            Position = new(0, 0.761f, 0),
            JumpBufferRemaining = 0.08f,
            LandingImpactSpeed = 3,
            StepDeltaY = 0.2f,
            SupportGranted = true,
        }, basis.Frame, null);
        var result = ExplicitCharacterMovement.SettlePlacement(input, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Equal(0, result.State.State.LandingImpactSpeed);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.False(result.State.State.SupportGranted);
        Assert.Null(result.State.Selection);
    }

    static MovementStepResult Admit(int door, FramedMovementState state, MovementQueryLease lease) => door switch
    {
        0 => ExplicitCharacterMovement.ValidatePlacement(state, Tuning, Policy, lease),
        1 => ExplicitCharacterMovement.SettlePlacement(state, Tuning, Policy, lease),
        _ => ExplicitCharacterMovement.ReclassifyContinuation(state, Tuning, Policy, lease),
    };

    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld world = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView view;
        public EnvironmentAcquisitionFixture Environment { get; }
        public MovementQueryLease Lease { get; }
        public Scene()
        {
            var floor = world.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, -0.125f, 0)));
            view = world.CreateQueryViewExcludingStatics([]);
            var identity = new MovementQueryIdentity("closure", 1, "scope");
            var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
            Environment = new(view, new(new(-16), new(16), 4, 4, "world", null, identity, frame)) { ContactSupportEnabled = true };
            Environment.OnRebuild = (in FramedMovementState state, out MovementSelection selection) =>
            {
                Assert.Null(state.Selection);
                selection = new(Room, null, identity);
                return MovementAvailability.Known;
            };
            Environment.Point = new(MovementAvailability.Known, Room, null, false, 1, null);
            Environment.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            {
                spans[0] = new(0, 1, 0, 0, true);
                return new(MovementAvailability.Known, 1, 1, 0, 0, 0, identity);
            };
            Environment.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            {
                candidates[0] = new(Floor, Room, new(request.Body.Centre.X, 0, request.Body.Centre.Z), Vector3.UnitY, null);
                return new(MovementAvailability.Known, 1, 1, identity);
            };
            Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
            {
                Assert.Equal(floor, request.Static);
                Assert.Equal(Room, request.Transition.OriginSpace);
                binding = new(Floor, Room, null, identity);
                return MovementAvailability.Known;
            };
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        }
        public FramedMovementState State() => new(new MoveState { Position = new(0, 0.751f, 0), Grounded = true }, Lease.Frame, null);
        public void Dispose() { Lease.Dispose(); view.Dispose(); world.Dispose(); }
    }
}
