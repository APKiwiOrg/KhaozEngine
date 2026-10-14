using System;
using System.Collections.Generic;
using System.Threading;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Private read storage for one reader or writer interval at a time. Its Bepu pool is separate from
/// the simulation pool, so every read-side native allocation stays inside the borrowing interval.
/// Operation sets are loaned with take-and-restore, so a nested operation of the same kind receives an
/// inactive or fresh set and never the active one.</summary>
internal sealed class QueryScratch : IDisposable
{
    /// <summary>Capsule contact candidates and their collector, loaned together to one query.</summary>
    internal sealed class ContactSet
    {
        internal readonly List<CollidableReference> Candidates = [];
        internal readonly CapsuleContactCollector Collector = new();
    }

    internal BufferPool Pool { get; } = new();
    internal Slot<List<CollidableReference>> Overlaps;
    internal Slot<List<CollidableReference>> SweepCandidates;
    internal Slot<ContactSet> Contacts;
    internal Slot<SupportFootprintCollector> Footprints;
    internal Slot<SupportNeighborhoodCollector> Neighborhoods;
    int _disposals;

    /// <summary>How many times disposal ran. Exactly one is the owner's contract.</summary>
    internal int Disposals => Volatile.Read(ref _disposals);

    /// <summary>The native capacity this scratch still holds, in bytes.</summary>
    internal long NativeCapacity
    {
        get
        {
            long total = 0;
            for (int power = 0; power <= SpanHelper.MaximumSpanSizePower; power++)
                total += Pool.GetCapacityForPower(power);
            return total;
        }
    }

    /// <summary>Returns the scratch to a logically empty state. Allocator capacity is kept for reuse.</summary>
    internal void Reset()
    {
        Overlaps.Idle?.Clear();
        SweepCandidates.Idle?.Clear();
        Contacts.Idle?.Candidates.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Increment(ref _disposals) == 1) Pool.Clear();
    }

    /// <summary>One loanable operation set. Only the interval holding the scratch touches it.</summary>
    internal struct Slot<T> where T : class, new()
    {
        internal T? Idle;

        internal T Take()
        {
            T? value = Idle;
            Idle = null;
            return value ?? new T();
        }

        // The outermost operation restores last, so the warmest set is the one retained.
        internal void Restore(T value) => Idle = value;
    }
}
