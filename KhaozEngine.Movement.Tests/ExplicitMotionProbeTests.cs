using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Movement;

public class ExplicitMotionProbeTests
{
    static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        WalkSpeed = 4,
        SwimSpeed = 3,
        Gravity = 20,
        GroundedEpsilon = 0.05f
    };
    static ExplicitMovementProfile Profile(WaterTraversalMode mode = WaterTraversalMode.SurfaceSwimmer, int steps = 64) =>
        new("fixture", Tuning, new(mode, 7), new NavBakeSources().AddHashOf("query-policy", new byte[] { 1 }),
            stepSeconds: 1f / 30, maxSteps: steps);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeEntryAndProbeShareTheExplicitKernelAndCapturedView(bool wet)
    {
        using var fixture = new Scene(wet);
        var profile = Profile();
        var state = fixture.State;
        var direct = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, profile.StepSeconds,
            profile.Tuning, profile.Water, fixture.Lease);
        var runtime = fixture.Context.StepExplicit(state, Vector2.UnitX, false, profile, fixture.Lease);
        Assert.Equal(direct, runtime);
        var proof = fixture.Context.ProbeExplicitMotion(state, state.State.Position + Vector3.UnitX, wet, profile, fixture.Lease);
        Assert.Equal(ExplicitMotionProofStatus.Proven, proof.Status);
        Assert.NotNull(proof.End);
        Assert.InRange(proof.End.Value.State.Position.X, 0.999f, 1.001f);
        Assert.Equal(profile.Fingerprint, proof.ProfileFingerprint);
    }

    [Fact]
    public void RuntimeAndProbeSeeTheSameRealWall()
    {
        using var fixture = new Scene(wet: true, wall: true);
        var proof = fixture.Context.ProbeExplicitMotion(fixture.State, new(3, 0.85f, 0), true, Profile(), fixture.Lease);
        Assert.Equal(ExplicitMotionProofStatus.Blocked, proof.Status);
        Assert.Null(proof.End);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved, ExplicitMotionProofStatus.Unresolved)]
    [InlineData(MovementAvailability.CapacityExceeded, ExplicitMotionProofStatus.Unresolved)]
    [InlineData(MovementAvailability.Invalid, ExplicitMotionProofStatus.Invalid)]
    public void AQueryRefusalIsNeverAProvedBlockedEdge(MovementAvailability failure, ExplicitMotionProofStatus expected)
    {
        using var fixture = new Scene(wet: true);
        fixture.Environment.MissingContainment = failure == MovementAvailability.Unresolved;
        fixture.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> _,
            Span<MovementDomainContact> _) => new(failure, 0, 0, 0, 0, 0, Identity);
        var proof = fixture.Context.ProbeExplicitMotion(fixture.State, new(1, 0.85f, 0), true, Profile(), fixture.Lease);
        Assert.Equal(expected, proof.Status);
        Assert.Null(proof.End);
    }

    [Fact]
    public void ExhaustingTheFiniteProbeBudgetIsUnresolved()
    {
        using var fixture = new Scene(wet: true);
        var proof = fixture.Context.ProbeExplicitMotion(fixture.State, new(3, 0.85f, 0), true, Profile(steps: 1), fixture.Lease);
        Assert.Equal(ExplicitMotionProofStatus.BudgetExceeded, proof.Status);
        Assert.Null(proof.End);
    }

    [Fact]
    public void ASecondViewOfTheSameWorldCannotSubstituteForTheCapturedSelection()
    {
        using var fixture = new Scene(wet: true);
        var other = fixture.Environment.Physics.CreateQueryViewExcludingStatics([]);
        try
        {
            var mismatched = new GroundMoveContext((_, _) => throw new InvalidOperationException("Legacy called."), null,
                fixture.Environment.Physics, null, null, other);
            var result = mismatched.StepExplicit(fixture.State, Vector2.UnitX, false, Profile(), fixture.Lease);
            Assert.Equal(MovementStepOutcome.EnvironmentInvalid, result.Outcome);
            Assert.Equal(fixture.State.State.Position, result.State.State.Position);
        }
        finally { fixture.Lease.Dispose(); other.Dispose(); }
    }


    [Fact]
    public void ARefusalAfterEarlierProbeStepsExposesNoUsablePrefix()
    {
        using var fixture = new Scene(wet: true);
        var real = fixture.Environment.Acquisition.OnCoverage!;
        bool later = false;
        fixture.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            if (query.Body.Centre.X > 0.2f)
            {
                later = true;
                return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, Identity);
            }
            return real(query, spans, contacts);
        };
        var proof = fixture.Context.ProbeExplicitMotion(fixture.State, new(1, 0.85f, 0), true, Profile(), fixture.Lease);
        Assert.True(later);
        Assert.True(proof.Steps > 1);
        Assert.Equal(ExplicitMotionProofStatus.Unresolved, proof.Status);
        Assert.Null(proof.End);
    }

    [Fact]
    public void AProfileCannotSilentlyOmitItsCapturedBoundary()
    {
        using var fixture = new Scene(wet: true);
        var boundary = MovementBoundary.Rectangle(-1, -1, 1, 1);
        var result = fixture.Context.StepExplicit(fixture.State, Vector2.UnitX, false, Profile(), fixture.Lease, boundary);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid, result.Outcome);
        Assert.Equal(fixture.State.State.Position, result.State.State.Position);
    }

    [Fact]
    public void AForbiddenWetStartIsBlockedButAChangedFrameIsNot()
    {
        using var fixture = new Scene(wet: true);
        var blocked = fixture.Context.ProbeExplicitMotion(fixture.State, new(1, 0.85f, 0), true,
            Profile(WaterTraversalMode.DryOnly), fixture.Lease);
        Assert.Equal(ExplicitMotionProofStatus.Blocked, blocked.Status);
        var frame = fixture.State.Frame;
        var stale = new FramedMovementState(fixture.State.State, new(frame.Frame, frame.PhysicsOrigin, frame.Epoch + 1), null);
        var mismatch = fixture.Context.ProbeExplicitMotion(stale, new(1, 0.85f, 0), true, Profile(), fixture.Lease);
        Assert.Equal(ExplicitMotionProofStatus.FrameMismatch, mismatch.Status);
        Assert.Null(mismatch.End);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticAdjacentFramesProveTheSameWorldEdgeWithoutSharingLeaseIdentity(bool wet)
    {
        using var left = new Scene(wet, origin: new(127, 0, 0));
        using var right = new Scene(wet, origin: new(128, 0, 0));
        Assert.NotEqual(left.Lease.Id, right.Lease.Id);
        var move = left.State.State;
        move.Position.X = 0.2f;
        var start = new FramedMovementState(move, left.Lease.Frame, null);
        Assert.True(MovementFrameRebinding.TryRebind(start, right.Lease.Frame, out var rebound));
        Assert.Null(rebound.Selection);
        var a = Profile();
        var b = Profile();
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        var first = left.Context.ProbeExplicitMotion(start, new(1.5f, move.Position.Y, 0), wet, a, left.Lease);
        var second = right.Context.ProbeExplicitMotion(rebound, new(0.5f, move.Position.Y, 0), wet, b, right.Lease);
        Assert.Equal(ExplicitMotionProofStatus.Proven, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.NotNull(first.End);
        Assert.NotNull(second.End);
        double x1 = (double)first.End.Value.State.Position.X + first.End.Value.Frame.PhysicsOrigin.X;
        double x2 = (double)second.End.Value.State.Position.X + second.End.Value.Frame.PhysicsOrigin.X;
        Assert.InRange(Math.Abs(x1 - x2), 0, 0.001);
        Assert.Equal(first.End.Value.State.Swimming, second.End.Value.State.Swimming);
    }

    sealed class Scene : IDisposable
    {
        public AnalyticMovementEnvironment Environment { get; }
        public MovementQueryLease Lease { get; }
        public GroundMoveContext Context { get; }
        public FramedMovementState State { get; }
        public Scene(bool wet, bool wall = false, Vector3 origin = default)
        {
            Environment = new([new Room("room", new(new(-16), new(16)))], wet
                ? [new Water("lake", "room", new(new(-8, -4, -8), new(8, 1, 8)), 1)] : []);
            Environment.Physics.AddStatic(new BoxShape(new(8, 0.1f, 8)), Pose.At(origin + new Vector3(0, (wet ? -4 : 0) - 0.1f, 0)));
            if (wall) Environment.Physics.AddStatic(new BoxShape(new(0.03125f, 4, 8)), Pose.At(origin + new Vector3(2, 0, 0)));
            if (origin != Vector3.Zero) Environment.Physics.Rebase(origin);
            Lease = Environment.Acquire(null, 1, 4, WorldFrame.Nearest(origin));
            Environment.Acquisition.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            {
                float y = wet ? -4 : 0;
                candidates[0] = new(new("world", "floor"), Space("room"), new(request.Body.Centre.X, y, request.Body.Centre.Z), Vector3.UnitY, null);
                return new(MovementAvailability.Known, 1, 1, Identity);
            };
            Context = new((_, _) => throw new InvalidOperationException("Legacy height called."), null, Environment.Physics,
                null, (_, _, _) => throw new InvalidOperationException("Legacy medium called."), Environment.View);
            State = new(new MoveState
            {
                Position = new(0, wet ? 0.85f : 0.751f, 0),
                Grounded = !wet,
                Swimming = wet,
                WaterExcursion = wet ? WaterExcursionState.Surface : WaterExcursionState.None
            }, Lease.Frame, null);
        }
        public void Dispose() { Lease.Dispose(); Environment.Dispose(); }
    }
}
