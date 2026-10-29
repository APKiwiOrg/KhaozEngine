using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

// Counts lifetimes while forwarding every geometry query to real Bepu certificates.
internal sealed partial class ObservedPhysicsWorld : PhysicsForwarder, IPhysicsQueryLeaseSource,
    IPhysicsStaticGeometrySource, IPhysicsCapsuleContacts, IPhysicsCapsuleSweep, IPhysicsSupportFootprint
{
    internal BepuPhysicsWorld Backend { get; }
    internal int ReadCount { get; private set; }
    public long StaticGeometryGeneration => Backend.StaticGeometryGeneration;
    internal int ViewCreations { get; private set; }
    internal int ViewDisposals { get; private set; }
    internal int SourceDisposals { get; private set; }
    internal bool Selectable { get; init; } = true;
    internal bool CertifiedSweep { get; init; } = true;
    internal System.Collections.Generic.List<string> DisposalOrder { get; } = [];
    internal ObservedPhysicsWorld() : this(new BepuPhysicsWorld(Vector3.Zero)) { }
    ObservedPhysicsWorld(BepuPhysicsWorld backend) : base(backend) => Backend = backend;
    public IPhysicsQueryLease AcquireQueryReadLease()
    {
        ReadCount++;
        return new Read(this, Backend.AcquireQueryReadLease());
    }
    public override IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> handles)
    {
        if (!Selectable) throw new NotSupportedException("Static selection is unavailable.");
        ViewCreations++;
        var selected = Backend.CreateQueryViewExcludingStatics(handles);
        return CertifiedSweep ? new View(this, selected) : new UnsweptView(this, selected);
    }
    public override void Dispose() { SourceDisposals++; DisposalOrder.Add("physics"); Backend.Dispose(); }

    public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float separation,
        Span<CapsuleContact> destination, QueryFilter filter = default) =>
        Backend.QueryCapsuleContacts(capsule, pose, separation, destination, filter);
    public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default) => Backend.SweepCapsuleCertified(capsule, pose, displacement, filter);
    public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
        Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default) =>
        Footprint(this, Backend, lease, request, members, joins, filter);
    public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) =>
        AssertFootprint(this, Backend, result, lease);

    static SupportFootprintResult Footprint(IPhysicsWorld receiver, IPhysicsWorld backend, IPhysicsQueryLease lease,
        in SupportFootprintRequest request, Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter)
    {
        var read = (Read)lease;
        var result = ((IPhysicsSupportFootprint)backend).QuerySupportFootprint(read.Inner, request, members, joins, filter);
        if (result.Status != CapsuleFeatureStatus.Complete) return result;
        ((IPhysicsSupportFootprint)backend).AssertFootprintCurrent(result, read.Inner);
        return SupportFootprintResult.Completed(receiver, lease, result.Members, result.Witnesses, result.AxisVerdict, result.FootVerdict);
    }

    static void AssertFootprint(IPhysicsWorld receiver, IPhysicsWorld backend, in SupportFootprintResult result, IPhysicsQueryLease lease)
    {
        lease.AssertCurrent();
        if (!ReferenceEquals(result.QueryWorld, receiver) || !ReferenceEquals(result.Lease, lease))
            throw new InvalidOperationException("The footprint receiver or read changed.");
        var original = SupportFootprintResult.Completed(backend, ((Read)lease).Inner,
            result.Members, result.Witnesses, result.AxisVerdict, result.FootVerdict);
        ((IPhysicsSupportFootprint)backend).AssertFootprintCurrent(original, ((Read)lease).Inner);
    }

    sealed class Read(IPhysicsWorld source, IPhysicsQueryLease inner) : IPhysicsQueryLease
    {
        internal IPhysicsQueryLease Inner => inner;
        public IPhysicsWorld SourceWorld => source;
        public long GeometryGeneration => inner.GeometryGeneration;
        public Vector3 Origin => inner.Origin;
        public void AssertCurrent() => inner.AssertCurrent();
        public void Dispose() => inner.Dispose();
    }

    class UnsweptView(ObservedPhysicsWorld owner, IPhysicsWorldQueryView inner) : PhysicsForwarder(inner),
        IPhysicsWorldQueryView, IPhysicsQueryLeaseSource, IPhysicsCapsuleContacts, IPhysicsSupportFootprint
    {
        protected IPhysicsWorldQueryView SelectedView => inner;
        public IPhysicsWorld SourceWorld => owner;
        public IPhysicsQueryLease AcquireQueryReadLease() => owner.AcquireQueryReadLease();
        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float separation,
            Span<CapsuleContact> destination, QueryFilter filter = default) =>
            ((IPhysicsCapsuleContacts)inner).QueryCapsuleContacts(capsule, pose, separation, destination, filter);
        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
            Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default) =>
            Footprint(this, inner, lease, request, members, joins, filter);
        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) =>
            AssertFootprint(this, inner, result, lease);
        public override void Dispose() { inner.Dispose(); owner.ViewDisposals++; owner.DisposalOrder.Add("movement-view"); }
    }
    sealed class View(ObservedPhysicsWorld owner, IPhysicsWorldQueryView inner) : UnsweptView(owner, inner), IPhysicsCapsuleSweep
    {
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
            QueryFilter filter = default) => ((IPhysicsCapsuleSweep)SelectedView).SweepCapsuleCertified(capsule, pose, displacement, filter);
    }

}

internal abstract class PhysicsForwarder(IPhysicsWorld inner) : IPhysicsWorld
{
    public Vector3 Origin => inner.Origin;
    public bool CanRebase => inner.CanRebase;
    public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => inner.AddStatic(shape, pose, material);
    public void RemoveStatic(StaticHandle handle) => inner.RemoveStatic(handle);
    public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) =>
        inner.AddDynamic(shape, pose, body, material);
    public void RemoveDynamic(DynamicBodyHandle handle) => inner.RemoveDynamic(handle);
    public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
    public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) =>
        inner.GetDynamicVelocity(handle, out linear, out angular);
    public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) => inner.SetDynamicVelocity(handle, linear, angular);
    public bool IsAwake(DynamicBodyHandle handle) => inner.IsAwake(handle);
    public ConstraintHandle AddConstraint(in ConstraintDescription description) => inner.AddConstraint(description);
    public void RemoveConstraint(ConstraintHandle handle) => inner.RemoveConstraint(handle);
    public void SetConstraintTarget(ConstraintHandle handle, float target) => inner.SetConstraintTarget(handle, target);
    public void Step(float dt) => inner.Step(dt);
    public bool Raycast(Vector3 origin, Vector3 direction, float maximum, out RayHit hit, QueryFilter filter = default) =>
        inner.Raycast(origin, direction, maximum, out hit, filter);
    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maximum, out SweepHit hit, QueryFilter filter = default) =>
        inner.SweepCapsule(capsule, pose, direction, maximum, out hit, filter);
    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) => inner.ComputePenetration(capsule, pose, out mtv);
    public virtual IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> handles) =>
        inner.CreateQueryViewExcludingStatics(handles);
    public void Rebase(Vector3 origin) => inner.Rebase(origin);
    public virtual void Dispose() => inner.Dispose();
}
