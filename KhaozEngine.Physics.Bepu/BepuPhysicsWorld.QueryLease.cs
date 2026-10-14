using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsQueryLeaseSource, IPhysicsConcurrentReadSource
{
    // Readers run together because each interval borrows private scratch, and the simulation is only read.
    // Mutations are exclusive writers. Nested operations on one thread reuse its admitted interval.
    readonly QueryGate _queryGate = new(new QueryScratchPool());
    long _queryGeneration;
    bool _disposed;

    /// <inheritdoc/>
    public IPhysicsQueryLease AcquireQueryReadLease()
    {
        QueryGate.Holder holder = _queryGate.EnterLease();
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BepuPhysicsWorld));
            SupportQueryDiagnostics.Record(SupportQueryCounter.PhysicsLeases);
            var lease = new ReadLease(this, holder);
            holder.Lease = lease;
            return lease;
        }
        catch
        {
            _queryGate.ExitRead(holder);
            throw;
        }
    }

    QueryOperation EnterQuery(bool allowDisposed = false) =>
        EnterOperation(mutation: false, allowDisposed, changesGeometry: false);

    QueryOperation EnterMutation(bool allowDisposed = false, bool changesGeometry = true) =>
        EnterOperation(mutation: true, allowDisposed, changesGeometry);

    QueryOperation EnterOperation(bool mutation, bool allowDisposed, bool changesGeometry)
    {
        // A mutation on a thread inside a read interval or lease is refused here, before any wait.
        QueryGate.Holder holder = mutation ? _queryGate.EnterWrite() : _queryGate.EnterRead();
        var operation = new QueryOperation(_queryGate, holder, mutation);
        try
        {
            if (_disposed && !allowDisposed)
                throw new ObjectDisposedException(nameof(BepuPhysicsWorld));
            // Advance before a write that may partially fail. A failed validation may conservatively advance
            // this local generation too, but a refused leased mutation reaches no write and never advances it.
            if (changesGeometry) checked { _queryGeneration++; }
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    readonly struct QueryOperation(QueryGate gate, QueryGate.Holder holder, bool mutation) : IDisposable
    {
        /// <summary>The interval's private scratch, attached on first use.</summary>
        public QueryScratch Scratch => gate.Scratch(holder);

        public void Dispose()
        {
            if (mutation) gate.ExitWrite(holder);
            else gate.ExitRead(holder);
        }
    }

    sealed class ReadLease : IPhysicsQueryLease
    {
        readonly BepuPhysicsWorld _owner;
        readonly QueryGate.Holder _holder;
        readonly int _thread;
        bool _disposed;

        public ReadLease(BepuPhysicsWorld owner, QueryGate.Holder holder)
        {
            _owner = owner;
            _holder = holder;
            _thread = Environment.CurrentManagedThreadId;
            Origin = owner._origin;
            GeometryGeneration = owner._queryGeneration;
        }

        public IPhysicsWorld SourceWorld => _owner;
        public Vector3 Origin { get; }
        public long GeometryGeneration { get; }

        public void AssertCurrent()
        {
            AssertThread();
            if (_disposed) throw new ObjectDisposedException(nameof(IPhysicsQueryLease));
            // The live lease keeps writers out, so the owner fields read here cannot change underneath it.
            if (!ReferenceEquals(_holder.Lease, this) || _owner._queryGeneration != GeometryGeneration ||
                _owner._origin != Origin || _owner._disposed)
                throw new InvalidOperationException("The physics query lease no longer describes its owner.");
        }

        public void Dispose()
        {
            AssertThread();
            if (_disposed) return;
            AssertCurrent();
            _holder.Lease = null;
            _disposed = true;
            _owner._queryGate.ExitRead(_holder);
        }

        void AssertThread()
        {
            // Check before touching the gate: another thread must fail, not wait behind the very lease it is
            // trying to inspect or dispose.
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("A physics query lease belongs to its acquiring thread.");
        }
    }
}
