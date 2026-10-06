using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

public sealed class EnvironmentAcquisitionFixture : IMovementEnvironmentProvider
{
    readonly IPhysicsWorldQueryView _view;
    readonly MovementQueryIdentity _identity = new("closure", 1u, "scope");
    readonly MovementQueryScope _scope;
    readonly MovementEnvironmentContext _context;
    public MovementAvailability PinAvailability = MovementAvailability.Known;
    public string Fault = "";
    public IPhysicsWorldQueryView? PinView;
    public Action? OnPrepare;
    public Action? OnPin;
    public Action? OnDispose;
    public bool ThrowOnDispose;
    public bool Prepared;
    public bool Pinned;
    public bool Disposed;
    public int WitnessReads;
    public int SampleCalls;
    public MovementWaterPoint Point;
    public Action? OnSample;
    public delegate MovementWaterPoint SampleHandler(in MovementBodyQuery body);
    public SampleHandler? OnBodySample;
    public delegate MovementSupportSet SupportHandler(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates);
    public delegate MovementCoverageResult CoverageHandler(in MovementMediumSweepQuery query,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts);
    public SupportHandler? OnSupport;
    public CoverageHandler? OnCoverage;
    public int SupportCalls;
    public int CoverageCalls;
    public MovementQueryIdentity Identity => _identity;
    public string WorldId => "world";
    public MovementAvailability PrepareAvailability = MovementAvailability.Known;
    public MovementQueryScope? WitnessScope;
    public IReadOnlyList<string> Resources = new[] { "directory" };
    public MovementSpaceKey? SampleSpaceObserved;
    public delegate MovementAvailability RebuildHandler(in FramedMovementState state, out MovementSelection selection);
    public RebuildHandler? OnRebuild;
    public int RebuildCalls;

    public EnvironmentAcquisitionFixture(IPhysicsWorldQueryView view, MovementQueryScope? scope = null)
    {
        _view = view;
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, view.Origin, 1ul);
        _scope = scope ?? new MovementQueryScope(new Vector3(-4f), new Vector3(4f), 0.5f, 2f,
            new MovementSpaceKey("world", "room"), _identity, frame);
        _context = new MovementEnvironmentContext(view, this, _identity);
    }

    public (MovementAvailability Status, IDisposable? Lease) Acquire()
    {
        MovementAvailability result = _context.TryAcquire(_scope, out MovementQueryLease? lease);
        return (result, lease);
    }

    public MovementPreparationResult Prepare(in MovementQueryScope scope)
    {
        Prepared = true;
        OnPrepare?.Invoke();
        return new MovementPreparationResult(PrepareAvailability,
            Fault == "preparation-identity" ? OtherIdentity : _identity);
    }

    public MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physicsLease,
        out IMovementEnvironmentPin? pin)
    {
        Pinned = true;
        OnPin?.Invoke();
        pin = new Pin(this, physicsLease);
        return PinAvailability;
    }

    sealed class Pin(EnvironmentAcquisitionFixture owner, IPhysicsQueryLease physics) : IMovementEnvironmentPin
    {
        public IPhysicsWorldQueryView PhysicsView => owner.PinView ?? owner._view;
        public long GeometryGeneration => physics.GeometryGeneration + (owner.Fault == "geometry-generation" ? 1 : 0);
        public long EnvironmentGeneration => 7L;
        public MovementFrameDescriptor Frame => owner.Fault == "frame"
            ? new MovementFrameDescriptor(WorldFrame.Origin, owner._view.Origin, 2ul) : owner._scope.Frame;

        public MovementScopeWitness Witness
        {
            get
            {
                owner.WitnessReads++;
                if (owner.Fault == "throw-validation") throw new FixtureException();
                MovementQueryIdentity identity = owner.Fault == "witness-identity" ? OtherIdentity : owner._identity;
                var scope = owner.WitnessScope ?? (owner.Fault is "scope" or "support-envelope" or "witness-identity"
                    ? new MovementQueryScope(owner._scope.Min,
                    owner.Fault == "scope" ? owner._scope.Max - Vector3.UnitX : owner._scope.Max,
                    owner.Fault == "support-envelope" ? 0f : owner._scope.MaxRise, owner._scope.MaxDrop,
                    owner._scope.WorldId, owner._scope.CurrentSpace, identity, owner._scope.Frame) : owner._scope);
                Assert.Equal(MovementAvailability.Known,
                    MovementScopeWitness.TryCreate(scope, identity, owner.Resources, true, out var witness));
                return witness!;
            }
        }

        public void AssertCurrent()
        {
            if (owner.Fault == "stale-pin") throw new InvalidOperationException("Pin is stale.");
        }

        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body)
        {
            owner.SampleCalls++;
            owner.SampleSpaceObserved = body.CurrentSpace;
            owner.OnSample?.Invoke();
            return owner.OnBodySample is { } sample ? sample(body) : owner.Point;
        }

        public MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates)
        {
            owner.SupportCalls++;
            return owner.OnSupport!(request, candidates);
        }

        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            owner.CoverageCalls++;
            return owner.OnCoverage!(query, spans, contacts);
        }

        public MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection)
        {
            owner.RebuildCalls++;
            return owner.OnRebuild!(state, out selection);
        }

        public void Dispose()
        {
            owner.Disposed = true;
            owner.OnDispose?.Invoke();
            if (owner.ThrowOnDispose) throw new FixtureException();
        }
    }

    static MovementQueryIdentity OtherIdentity => new("closure", 1u, "wrong-scope");
    public sealed class FixtureException : Exception { }
}
