using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsStaticMutationSource
{
    /// <inheritdoc/>
    public IPhysicsStaticMutationLease AcquireStaticMutationLease()
    {
        // Enter the gate directly. Reserving an interval is not a geometry mutation.
        QueryGate.Holder holder = _queryGate.EnterWrite();
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BepuPhysicsWorld));
            if (holder.StaticMutationLease is not null)
                throw new InvalidOperationException("Physics static-mutation leases cannot be nested.");
            var lease = new StaticMutationLease(this, holder);
            holder.StaticMutationLease = lease;
            return lease;
        }
        catch
        {
            _queryGate.ExitWrite(holder);
            throw;
        }
    }

    sealed class StaticMutationLease : IPhysicsStaticMutationLease
    {
        readonly BepuPhysicsWorld _owner;
        readonly QueryGate.Holder _holder;
        readonly int _thread;
        bool _disposed;

        internal StaticMutationLease(BepuPhysicsWorld owner, QueryGate.Holder holder)
        {
            _owner = owner;
            _holder = holder;
            _thread = Environment.CurrentManagedThreadId;
            Origin = owner._origin;
        }

        public IPhysicsWorld SourceWorld => _owner;
        public Vector3 Origin { get; }
        public long StaticGeometryGeneration
        {
            get
            {
                AssertCurrent();
                return _owner._staticGeometryVersion;
            }
        }

        public void AssertCurrent()
        {
            AssertThread();
            if (_disposed) throw new ObjectDisposedException(nameof(IPhysicsStaticMutationLease));
            // Static revisions deliberately change during this interval. Its holder and frame must not.
            if (!ReferenceEquals(_holder.StaticMutationLease, this) || _holder.Writes <= 0 ||
                _owner._origin != Origin || _owner._disposed)
                throw new InvalidOperationException("The physics static-mutation lease no longer describes its owner.");
        }

        public void Dispose()
        {
            AssertThread();
            if (_disposed) return;
            try { AssertCurrent(); }
            finally
            {
                // A changed origin or disposed owner must not strand the exclusive writer.
                _holder.StaticMutationLease = null;
                _disposed = true;
                _owner._queryGate.ExitWrite(_holder);
            }
        }

        void AssertThread()
        {
            // Refuse before inspecting holder state or touching the gate on a different thread.
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("A physics static-mutation lease belongs to its acquiring thread.");
        }
    }
}
