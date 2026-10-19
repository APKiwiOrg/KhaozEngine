using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A bounded source-local exact table. Owned array capacities and inline entries are charged by their
/// actual element sizes. No result retains a world, lease, request probe, collector or caller buffer.</summary>
internal sealed class SupportFootprintProofMemo : IDisposable
{
    struct Entry
    {
        internal bool Occupied;
        internal SupportFootprintProofKey Key;
        internal SupportFootprintMember[] Members;
        internal ulong[] Joins;
        internal int Witnesses;
        internal SupportProposalVerdict Axis, Foot;
    }

    readonly int _keyCapacity, _payloadBudgetBytes;
    Entry[]? _entries;
    int _count, _bytes;
    bool _disposed;

    internal SupportFootprintProofMemo(int keyCapacity = 512, int payloadBudgetBytes = 2 * 1024 * 1024)
    {
        if (keyCapacity is < 0 or > 512) throw new ArgumentOutOfRangeException(nameof(keyCapacity));
        if (payloadBudgetBytes is < 0 or > 2 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(payloadBudgetBytes));
        _keyCapacity = keyCapacity;
        _payloadBudgetBytes = payloadBudgetBytes;
    }

    internal bool TryCopy(IPhysicsWorld receiver, IPhysicsQueryLease lease, in SupportFootprintProofKey key,
        Span<SupportFootprintMember> members, Span<ulong> joins, out SupportFootprintResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        result = default;
        int index = Find(key);
        if (index < 0 || !_entries![index].Occupied)
        {
            SupportQueryDiagnostics.Record(SupportQueryCounter.FootprintReuseMisses);
            return false;
        }
        ref readonly Entry entry = ref _entries[index];
        // Preserve the original evaluator's capacity/refusal ordering by letting it handle short callers.
        if (members.Length < entry.Members.Length || joins.Length < entry.Joins.Length) return false;
        result = SupportFootprintResult.Completed(receiver, lease, entry.Members.Length, entry.Witnesses,
            entry.Axis, entry.Foot);
        entry.Members.AsSpan().CopyTo(members);
        entry.Joins.AsSpan().CopyTo(joins);
        SupportQueryDiagnostics.Record(SupportQueryCounter.FootprintReuseHits);
        return true;
    }

    internal void Remember(in SupportFootprintProofKey key, in SupportFootprintResult result,
        ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (result.Status != CapsuleFeatureStatus.Complete) return;
        int index = Find(key);
        if (index >= 0 && _entries![index].Occupied) return;
        int words = result.Members * result.JoinWordsPerRow;
        int tableBytes = _entries is null ? _keyCapacity * Unsafe.SizeOf<Entry>() : 0;
        int payloadBytes = result.Members * Unsafe.SizeOf<SupportFootprintMember>() + words * sizeof(ulong);
        if (_count == _keyCapacity || tableBytes + payloadBytes > _payloadBudgetBytes - _bytes)
        {
            SupportQueryDiagnostics.Record(SupportQueryCounter.FootprintReuseBudgetSkips);
            return;
        }
        // Allocate exact lengths. Both occupied and spare fixed table slots are included in the budget.
        SupportFootprintMember[] ownedMembers = members[..result.Members].ToArray();
        ulong[] ownedJoins = joins[..words].ToArray();
        _entries ??= new Entry[_keyCapacity];
        index = Find(key);
        _entries[index] = new Entry { Occupied = true, Key = key, Members = ownedMembers, Joins = ownedJoins,
            Witnesses = result.Witnesses, Axis = result.AxisVerdict, Foot = result.FootVerdict };
        _count++;
        int retained = tableBytes + payloadBytes;
        _bytes += retained;
        SupportQueryDiagnostics.ProofReuseRetention(1, retained, _count, _bytes);
        SupportQueryDiagnostics.Record(SupportQueryCounter.FootprintReuseStores);
    }

    int Find(in SupportFootprintProofKey key)
    {
        if (_entries is null) return -1;
        int start = (int)((uint)key.GetHashCode() % (uint)_entries.Length);
        for (int offset = 0; offset < _entries.Length; offset++)
        {
            int index = (start + offset) % _entries.Length;
            ref readonly Entry entry = ref _entries[index];
            if (!entry.Occupied || entry.Key == key) return index;
        }
        return -1;
    }

    internal void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SupportQueryDiagnostics.ProofReuseRetention(-_count, -_bytes, 0, 0);
        _entries = null;
        _count = _bytes = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
    }
}
