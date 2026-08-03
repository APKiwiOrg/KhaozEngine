using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE DEVICE-FREE HALF OF THE EVENT-QUERY FALLBACK: the issue counter, the in-flight ordering and the
    /// marker recycling that turn a pile of one-shot <c>ID3D11Query</c> event objects into the single monotonic
    /// counter <see cref="ID3D11FenceTimeline"/> promises.
    /// <para>
    /// Split out of <see cref="D3D11EventQueryTimeline"/> so this logic is testable on macOS and Linux. What is
    /// left in the Windows type is four native calls (create a query, end it, poll it, release it) and nothing
    /// else, and every decision that could be wrong lives here where a plain <c>[Fact]</c> can drive it.
    /// </para>
    /// <para>
    /// WHY RETIRING IN ISSUE ORDER IS CORRECT. Every marker is placed on the immediate context, which consumes
    /// its work in submission order, so a marker placed later cannot complete before one placed earlier. The
    /// queue therefore advances the completed counter by draining from the FRONT while the front marker reports
    /// done, and it never has to look past it. A marker placed on a deferred context would break that, which is
    /// exactly why nothing in this backend places one there.
    /// </para>
    /// <para>
    /// RECYCLING IS NOT AN OPTIMISATION, it is the bound. One event query per submission with no reuse would
    /// allocate a device object per frame forever. A retired marker goes straight onto the free list and the
    /// timeline rents it back on the next signal, so a steady-state session holds roughly as many query objects
    /// as it has submissions in flight.
    /// </para>
    /// <para>Not thread-safe, and it does not need to be: every call is made under the device's submit lock.</para>
    /// </summary>
    internal sealed class D3D11EventTimelineQueue
    {
        readonly struct Pending
        {
            internal Pending(ulong value, object marker) { Value = value; Marker = marker; }
            internal ulong Value { get; }
            internal object Marker { get; }
        }

        // In-flight markers in issue order. Dequeued from the front only (see the ordering note above).
        readonly Queue<Pending> _pending = new();

        // Retired markers waiting to be rented back. A stack rather than a queue because any of them will do and
        // the most recently retired one is the warmest.
        readonly Stack<object> _free = new();

        ulong _issued;
        ulong _completed;

        /// <summary>The highest value handed out by <see cref="Enqueue"/>. Every value from 1 to this has been
        /// issued, so a caller can tell a never-signalled timeline (0) from a signalled one.</summary>
        internal ulong Issued => _issued;

        /// <summary>The highest value whose marker has been retired. Starts at 0, never decreases.</summary>
        internal ulong Completed => _completed;

        /// <summary>How many markers are in flight. The steady-state size of the query pool, and the number a
        /// leak would grow without bound.</summary>
        internal int PendingCount => _pending.Count;

        /// <summary>How many retired markers are available to rent. Exposed so a test can prove the recycling
        /// happens rather than inferring it from an allocation count nobody can see.</summary>
        internal int RecycledCount => _free.Count;

        /// <summary>A retired marker to reuse, or null when the caller must create one. Null is the normal answer
        /// while the pool is still filling and the abnormal one after that.</summary>
        internal object? Rent() => _free.Count > 0 ? _free.Pop() : null;

        /// <summary>Take ownership of <paramref name="marker"/> as the newest in-flight signal and return its
        /// value. The caller must already have placed the marker on the context: this type never touches a device
        /// and cannot do it for them.</summary>
        internal ulong Enqueue(object marker)
        {
            if (marker is null) throw new ArgumentNullException(nameof(marker));

            _issued++;
            _pending.Enqueue(new Pending(_issued, marker));
            return _issued;
        }

        /// <summary>The oldest in-flight marker, which is the only one worth polling. False when nothing is in
        /// flight, in which case <see cref="Completed"/> is already final.</summary>
        internal bool TryPeekOldest(out object marker)
        {
            if (_pending.Count == 0) { marker = null!; return false; }

            marker = _pending.Peek().Marker;
            return true;
        }

        /// <summary>Retire the oldest in-flight marker: its value becomes <see cref="Completed"/> and the marker
        /// itself goes on the free list. Call it only after the poll said that marker is done.</summary>
        internal void RetireOldest()
        {
            if (_pending.Count == 0)
                throw new InvalidOperationException(
                    "The Direct3D 11 event-query timeline was asked to retire a signal with nothing in flight. "
                    + "Retiring is driven by the poll of the oldest marker, so reaching here means the poll and "
                    + "the retire have drifted apart and the completed value would run ahead of the GPU.");

            Pending done = _pending.Dequeue();
            _completed = done.Value;
            _free.Push(done.Marker);
        }

        /// <summary>Hand back every marker this queue owns, in-flight and retired alike, and forget them. The
        /// disposal path: the caller releases the native objects, which this type cannot do. The counters are
        /// left where they are on purpose, so a value read after disposal still reports what it reported before.
        /// </summary>
        internal object[] TakeEveryMarker()
        {
            var all = new object[_pending.Count + _free.Count];
            int next = 0;
            while (_pending.Count > 0) all[next++] = _pending.Dequeue().Marker;
            while (_free.Count > 0) all[next++] = _free.Pop();
            return all;
        }
    }
}
