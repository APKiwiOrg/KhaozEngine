using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

// Owned by one immutable sampler. No thread-static reference keeps discarded worlds alive.
internal sealed class TileWaterTraceMemo
{
    // Bound retained world memory even when a host cycles workers. Overflow runs uncached.
    internal const int ThreadLimit = 4;
    // Small repeated Contact paths fit here. Large traces must not expand retained storage.
    internal const int EntryLimit = 128, SpanLimit = 8, ContactLimit = 48;
    // Space identifiers are caller-owned strings with no public length cap. Do not let a small
    // fixed table retain arbitrarily large caller allocations through keys or contact records.
    internal const int SpaceIdCharacterLimit = 256;
    readonly Slot[] _slots = [new(), new(), new(), new()];

    sealed class Slot
    {
        internal int ThreadId;
        internal Table? Table;
    }

    internal Table? ForCurrentThread()
    {
        int thread = Environment.CurrentManagedThreadId;
        foreach (var slot in _slots)
        {
            int owner = Volatile.Read(ref slot.ThreadId);
            if (owner == thread) return slot.Table;
            if (owner == 0 && Interlocked.CompareExchange(ref slot.ThreadId, thread, 0) == 0)
                return slot.Table = new();
        }
        return null;
    }

    internal void ResetCurrentThread()
    {
        foreach (var slot in _slots)
            if (Volatile.Read(ref slot.ThreadId) == Environment.CurrentManagedThreadId)
                slot.Table?.Clear();
    }

    // A weak lifetime token only. Its target is borrowed, never mutated by the test.
    internal WeakReference? CaptureCurrentTableForTests()
    {
        foreach (var slot in _slots)
            if (Volatile.Read(ref slot.ThreadId) == Environment.CurrentManagedThreadId && slot.Table is { } table)
                return new(table);
        return null;
    }

    // Integer representations preserve signed zero. Space strings compare ordinally by value.
    internal readonly record struct Key(int X, int Y, int Z, int Dx, int Dy, int Dz, int Radius,
        int HalfHeight, int Ox, int Oy, int Oz, MovementSpaceKey Space, int Spans, int Contacts, ulong Fp)
    {
        internal Key(in MovementMediumSweepQuery query, Vector3 origin, int spans, int contacts, ulong fp)
            : this(Bits(query.Body.Centre.X), Bits(query.Body.Centre.Y), Bits(query.Body.Centre.Z),
                Bits(query.Delta.X), Bits(query.Delta.Y), Bits(query.Delta.Z), Bits(query.Body.Radius),
                Bits(query.Body.HalfHeight), Bits(origin.X), Bits(origin.Y), Bits(origin.Z),
                query.Body.CurrentSpace, spans, contacts, fp) { }
        static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    }

    // Identity is deliberately absent. A validated caller receives its own identity on replay.
    readonly record struct Value(MovementAvailability Availability, int Spans, int RequiredSpans,
        int Contacts, int RequiredContacts, float Error)
    {
        internal Value(in MovementCoverageResult result) : this(result.Availability, result.SpansWritten,
            result.RequiredSpanCapacity, result.ContactsWritten, result.RequiredContactCapacity,
            result.CertifiedErrorMetres) { }
        internal MovementCoverageResult Stamp(in MovementQueryIdentity identity) =>
            new(Availability, Spans, RequiredSpans, Contacts, RequiredContacts, Error, identity);
    }

    internal sealed class Table
    {
        readonly Key[] _keys = new Key[EntryLimit];
        readonly Value[] _values = new Value[EntryLimit];
        readonly MovementCoverageSpan[] _spans = new MovementCoverageSpan[EntryLimit * SpanLimit];
        readonly MovementDomainContact[] _contacts = new MovementDomainContact[EntryLimit * ContactLimit];
        int _count, _next;
        internal int CountForTests => _count;

        internal bool TryRead(in Key key, in MovementSpaceKey space, in MovementQueryIdentity identity,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts, out MovementCoverageResult result)
        {
            for (int i = 0; i < _count; i++)
                if (_keys[i] == key)
                {
                    Value value = _values[i];
                    _spans.AsSpan(i * SpanLimit, value.Spans).CopyTo(spans);
                    for (int c = 0; c < value.Contacts; c++)
                    {
                        MovementDomainContact contact = _contacts[i * ContactLimit + c];
                        // Space strings belong to this caller. Domain and interval ids belong to
                        // the captured sampler. Boundary ids do too, except the freshly allocated
                        // joined-triangle id. Its immutable value is replayed without a new string allocation.
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

        internal void Store(in Key key, in MovementCoverageResult result,
            ReadOnlySpan<MovementCoverageSpan> spans, ReadOnlySpan<MovementDomainContact> contacts)
        {
            if (result.SpansWritten > SpanLimit || result.ContactsWritten > ContactLimit ||
                key.Space.WorldId.Length > SpaceIdCharacterLimit || key.Space.LocalId.Length > SpaceIdCharacterLimit) return;
            int at = _next;
            _keys[at] = key;
            _values[at] = new(result);
            // Clear replaced references as well as owning every written prefix ourselves.
            var destination = _contacts.AsSpan(at * ContactLimit, ContactLimit);
            destination.Clear();
            spans[..result.SpansWritten].CopyTo(_spans.AsSpan(at * SpanLimit));
            contacts[..result.ContactsWritten].CopyTo(destination);
            _next = (at + 1) % EntryLimit;
            _count = Math.Min(_count + 1, EntryLimit);
        }

        internal void Clear()
        {
            Array.Clear(_keys);
            Array.Clear(_values);
            Array.Clear(_contacts);
            _count = _next = 0;
        }
    }
}
