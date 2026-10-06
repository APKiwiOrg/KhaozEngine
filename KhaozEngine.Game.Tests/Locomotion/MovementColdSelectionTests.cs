using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Synthetic acquisition/reconstruction lifecycle only. Native membership geometry remains G1b.
public class MovementColdSelectionTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementSpaceKey Future = new("world", "discarded-future");
    static readonly MovementQueryIdentity Identity = new("closure", 1u, "scope");
    static readonly MovementFrameDescriptor Frame = new(WorldFrame.Origin, Vector3.Zero, 1ul);

    [Fact]
    public void NullHintRebuildAndBodyQueryUseTheSameUnchangedWitness()
    {
        using var scene = new Scene(ColdScope());
        MovementQueryLease lease = scene.Acquire();
        MovementScopeWitness witness = lease.Witness;
        Assert.Null(Hint(witness.Scope));
        AssertBlocked(scene, lease);
        scene.Environment.OnRebuild = (in FramedMovementState state, out MovementSelection selection) =>
        {
            Assert.Null(state.Selection);
            selection = Selection(Room);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future), out MovementSelection rebuilt));
        Assert.Equal(Room, rebuilt.Space);
        scene.Environment.Point = Dry(Room);
        Assert.Equal(MovementAvailability.Known, lease.SampleCentreWater(Body(rebuilt.Space)).Availability);
        Assert.Equal(Room, scene.Environment.SampleSpaceObserved);
        AssertKnownSupportAndCoverage(scene, lease, rebuilt.Space);
        Assert.Same(witness, lease.Witness);
        Assert.Null(Hint(lease.Witness.Scope));
        Assert.Equal(Identity, lease.Identity);
        Assert.Equal(1, scene.Environment.WitnessReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrThrowingRebuildClearsEarlierReadinessUntilNewKnownRebuild(bool throws)
    {
        using var scene = new Scene(ColdScope());
        MovementQueryLease lease = scene.Acquire();
        MovementScopeWitness witness = lease.Witness;
        scene.Environment.OnRebuild = Known;
        Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future), out _));
        scene.Environment.Point = Dry(Room);
        Assert.Equal(MovementAvailability.Known, lease.SampleCentreWater(Body(Room)).Availability);
        int samples = scene.Environment.SampleCalls;
        scene.Environment.OnRebuild = (in FramedMovementState state, out MovementSelection selection) =>
        {
            selection = Selection(Future);
            if (throws) throw new EnvironmentAcquisitionFixture.FixtureException();
            return MovementAvailability.Unresolved;
        };
        if (throws)
        {
            MovementSelection leaked = Selection(Future);
            Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(() => Rebuild(lease, State(Future), out leaked));
            Assert.Equal(default, leaked);
        }
        else
        {
            Assert.Equal(MovementAvailability.Unresolved, Rebuild(lease, State(Future), out MovementSelection refused));
            Assert.Equal(default, refused);
        }
        AssertBlocked(scene, lease, samples);
        scene.Environment.OnRebuild = Known;
        Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future), out _));
        Assert.Equal(MovementAvailability.Known, lease.SampleCentreWater(Body(Room)).Availability);
        Assert.Same(witness, lease.Witness);
        Assert.Null(Hint(lease.Witness.Scope));
        Assert.Equal(1, scene.Environment.WitnessReads);
    }

    [Fact]
    public void KnownHintCertificateCannotReconstructEvenWithClearedStateSelection()
    {
        using var scene = new Scene(KnownScope(Future));
        MovementQueryLease lease = scene.Acquire();
        scene.Environment.OnRebuild = Known;
        FramedMovementState state = new(new MoveState { Position = Vector3.Zero }, Frame, null);
        Assert.Equal(MovementAvailability.Invalid, Rebuild(lease, state, out MovementSelection selection));
        Assert.Equal(default, selection);
        Assert.Equal(0, scene.Environment.RebuildCalls);
        AssertBlocked(scene, lease);
    }

    [Theory]
    [InlineData("null-to-known")]
    [InlineData("known-to-null")]
    [InlineData("known-to-other")]
    public void AcquisitionCannotReuseADifferentlyHintedCertificate(string mode)
    {
        using var scene = new Scene(mode == "null-to-known" ? ColdScope() : KnownScope(Room));
        scene.Environment.WitnessScope = mode switch
        {
            "null-to-known" => KnownScope(Room),
            "known-to-null" => ColdScope(),
            _ => KnownScope(Future)
        };
        var result = scene.AcquireRaw();
        Assert.NotEqual(MovementAvailability.Known, result.Status);
        Assert.Null(result.Lease);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("cross-world")]
    public void PresentHintsMustBeValidAndBelongToTheRequiredWorld(string kind)
    {
        MovementSpaceKey? hint = kind == "invalid" ? default(MovementSpaceKey) : new MovementSpaceKey("elsewhere", "room");
        Assert.ThrowsAny<ArgumentException>(() => ColdScope(hint: hint));
    }

    [Fact]
    public void RequestWorldCannotRelabelTheActualProviderWorld()
    {
        using var scene = new Scene(ColdScope(world: "elsewhere"));
        var result = scene.AcquireRaw();
        Assert.Equal(MovementAvailability.Invalid, result.Status);
        Assert.Null(result.Lease);
        Assert.False(scene.Environment.Prepared);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved)]
    [InlineData(MovementAvailability.CapacityExceeded)]
    public void MissingOrOverBudgetMembershipDependenciesCannotAcquireAKnownLease(MovementAvailability refusal)
    {
        using var scene = new Scene(ColdScope());
        scene.Environment.PrepareAvailability = refusal;
        var result = scene.AcquireRaw();
        Assert.Equal(refusal, result.Status);
        Assert.Null(result.Lease);
        Assert.False(scene.Environment.Pinned);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("world")]
    [InlineData("airborne-support")]
    [InlineData("stale")]
    public void UnvalidatedReconstructionCannotEnableSelectedQueries(string fault)
    {
        using var scene = new Scene(ColdScope());
        MovementQueryLease lease = scene.Acquire();
        scene.Environment.OnRebuild = (in FramedMovementState state, out MovementSelection selection) =>
        {
            var space = fault == "world" ? new MovementSpaceKey("elsewhere", "room") : Room;
            var identity = fault == "identity" ? new MovementQueryIdentity("closure", 1u, "wrong") : Identity;
            selection = new MovementSelection(space, new MovementSupportKey(space.WorldId, "floor"), identity);
            if (fault == "stale") scene.Environment.Fault = "stale-pin";
            return MovementAvailability.Known;
        };
        FramedMovementState input = State(Future, grounded: fault != "airborne-support");
        Assert.NotEqual(MovementAvailability.Known, Rebuild(lease, input, out MovementSelection selection));
        Assert.Equal(default, selection);
        AssertBlocked(scene, lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameOrPointPreconditionFailureClearsEarlierReadiness(bool outside)
    {
        using var scene = new Scene(ColdScope());
        MovementQueryLease lease = scene.Acquire();
        scene.Environment.OnRebuild = Known;
        Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future), out _));
        FramedMovementState invalid = outside
            ? State(Future, position: new Vector3(100f, 0f, 0f))
            : new FramedMovementState(State(Future).State, new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 2ul), null);
        Assert.NotEqual(MovementAvailability.Known, Rebuild(lease, invalid, out MovementSelection selection));
        Assert.Equal(default, selection);
        Assert.Equal(1, scene.Environment.RebuildCalls);
        AssertBlocked(scene, lease);
    }

    [Fact]
    public void NullHintDoesNotFilterStackedMembershipThroughTheDiscardedFuture()
    {
        using var scene = new Scene(ColdScope());
        scene.Environment.Resources = ["lower-floor", "lower-ceiling", "upper-floor", "upper-ceiling", "aliases"];
        MovementQueryLease lease = scene.Acquire();
        scene.Environment.OnRebuild = (in FramedMovementState state, out MovementSelection selection) =>
        {
            Assert.Null(state.Selection);
            var space = new MovementSpaceKey("world", state.State.Position.Y < 0f ? "lower" : "upper");
            selection = Selection(space);
            scene.Environment.Point = Dry(space);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future, position: new Vector3(0f, -2f, 0f)), out var lower));
        Assert.Equal("lower", lower.Space.LocalId);
        Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future, position: new Vector3(0f, 2f, 0f)), out var upper));
        Assert.Equal("upper", upper.Space.LocalId);
        Assert.Null(Hint(lease.Witness.Scope));
        Assert.Equal(5, lease.Witness.ResourceIds.Count);
        Assert.Equal(1, scene.Environment.WitnessReads);
    }

    [Fact]
    public void RepackedBackingEvidencePreservesColdReconstructionSemantics()
    {
        MovementSelection first;
        MovementQueryIdentity identity;
        using (var scene = new Scene(ColdScope()))
        {
            scene.Environment.Resources = ["old-page-a", "old-page-b"];
            scene.Environment.OnRebuild = Known;
            var lease = scene.Acquire();
            Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future), out first));
            identity = lease.Identity;
        }
        using (var scene = new Scene(ColdScope()))
        {
            scene.Environment.Resources = ["new-page-combined"];
            scene.Environment.OnRebuild = Known;
            var lease = scene.Acquire();
            Assert.Equal(MovementAvailability.Known, Rebuild(lease, State(Future), out var second));
            Assert.Equal(first, second);
            Assert.Equal(identity, lease.Identity);
        }
    }

    static void AssertKnownSupportAndCoverage(Scene scene, MovementQueryLease lease, MovementSpaceKey space)
    {
        scene.Environment.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> output) =>
        {
            Assert.Equal(space, request.Body.CurrentSpace);
            output[0] = new MovementSupportCandidate(new MovementSupportKey("world", "floor"), space,
                request.Body.Feet, Vector3.UnitY, null);
            return new MovementSupportSet(MovementAvailability.Known, 1, 1, Identity);
        };
        scene.Environment.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            Assert.Equal(space, query.Body.CurrentSpace);
            spans[0] = new MovementCoverageSpan(0f, 1f, 0, 0, true);
            return new MovementCoverageResult(MovementAvailability.Known, 1, 1, 0, 0, 0f, Identity);
        };
        MovementBodyQuery body = Body(space);
        var request = new MovementSupportRequest(body, 0.5f, 2f, 0.8f, new MovementTransitionContext(space, body.Feet));
        Assert.Equal(MovementAvailability.Known, lease.EnumerateSupport(request, new MovementSupportCandidate[1]).Availability);
        Assert.Equal(MovementAvailability.Known,
            lease.TraceWater(new MovementMediumSweepQuery(body, Vector3.UnitX), new MovementCoverageSpan[1], []).Availability);
    }

    static void AssertBlocked(Scene scene, MovementQueryLease lease, int samples = 0)
    {
        MovementBodyQuery body = Body(Future);
        Assert.NotEqual(MovementAvailability.Known, lease.SampleCentreWater(body).Availability);
        var request = new MovementSupportRequest(body, 0.5f, 2f, 0.8f, new MovementTransitionContext(Future, body.Feet));
        var candidate = new MovementSupportCandidate(new MovementSupportKey("world", "sentinel"), Future,
            Vector3.Zero, Vector3.UnitY, null);
        MovementSupportCandidate[] candidates = [candidate];
        Assert.NotEqual(MovementAvailability.Known, lease.EnumerateSupport(request, candidates).Availability);
        Assert.Equal(candidate, candidates[0]);
        var span = new MovementCoverageSpan(0.5f, 1f, 0, 0, true);
        var contact = new MovementDomainContact(new MovementDomainKey("world", "sentinel"), Future,
            new MovementWaterInterval(-4f, 2f, 2f, true, "bed", "top"), Vector2.Zero, Vector3.UnitY, 0.5f, "edge", 0u);
        MovementCoverageSpan[] spans = [span];
        MovementDomainContact[] contacts = [contact];
        Assert.NotEqual(MovementAvailability.Known,
            lease.TraceWater(new MovementMediumSweepQuery(body, Vector3.UnitX), spans, contacts).Availability);
        Assert.Equal(span, spans[0]);
        Assert.Equal(contact, contacts[0]);
        Assert.Equal(samples, scene.Environment.SampleCalls);
        Assert.Equal(0, scene.Environment.SupportCalls);
        Assert.Equal(0, scene.Environment.CoverageCalls);
    }

    static MovementAvailability Known(in FramedMovementState state, out MovementSelection selection)
    {
        Assert.Null(state.Selection);
        selection = Selection(Room);
        return MovementAvailability.Known;
    }
    static MovementSelection Selection(MovementSpaceKey space) => new(space, new MovementSupportKey(space.WorldId, "floor"), Identity);
    static MovementWaterPoint Dry(MovementSpaceKey space) => new(MovementAvailability.Known, space, null, false, 1f, null);
    static MovementBodyQuery Body(MovementSpaceKey space) => new(new Vector3(0f, 1f, 0f), 0.3f, 0.75f, space, null);
    static FramedMovementState State(MovementSpaceKey future, bool grounded = true, Vector3? position = null) =>
        new(new MoveState { Position = position ?? Vector3.Zero, Grounded = grounded }, Frame, Selection(future));
    static MovementQueryScope KnownScope(MovementSpaceKey space) => new(new Vector3(-4f), new Vector3(4f), 0.5f, 2f, space, Identity, Frame);

    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld _world = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView _view;
        MovementQueryLease? _lease;
        public EnvironmentAcquisitionFixture Environment { get; }
        public Scene(MovementQueryScope scope)
        {
            _view = _world.CreateQueryViewExcludingStatics([]);
            Environment = new EnvironmentAcquisitionFixture(_view, scope);
        }
        public (MovementAvailability Status, IDisposable? Lease) AcquireRaw()
        {
            var result = Environment.Acquire();
            _lease = result.Lease as MovementQueryLease;
            return result;
        }
        public MovementQueryLease Acquire()
        {
            var result = AcquireRaw();
            Assert.Equal(MovementAvailability.Known, result.Status);
            return Assert.IsType<MovementQueryLease>(result.Lease);
        }
        public void Dispose()
        {
            try { _lease?.Dispose(); }
            finally { try { _view.Dispose(); } finally { _world.Dispose(); } }
        }
    }

    static MovementQueryScope ColdScope(string world = "world", MovementSpaceKey? hint = null) =>
        new(new Vector3(-4f), new Vector3(4f), 0.5f, 2f, world, hint, Identity, Frame);
    static MovementSpaceKey? Hint(in MovementQueryScope scope) => scope.CurrentSpace;
    static MovementAvailability Rebuild(MovementQueryLease lease, in FramedMovementState state, out MovementSelection selection) =>
        lease.RebuildSelection(state, out selection);
}
