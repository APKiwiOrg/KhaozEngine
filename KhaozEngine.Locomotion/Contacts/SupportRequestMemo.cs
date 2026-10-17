using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Exact repeat support reads within one lease interval (SC-15). A key holds every request bit and the view
/// identity: the physics view, the lease the memo is open for, the query, the analytic fact and the scope, all
/// compared bit for bit. A hit returns the sample the first read certified. The memo serves only while it is open
/// for its lease, and its owner closes it when that interval or tick ends, so nothing crosses ticks, bodies,
/// reconciles or leases and no refusal outlives its interval. Storage is fixed at <see cref="Capacity"/>, and a full
/// memo stops remembering rather than evicting.</summary>
internal sealed class SupportRequestMemo
{
    internal const int Capacity = 16;

    // The dry door's memo for this thread, provisioned once. Thread-static storage is never shared between threads.
    [ThreadStatic] static SupportRequestMemo? t_dry;
    [ThreadStatic] static bool t_bypass;

    readonly Entry[] _entries = new Entry[Capacity];
    int _count;

    /// <summary>The lease this memo serves, or null while it is closed.</summary>
    internal IPhysicsQueryLease? Lease { get; private set; }

    /// <summary>Remembered reads.</summary>
    internal int Count => _count;

    /// <summary>Test only. Makes every memo on this thread miss and remember nothing, so a test can run the uncached
    /// evaluator through the same doors. Disposing the scope restores the setting it found, so scopes nest.</summary>
    internal static BypassScope Bypass()
    {
        var scope = new BypassScope(t_bypass);
        t_bypass = true;
        return scope;
    }

    /// <summary>Restores this thread's earlier bypass setting when the scope ends.</summary>
    internal readonly struct BypassScope(bool previous) : IDisposable
    {
        public void Dispose() => t_bypass = previous;
    }

    /// <summary>Opens this thread's dry memo for one tick's <paramref name="lease"/>. Null without a lease, or when
    /// the memo is already open, which only a tick nested on the same thread finds. The nested tick then reads
    /// uncached.</summary>
    internal static SupportRequestMemo? OpenDry(IPhysicsQueryLease? lease)
    {
        if (lease is null) return null;
        SupportRequestMemo memo = t_dry ??= new SupportRequestMemo();
        if (memo.Lease is not null) return null;
        memo.Open(lease);
        return memo;
    }

    internal void Open(IPhysicsQueryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (Lease is not null) throw new InvalidOperationException("The support memo is already open.");
        Lease = lease;
    }

    internal void Close()
    {
        Array.Clear(_entries, 0, _count);
        _count = 0;
        Lease = null;
    }

    /// <summary>True when this memo is open for <paramref name="lease"/> and not bypassed.</summary>
    internal bool Serves(IPhysicsQueryLease lease) => !t_bypass && ReferenceEquals(Lease, lease);

    internal bool TryGet(IPhysicsWorld world, in FootAnalyticGround? analytic, in FootSupportQuery query,
        in SupportFootprintBounds? scope, out SupportSample sample)
    {
        for (int i = 0; i < _count; i++)
            if (_entries[i].Matches(world, analytic, query, scope))
            {
                sample = _entries[i].Sample;
                return true;
            }
        sample = default;
        return false;
    }

    internal void Remember(IPhysicsWorld world, in FootAnalyticGround? analytic, in FootSupportQuery query,
        in SupportFootprintBounds? scope, in SupportSample sample)
    {
        if (_count == _entries.Length) return;
        _entries[_count++] = new(world, analytic, query, scope, sample);
    }

    readonly record struct Entry(IPhysicsWorld World, FootAnalyticGround? Analytic, FootSupportQuery Query,
        SupportFootprintBounds? Scope, SupportSample Sample)
    {
        internal bool Matches(IPhysicsWorld world, in FootAnalyticGround? analytic, in FootSupportQuery query,
            in SupportFootprintBounds? scope) =>
            ReferenceEquals(World, world) && Same(Query, query) && Same(Analytic, analytic) && Same(Scope, scope);
    }

    static bool Same(in FootSupportQuery a, in FootSupportQuery b) =>
        Same(a.Axis.X, b.Axis.X) && Same(a.Axis.Y, b.Axis.Y) && Same(a.FeetY, b.FeetY) &&
        Same(a.FootRadius, b.FootRadius) && Same(a.ReachUp, b.ReachUp) && Same(a.ReachDown, b.ReachDown) &&
        Same(a.CosMaxSlope, b.CosMaxSlope);

    static bool Same(in FootAnalyticGround? a, in FootAnalyticGround? b) =>
        a is { } x && b is { } y ? Same(x.Height, y.Height) && Same(x.Normal, y.Normal) : a.HasValue == b.HasValue;

    static bool Same(in SupportFootprintBounds? a, in SupportFootprintBounds? b) =>
        a is { } x && b is { } y ? Same(x.Minimum, y.Minimum) && Same(x.Maximum, y.Maximum) : a.HasValue == b.HasValue;

    static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);

    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
}
