using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using Key = KhaozEngine.TileWorld.Physics.TileWaterTraceMemo.Key;

namespace KhaozEngine.TileWorld.Physics;

// One exact-copy and FIFO policy for both result sizes. Tables never borrow caller storage.
internal class TileWaterTraceStorage
{
    readonly Key[] _keys = [];
    readonly Value[] _values = [];
    readonly MovementCoverageSpan[] _spans = [];
    readonly MovementDomainContact[] _contacts = [];
    readonly int[] _buckets = [], _links = [], _identifierCharges = [];
    readonly TileWaterTraceIdentifiers? _identifiers;
    readonly int _spanLimit, _contactLimit;
    int _count, _next;
    internal int CountForTests => _count;
    internal int OwnedBytes { get; }
    internal int IdentifierBytes { get; private set; }

    // A negative identifier budget preserves the original small tier's per-space length policy.
    internal TileWaterTraceStorage(int entries, int spanLimit, int contactLimit,
        int byteBudget = int.MaxValue, int identifierBudget = -1)
    {
        _spanLimit = spanLimit;
        _contactLimit = contactLimit;
        int identifierCapacity = Math.Max(0, identifierBudget / TileWaterTraceIdentifiers.MinimumStringBytes);
        // Include aligned array headers, the table object and the identifier accountant object.
        // The object allowance deliberately exceeds their current managed layouts on both 64-bit runtimes.
        int bytes = 256 + ArrayBytes(entries, Unsafe.SizeOf<Key>()) + ArrayBytes(entries, Unsafe.SizeOf<Value>()) +
            ArrayBytes(entries * spanLimit, Unsafe.SizeOf<MovementCoverageSpan>()) +
            ArrayBytes(entries * contactLimit, Unsafe.SizeOf<MovementDomainContact>()) +
            ArrayBytes(entries * 2, sizeof(int)) + ArrayBytes(entries, sizeof(int));
        if (identifierBudget >= 0)
            bytes += ArrayBytes(entries, sizeof(int)) + ArrayBytes(identifierCapacity, IntPtr.Size);
        if (bytes > byteBudget) return;
        OwnedBytes = bytes;
        _keys = new Key[entries];
        _values = new Value[entries];
        _spans = new MovementCoverageSpan[entries * spanLimit];
        _contacts = new MovementDomainContact[entries * contactLimit];
        _buckets = new int[entries * 2];
        _links = new int[entries];
        if (identifierBudget >= 0)
        {
            _identifierCharges = new int[entries];
            _identifiers = new(identifierBudget, identifierCapacity);
        }
    }

    static int ArrayBytes(int count, int elementBytes) => (3 * IntPtr.Size + count * elementBytes + 7) & ~7;
    protected static int Bucket(in Key key, int buckets) => (int)((uint)key.GetHashCode() & (buckets - 1));
    static bool Eligible(in Key key) => key.Space.WorldId.Length <= TileWaterTraceMemo.SpaceIdCharacterLimit &&
        key.Space.LocalId.Length <= TileWaterTraceMemo.SpaceIdCharacterLimit;

    internal bool TryRead(in Key key, in MovementSpaceKey space, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts, out MovementCoverageResult result)
    {
        if (_keys.Length != 0 && Eligible(key))
            for (int link = _buckets[Bucket(key, _buckets.Length)]; link != 0; link = _links[link - 1])
            {
                int i = link - 1;
                if (_keys[i] != key) continue;
                Value value = _values[i];
                _spans.AsSpan(i * _spanLimit, value.Spans).CopyTo(spans);
                for (int c = 0; c < value.Contacts; c++)
                {
                    MovementDomainContact contact = _contacts[i * _contactLimit + c];
                    // Only the caller's space and identity are output stamps. Immutable geometry ids
                    // retain their references, except fresh joined-triangle ids whose value is the contract.
                    contacts[c] = new(contact.Domain, space, contact.Interval, contact.IntervalColumnXZ,
                        contact.Normal, contact.Fraction, contact.BoundaryId, contact.CoverageRegionHandle,
                        contact.Overlap, contact.SpanOverlap);
                }
                result = value.Stamp(identity);
                return true;
            }
        result = default;
        return false;
    }

