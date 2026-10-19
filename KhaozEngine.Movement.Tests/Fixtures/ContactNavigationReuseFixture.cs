using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal sealed class ContactNavigationReuseFixture : IDisposable
{
    internal readonly ConcurrentQueue<object> Trace = new();
    internal readonly OrdinaryContactScene Scene;
    internal readonly GroundMoveContext Context;
    internal readonly PhysicsNavBake Bake;
    internal readonly Vector3 From, To;
    internal bool FailProvider;
    internal Action<float, float>? OnHeight;
    readonly IPhysicsWorldQueryView? _unsupported;

    internal ContactNavigationReuseFixture(bool physicsWins = false, bool unsupported = false, bool skippedFloor = false)
    {
        float top = skippedFloor ? 1 : physicsWins ? 0.125f : 0;
        From = new(-0.075f, top, 0.125f);
        To = From + new Vector3(0.25f, 0, 0);
        var water = new TracedWater(this);
        Scene = new(Tuning, (x, z) =>
        {
            Trace.Enqueue(("height", BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(z)));
            OnHeight?.Invoke(x, z);
            if (FailProvider && x > 0) throw new ProviderFailure();
            return 0;
        }, (x, z) =>
        {
            Trace.Enqueue(("normal", BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(z)));
            return Vector3.UnitY;
        }, water: water, install: (world, builder) =>
        {
            if (skippedFloor) Box(world, builder, "lower", new(-8, -0.25f, -8), new(8, 0, 8));
            Box(world, builder, "floor", new(-8, skippedFloor ? 0.8f : -0.25f, -8), new(8, top, 8));
        });
        _unsupported = unsupported ? new UnsupportedView(Scene.Physics.CreateQueryViewExcludingStatics([])) : null;
        Context = new(Scene.Height, Scene.Normal, Scene.Physics, null, null, _unsupported,
            water, Scene.Registration, MovementBoundary.Rectangle(-20, -20, 20, 20));
        Bake = PhysicsNavBake.Capture(Context, Options, _ => 0);
        Trace.Clear();
    }

    internal ContactNavigationProof Proof(bool reuse = true)
    {
        using var source = Context.CreateNavigationBinding(Tuning);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var context = (MovementEnvironmentContext)typeof(EffectiveMovementBinding).GetField("_context", fields)!.GetValue(source)!;
        FieldInfo provider = typeof(MovementEnvironmentContext).GetField("_provider", fields)!;
        provider.SetValue(context, new TracedProvider((IMovementEnvironmentProvider)provider.GetValue(context)!, this));
        return new(Bake, Context, source, new NavAreaFootprint(Bake.Columns, Options, Tuning, default),
            new ContactNavigationReusePolicy(reuse, false, false));
    }

    internal static ContactNavigationSource Source(int ordinal = 0, int layer = 0) =>
        new(new(6, 4, ordinal, 0), layer);

    internal static void Released()
    {
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }

    // Lower the backend's existing internal limit before its first publication. No production test control is added.
    internal static void LimitSession(object session, int capacity)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo field = session.GetType().GetField("Memo", fields)!;
        ((IDisposable)field.GetValue(session)!).Dispose();
        object memo = Activator.CreateInstance(field.FieldType, fields, null, [capacity, 2 * 1024 * 1024], null)!;
        field.SetValue(session, memo);
    }

    internal IEnumerable<(object Session, SupportFootprintReuseDomain Domain)> ActiveSessions()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessions = (IEnumerable)Scene.Physics.GetType().GetField("_footprintReuseSessions", fields)!.GetValue(Scene.Physics)!;
        foreach (object pair in sessions)
        {
            object session = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            if ((int)session.GetType().GetField("_thread", fields)!.GetValue(session)! == Environment.CurrentManagedThreadId)
                yield return (session, (SupportFootprintReuseDomain)session.GetType().GetField("_domain", fields)!.GetValue(session)!);
        }
    }

    internal byte[] Build(NavBakeExecution? execution = null) => ContactBakeFile.Write(GroundNavigationBake.Create(
        Bake, ContactBakeFile.Sources(), [new("player", Tuning, default)], execution));

    internal sealed class ProviderFailure() : Exception("deterministic height failure");
    internal sealed record AnalyticCall(MovementSupportRequest Request, MovementAvailability Status, MovementAnalyticGround? Ground);
    internal sealed record BindingCall(MovementContactSupportRequest Request, MovementAvailability Status, MovementContactSupportBinding Binding);

    public void Dispose()
    {
        Bake.Dispose();
        Context.Dispose();
        _unsupported?.Dispose();
        Scene.Dispose();
    }

    sealed class TracedWater(ContactNavigationReuseFixture owner) : IMovementWaterField
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
        public string SemanticIdentity => _inner.SemanticIdentity;
        public long Generation => _inner.Generation;
        public MovementWaterExtent Extent => _inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin)
        {
            owner.Trace.Enqueue(("water", body, origin));
            return _inner.SampleCentreWater(body, origin);
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            owner.Trace.Enqueue(("coverage", query, origin));
            return _inner.TraceWater(query, origin, identity, spans, contacts);
        }
    }

    // Observe real ordinary-producer calls and results. The original provider still owns every side effect.
    sealed class TracedProvider(IMovementEnvironmentProvider inner, ContactNavigationReuseFixture owner) : IMovementEnvironmentProvider
    {
        public string WorldId => inner.WorldId;
        public MovementPreparationResult Prepare(in MovementQueryScope scope) => inner.Prepare(scope);
        public MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physics,
            out IMovementEnvironmentPin? pin)
        {
            var status = inner.TryPinPrepared(scope, physics, out var original);
            pin = original is null ? null : new TracedPin(original, owner);
            return status;
        }
    }

    sealed class TracedPin(IMovementEnvironmentPin inner, ContactNavigationReuseFixture owner) :
        IMovementEnvironmentPin, IMovementContactSupportPin
    {
        public IPhysicsWorldQueryView PhysicsView => inner.PhysicsView;
        public long GeometryGeneration => inner.GeometryGeneration;
        public long EnvironmentGeneration => inner.EnvironmentGeneration;
        public MovementFrameDescriptor Frame => inner.Frame;
        public MovementScopeWitness Witness => inner.Witness;
        public void AssertCurrent() => inner.AssertCurrent();
        public void Dispose() => inner.Dispose();
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body) => inner.SampleCentreWater(body);
        public MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            inner.EnumerateSupport(request, candidates);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) => inner.TraceWater(query, spans, contacts);
        public MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection) =>
            inner.RebuildSelection(state, out selection);
        public MovementAvailability SampleAnalyticGround(in MovementSupportRequest request, out MovementAnalyticGround? ground)
        {
            var status = ((IMovementContactSupportPin)inner).SampleAnalyticGround(request, out ground);
            owner.Trace.Enqueue(new AnalyticCall(request, status, ground));
            return status;
        }
        public MovementAvailability BindContactSupport(in MovementContactSupportRequest request, out MovementContactSupportBinding binding)
        {
            var status = ((IMovementContactSupportPin)inner).BindContactSupport(request, out binding);
            owner.Trace.Enqueue(new BindingCall(request, status, binding));
            return status;
        }
    }

    sealed class UnsupportedView(IPhysicsWorldQueryView inner) : PhysicsForwarder(inner), IPhysicsWorldQueryView,
        IPhysicsQueryLeaseSource, IPhysicsSupportFootprint, IPhysicsCapsuleSweep, IPhysicsCapsuleContacts
    {
        public IPhysicsWorld SourceWorld => inner.SourceWorld;
        public IPhysicsQueryLease AcquireQueryReadLease() => ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease();
        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float separation,
            Span<CapsuleContact> destination, QueryFilter filter = default) =>
            ((IPhysicsCapsuleContacts)inner).QueryCapsuleContacts(capsule, pose, separation, destination, filter);
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
            QueryFilter filter = default) => ((IPhysicsCapsuleSweep)inner).SweepCapsuleCertified(capsule, pose, displacement, filter);
        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
            Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default)
        {
            var result = ((IPhysicsSupportFootprint)inner).QuerySupportFootprint(lease, request, members, joins, filter);
            if (result.Status != CapsuleFeatureStatus.Complete) return result;
            ((IPhysicsSupportFootprint)inner).AssertFootprintCurrent(result, lease);
            return SupportFootprintResult.Completed(this, lease, result.Members, result.Witnesses, result.AxisVerdict, result.FootVerdict);
        }
        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease)
        {
            lease.AssertCurrent();
            if (!ReferenceEquals(result.QueryWorld, this) || !ReferenceEquals(result.Lease, lease))
                throw new InvalidOperationException("The footprint receiver or lease changed.");
            var original = SupportFootprintResult.Completed(inner, lease, result.Members, result.Witnesses,
                result.AxisVerdict, result.FootVerdict);
            ((IPhysicsSupportFootprint)inner).AssertFootprintCurrent(original, lease);
        }
    }
}
