using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsQueryLeaseSource
{
    // Queries reuse backend scratch storage, so readers are exclusive too. Ordinary nested queries on
    // the owning thread are safe, but another lease cannot shorten the first lease's lifetime.
    readonly object _queryGate = new();
    ReadLease? _readLease;
    long _queryGeneration;
    bool _disposed;

    /// <inheritdoc/>
    public IPhysicsQueryLease AcquireQueryReadLease()
    {
        QueryOperation operation = EnterQuery();
        try
        {
            if (_readLease is not null)
                throw new InvalidOperationException("Physics query leases cannot be nested.");
            var lease = new ReadLease(this, operation);
            _readLease = lease;
            return lease;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    QueryOperation EnterQuery(bool allowDisposed = false) =>
        EnterOperation(mutation: false, allowDisposed, changesGeometry: false);

    QueryOperation EnterMutation(bool allowDisposed = false, bool changesGeometry = true) =>
        EnterOperation(mutation: true, allowDisposed, changesGeometry);

    QueryOperation EnterOperation(bool mutation, bool allowDisposed, bool changesGeometry)
    {
        bool taken = false;
        try
        {
            Monitor.Enter(_queryGate, ref taken);
            if (_disposed && !allowDisposed)
                throw new ObjectDisposedException(nameof(BepuPhysicsWorld));
            if (mutation && _readLease is not null)
                throw new InvalidOperationException("Physics mutations are refused during a query read lease.");
            // Advance before a write that may partially fail. A failed validation may conservatively advance
            // this local generation too, but a refused leased mutation reaches no write and never advances it.
            if (changesGeometry) checked { _queryGeneration++; }
            return new QueryOperation(_queryGate);
        }
        catch
        {
            if (taken) Monitor.Exit(_queryGate);
            throw;
        }
    }

    readonly struct QueryOperation(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    sealed class ReadLease : IPhysicsQueryLease
    {
        readonly BepuPhysicsWorld _owner;
        readonly QueryOperation _operation;
        readonly int _thread;
        bool _disposed;

        public ReadLease(BepuPhysicsWorld owner, QueryOperation operation)
        {
            _owner = owner;
            _operation = operation;
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
            if (!ReferenceEquals(_owner._readLease, this) || _owner._queryGeneration != GeometryGeneration ||
                _owner._origin != Origin || _owner._disposed)
                throw new InvalidOperationException("The physics query lease no longer describes its owner.");
        }

        public void Dispose()
        {
            AssertThread();
            if (_disposed) return;
            AssertCurrent();
            _owner._readLease = null;
            _disposed = true;
            _operation.Dispose();
        }

        void AssertThread()
        {
            // Check before attempting the monitor: another thread must fail, not deadlock waiting for
            // the acquiring thread to release the very lease it is trying to inspect or dispose.
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("A physics query lease belongs to its acquiring thread.");
        }
    }
}
