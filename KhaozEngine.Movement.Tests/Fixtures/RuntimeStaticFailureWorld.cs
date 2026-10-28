using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>Injects a composing backend failure while retaining real physical gates, storage and revisions.</summary>
internal sealed class RuntimeStaticFailureWorld : PhysicsForwarder, IPhysicsWorld,
    IPhysicsQueryLeaseSource, IPhysicsStaticMutationSource
{
    internal BepuPhysicsWorld Backend { get; }
    internal Action? BeforeAdd { get; set; }
    internal Action? AfterAdd { get; set; }
    internal Action? BeforeRemove { get; set; }
    internal Action? AfterRemove { get; set; }
    internal int Adds { get; private set; }
    internal Action? WriterAttempt { get; set; }
    internal Func<StaticHandle, StaticHandle>? ReturnHandle { get; set; }
    internal bool FreezeRevision { get; set; }
    internal RuntimeStaticFailureWorld() : this(new(Vector3.Zero)) { }
    RuntimeStaticFailureWorld(BepuPhysicsWorld backend) : base(backend) => Backend = backend;
    public long StaticGeometryGeneration => ReportRevision(Backend.StaticGeometryGeneration);
    long ReportRevision(long revision) => FreezeRevision ? 0 : revision;
    public new StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
    {
        Adds++;
        BeforeAdd?.Invoke();
        StaticHandle handle = Backend.AddStatic(shape, pose, material);
        AfterAdd?.Invoke();
        return ReturnHandle?.Invoke(handle) ?? handle;
    }
    public new void RemoveStatic(StaticHandle handle)
    {
        BeforeRemove?.Invoke();
        Backend.RemoveStatic(handle);
        AfterRemove?.Invoke();
    }
    public IPhysicsQueryLease AcquireQueryReadLease() => new Read(this, Backend.AcquireQueryReadLease());
    public IPhysicsStaticMutationLease AcquireStaticMutationLease()
    {
        WriterAttempt?.Invoke();
        return new Writer(this, Backend.AcquireStaticMutationLease());
    }
    public override IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> handles) =>
        new View(this, Backend.CreateQueryViewExcludingStatics(handles));

    sealed class Read(IPhysicsWorld owner, IPhysicsQueryLease inner) : IPhysicsQueryLease
    {
        public IPhysicsWorld SourceWorld => owner;
        public Vector3 Origin => inner.Origin;
        public long GeometryGeneration => inner.GeometryGeneration;
        public void AssertCurrent() => inner.AssertCurrent();
        public void Dispose() => inner.Dispose();
    }
    sealed class Writer(RuntimeStaticFailureWorld owner, IPhysicsStaticMutationLease inner) : IPhysicsStaticMutationLease
    {
        public IPhysicsWorld SourceWorld => owner;
        public Vector3 Origin => inner.Origin;
        public long StaticGeometryGeneration => owner.ReportRevision(inner.StaticGeometryGeneration);
        public void AssertCurrent() => inner.AssertCurrent();
        public void Dispose() => inner.Dispose();
    }
    sealed class View(RuntimeStaticFailureWorld owner, IPhysicsWorldQueryView inner) : PhysicsForwarder(inner),
        IPhysicsWorldQueryView, IPhysicsQueryLeaseSource, IPhysicsStaticGeometrySource,
        IPhysicsCapsuleContacts, IPhysicsCapsuleSweep, IPhysicsSupportFootprint
    {
        public IPhysicsWorld SourceWorld => owner;
        public long StaticGeometryGeneration => owner.StaticGeometryGeneration;
        public IPhysicsQueryLease AcquireQueryReadLease() => owner.AcquireQueryReadLease();
        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float separation,
            Span<CapsuleContact> destination, QueryFilter filter = default) =>
            ((IPhysicsCapsuleContacts)inner).QueryCapsuleContacts(capsule, pose, separation, destination, filter);
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
            QueryFilter filter = default) => ((IPhysicsCapsuleSweep)inner).SweepCapsuleCertified(capsule, pose, displacement, filter);
        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
            Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default) =>
            throw new NotSupportedException("This failure fixture tests admission, not footprint certificates.");
        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) =>
            throw new NotSupportedException("This failure fixture tests admission, not footprint certificates.");
    }
}
