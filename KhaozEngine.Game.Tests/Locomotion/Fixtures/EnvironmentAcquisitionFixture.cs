using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

public sealed partial class EnvironmentAcquisitionFixture : IMovementEnvironmentProvider
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
    public IMovementEnvironmentPin? LastPin;
    public IPhysicsQueryLease? LastPhysicsLease;
    public MovementQueryScope Scope => _scope;
    MovementScopeWitness? _preparedWitness;

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
        PrepareWitness();
        return new MovementPreparationResult(PrepareAvailability,
            Fault == "preparation-identity" ? OtherIdentity : _identity);
    }

    public MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physicsLease,
        out IMovementEnvironmentPin? pin)
    {
        Pinned = true;
        OnPin?.Invoke();
        pin = ContactSupportEnabled ? new ContactPin(this, physicsLease) : new Pin(this, physicsLease);
        LastPin = pin;
        LastPhysicsLease = physicsLease;
        return PinAvailability;
    }

    class Pin(EnvironmentAcquisitionFixture owner, IPhysicsQueryLease physics) : IMovementEnvironmentPin
    {
        readonly MovementScopeWitness _witness = owner._preparedWitness!;
        readonly int _thread = Environment.CurrentManagedThreadId;
        bool _disposed;
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
                AssertCurrent();
                return _witness;
            }
        }

        public void AssertCurrent()
        {
            AssertThread();
            ObjectDisposedException.ThrowIf(_disposed, this);
            physics.AssertCurrent();
            if (owner.Fault == "stale-pin") throw new InvalidOperationException("Pin is stale.");
        }

        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body)
        {
            AssertCurrent();
            owner.SampleCalls++;
            owner.SampleSpaceObserved = body.CurrentSpace;
            owner.OnSample?.Invoke();
            return owner.OnBodySample is { } sample ? sample(body) : owner.Point;
        }

        public MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates)
        {
            AssertCurrent();
            owner.SupportCalls++;
            return owner.OnSupport!(request, candidates);
        }

        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            AssertCurrent();
            owner.CoverageCalls++;
            return owner.OnCoverage!(query, spans, contacts);
        }

        public MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection)
        {
            AssertCurrent();
            owner.RebuildCalls++;
            return owner.OnRebuild!(state, out selection);
        }

        public void Dispose()
        {
            AssertThread();
            if (_disposed) return;
            _disposed = true;
            owner.Disposed = true;
            owner.OnDispose?.Invoke();
            if (owner.ThrowOnDispose) throw new FixtureException();
        }

        void AssertThread()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("A producer pin belongs to its acquiring thread.");
        }
    }

    static MovementQueryIdentity OtherIdentity => new("closure", 1u, "wrong-scope");
    public sealed class FixtureException : Exception { }
}
