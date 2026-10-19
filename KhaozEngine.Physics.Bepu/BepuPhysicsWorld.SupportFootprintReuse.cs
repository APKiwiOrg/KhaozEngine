using System;
using System.Collections.Concurrent;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsSupportFootprintReuseSource
{
    readonly ConcurrentDictionary<(int Thread, IPhysicsWorld Receiver), FootprintReuseSession> _footprintReuseSessions = new();

    /// <inheritdoc/>
    public bool TryOpenSupportFootprintReuse(IPhysicsQueryLease lease, out IPhysicsSupportFootprintReuseSession? session) =>
        TryOpenSupportFootprintReuseCore(this, lease, null, out session);

    bool TryOpenSupportFootprintReuseCore(IPhysicsWorld receiver, IPhysicsQueryLease lease,
        StaticQueryExclusions? exclusions, out IPhysicsSupportFootprintReuseSession? session)
    {
        AuthenticateFeatureLease(lease);
        using QueryOperation scope = EnterQuery();
        session = null;
        var identity = (Environment.CurrentManagedThreadId, receiver);
        if (_footprintReuseSessions.ContainsKey(identity)) return false;
        var opened = new FootprintReuseSession(this, receiver, lease, exclusions);
        if (!_footprintReuseSessions.TryAdd(identity, opened)) return false;
        session = opened;
        return true;
    }

    FootprintReuseSession? FootprintReuse(IPhysicsWorld receiver, IPhysicsQueryLease lease,
        StaticQueryExclusions? exclusions, SupportFootprintCollector collector)
    {
        // Non-default scratch work policy must retain its original refusal, even after an unrestricted success.
        if (SupportReferenceEvaluation.Active || collector.CurvedWork.Limit != SupportCurvedWork.MaximumWork) return null;
        return _footprintReuseSessions.TryGetValue((Environment.CurrentManagedThreadId, receiver), out var session) &&
            ReferenceEquals(exclusions, session.Exclusions) && session.Matches(lease) ? session : null;
    }

    sealed class FootprintReuseSession : IPhysicsSupportFootprintReuseSession
    {
        readonly BepuPhysicsWorld _owner;
        readonly IPhysicsWorld _receiver;
        internal StaticQueryExclusions? Exclusions { get; }
        readonly int _thread, _originX, _originY, _originZ;
        readonly long _installedGeneration, _staticGeneration, _leaseGeneration;
        internal readonly SupportFootprintProofMemo Memo = new();
        SupportFootprintReuseDomain _domain;
        bool _hasDomain, _disposed;
        internal ulong SourceVersion { get; private set; }

        internal FootprintReuseSession(BepuPhysicsWorld owner, IPhysicsWorld receiver, IPhysicsQueryLease lease,
            StaticQueryExclusions? exclusions)
        {
            _owner = owner;
            _receiver = receiver;
            Exclusions = exclusions;
            _thread = Environment.CurrentManagedThreadId;
            _installedGeneration = owner._staticGeometryVersion;
            _staticGeneration = owner.StaticGeometryGeneration;
            _leaseGeneration = lease.GeometryGeneration;
            _originX = BitConverter.SingleToInt32Bits(lease.Origin.X);
            _originY = BitConverter.SingleToInt32Bits(lease.Origin.Y);
            _originZ = BitConverter.SingleToInt32Bits(lease.Origin.Z);
        }

        public void ResetSource(in SupportFootprintReuseDomain domain)
        {
            AssertThread();
            using QueryOperation scope = _owner.EnterQuery();
            AssertBasis();
            ArgumentNullException.ThrowIfNull(domain.ProfileIdentity);
            ArgumentNullException.ThrowIfNull(domain.SourceIdentity);
            Memo.Reset();
            SourceVersion++;
            _domain = domain;
            _hasDomain = true;
        }

        public void AssertCurrent(IPhysicsQueryLease lease)
        {
            AssertThread();
            _owner.AuthenticateFeatureLease(lease);
            using QueryOperation scope = _owner.EnterQuery();
            AssertBasis();
            if (!Matches(lease)) throw new InvalidOperationException("The footprint reuse session has no current source domain.");
        }

        internal bool Matches(IPhysicsQueryLease lease) => !_disposed && _hasDomain &&
            _domain.ProfileIdentity is not null && _domain.SourceIdentity is not null &&
            Environment.CurrentManagedThreadId == _thread && ReferenceEquals(lease.SourceWorld, _owner) &&
            lease.GeometryGeneration == _leaseGeneration && CurrentBasis() &&
            BitConverter.SingleToInt32Bits(lease.Origin.X) == _originX &&
            BitConverter.SingleToInt32Bits(lease.Origin.Y) == _originY &&
            BitConverter.SingleToInt32Bits(lease.Origin.Z) == _originZ;

        bool CurrentBasis() => !_owner._disposed && (_receiver is not QueryView view || !view.IsDisposed) &&
            _owner._staticGeometryVersion == _installedGeneration &&
            _owner.StaticGeometryGeneration == _staticGeneration && _owner._queryGeneration == _leaseGeneration &&
            BitConverter.SingleToInt32Bits(_owner._origin.X) == _originX &&
            BitConverter.SingleToInt32Bits(_owner._origin.Y) == _originY &&
            BitConverter.SingleToInt32Bits(_owner._origin.Z) == _originZ;

        void AssertBasis()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_receiver is QueryView view) view.ThrowIfDisposed();
            if (!CurrentBasis()) throw new InvalidOperationException("The footprint reuse session's selected view changed.");
        }

        void AssertThread()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("A footprint reuse session belongs to its opening thread.");
        }

        public void Dispose()
        {
            AssertThread();
            if (_disposed) return;
            try
            {
                using QueryOperation scope = _owner.EnterQuery();
                AssertBasis();
            }
            finally
            {
                try { Memo.Dispose(); }
                finally
                {
                    _domain = default;
                    _hasDomain = false;
                    _disposed = true;
                    _owner._footprintReuseSessions.TryRemove((_thread, _receiver), out _);
                }
            }
        }
    }
}