    internal bool Store(in Key key, in MovementCoverageResult result,
        ReadOnlySpan<MovementCoverageSpan> spans, ReadOnlySpan<MovementDomainContact> contacts)
    {
        if (_keys.Length == 0 || result.SpansWritten > _spanLimit || result.ContactsWritten > _contactLimit || !Eligible(key)) return false;
        // Only the immutable numeric evaluator's outcomes belong in the bounded result tier.
        // Invalid input is rejected before lookup. Producer/lifetime failures must never persist.
        if (_identifiers is not null && result.Availability is not (MovementAvailability.Known or
            MovementAvailability.CapacityExceeded or MovementAvailability.PrecisionExceeded or MovementAvailability.Unresolved)) return false;
        int at = _next, charge = 0;
        if (_identifiers is not null && !_identifiers.TryCharge(key, contacts[..result.ContactsWritten],
            IdentifierBytes - _identifierCharges[at], out charge)) return false;
        if (_count == _keys.Length)
        {
#if CONTACT_SHORE_ATTRIBUTION
            TileWaterShoreCounts.Count(_identifiers is null ? ShoreWork.SmallEvict : ShoreWork.ResultEvict);
#endif
            Remove(at);
        }
        _keys[at] = key;
        _values[at] = new(result);
        var destination = _contacts.AsSpan(at * _contactLimit, _contactLimit);
        destination.Clear();
        spans[..result.SpansWritten].CopyTo(_spans.AsSpan(at * _spanLimit));
        contacts[..result.ContactsWritten].CopyTo(destination);
        if (_identifiers is not null)
        {
            IdentifierBytes += charge - _identifierCharges[at];
            _identifierCharges[at] = charge;
        }
        Insert(at);
        _next = (at + 1) % _keys.Length;
        _count = Math.Min(_count + 1, _keys.Length);
        return true;
    }

    internal void Clear()
    {
        Array.Clear(_keys);
        Array.Clear(_values);
        Array.Clear(_contacts);
        Array.Clear(_buckets);
        Array.Clear(_links);
        Array.Clear(_identifierCharges);
        IdentifierBytes = _count = _next = 0;
    }

    void Remove(int at)
    {
        int bucket = Bucket(_keys[at], _buckets.Length), previous = 0;
        for (int link = _buckets[bucket]; link != 0; link = _links[link - 1])
        {
            int slot = link - 1;
            if (slot == at)
            {
                if (previous == 0) _buckets[bucket] = _links[slot];
                else _links[previous - 1] = _links[slot];
                _links[slot] = 0;
                return;
            }
            previous = link;
        }
    }

    void Insert(int at)
    {
        int bucket = Bucket(_keys[at], _buckets.Length), previous = 0, link = _buckets[bucket];
        // Preserve physical-slot search order, including duplicate internal Store calls.
        while (link != 0 && link - 1 < at)
        {
            previous = link;
            link = _links[link - 1];
        }
        _links[at] = link;
        if (previous == 0) _buckets[bucket] = at + 1;
        else _links[previous - 1] = at + 1;
    }

    readonly record struct Value(MovementAvailability Availability, int Spans, int RequiredSpans,
        int Contacts, int RequiredContacts, float Error)
    {
        internal Value(in MovementCoverageResult result) : this(result.Availability, result.SpansWritten,
            result.RequiredSpanCapacity, result.ContactsWritten, result.RequiredContactCapacity, result.CertifiedErrorMetres) { }
        internal MovementCoverageResult Stamp(in MovementQueryIdentity identity) =>
            new(Availability, Spans, RequiredSpans, Contacts, RequiredContacts, Error, identity);
    }
}
