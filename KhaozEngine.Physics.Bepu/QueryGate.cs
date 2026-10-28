using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Writer-preferring reader and writer gate for one physics owner, with per-thread nesting.
/// The outermost read on a thread admits one reader interval, and nested reads reuse it without waiting,
/// even behind a queued writer. Writers are exclusive, run in arrival order and wait for admitted readers
/// to leave. A read callback must not synchronously wait on another thread entering this owner.</summary>
internal sealed class QueryGate
{
    /// <summary>One thread's holdings on this gate. Only its own thread reads or writes it.</summary>
    internal sealed class Holder
    {
        internal int Reads;
        internal int Writes;
        internal object? Lease;
        internal object? StaticMutationLease;
        internal QueryScratch? Scratch;
    }

    readonly object _sync = new();
    // Not disposed: holders stay reachable from leases and post-disposal reads such as Origin.
    readonly ThreadLocal<Holder> _holders = new(static () => new Holder());
    readonly QueryScratchPool _scratch;
    readonly HashSet<long> _abandonedTickets = [];
    long _nextTicket, _servingTicket;
    int _activeReaders, _waitingReaders;
    bool _activeWriter;

    internal QueryGate(QueryScratchPool scratch) => _scratch = scratch;

    internal QueryScratchPool ScratchPool => _scratch;

    /// <summary>Writers queued behind the active interval, for diagnostics.</summary>
    internal int WaitingWriters
    {
        get { lock (_sync) return (int)(_nextTicket - _servingTicket) - (_activeWriter ? 1 : 0) - _abandonedTickets.Count; }
    }

    /// <summary>Admitted outer reader intervals, for diagnostics.</summary>
    internal int ActiveReaders
    {
        get { lock (_sync) return _activeReaders; }
    }

    /// <summary>Outer readers waiting behind a queued or active writer, for diagnostics.</summary>
    internal int WaitingReaders
    {
        get { lock (_sync) return _waitingReaders; }
    }

    internal Holder EnterRead()
    {
        Holder holder = _holders.Value!;
        if (holder.Reads > 0 || holder.Writes > 0)
        {
            holder.Reads++;
            return holder;
        }
        lock (_sync)
        {
            _waitingReaders++;
            try
            {
                while (_nextTicket != _servingTicket) Monitor.Wait(_sync);
            }
            finally { _waitingReaders--; }
            _activeReaders++;
        }
        holder.Reads = 1;
        return holder;
    }

    internal void ExitRead(Holder holder)
    {
        Debug.Assert(holder.Reads > 0);
        if (--holder.Reads > 0 || holder.Writes > 0) return;
        try { ReleaseScratch(holder); }
        finally
        {
            lock (_sync)
            {
                if (--_activeReaders == 0) Monitor.PulseAll(_sync);
            }
        }
    }

    /// <summary>Admits an explicit lease. It is refused before any wait when this thread already holds a
    /// lease or is inside a writer, so no lease can outlive a writer interval.</summary>
    internal Holder EnterLease()
    {
        Holder holder = _holders.Value!;
        if (holder.Lease is not null)
            throw new InvalidOperationException("Physics query leases cannot be nested.");
        if (holder.Writes > 0)
            throw new InvalidOperationException("Physics query leases cannot start inside a physics mutation.");
        return EnterRead();
    }

    internal Holder EnterWrite()
    {
        Holder holder = _holders.Value!;
        // Refuse before any upgrade attempt: waiting here would wait on this thread's own interval.
        if (holder.Reads > 0)
            throw new InvalidOperationException("Physics mutations are refused during a query read lease or interval.");
        if (holder.Writes > 0)
        {
            holder.Writes++;
            return holder;
        }
        lock (_sync)
        {
            long ticket = _nextTicket++;
            try
            {
                while (ticket != _servingTicket || _activeReaders > 0) Monitor.Wait(_sync);
            }
            catch
            {
                Abandon(ticket);
                throw;
            }
            _activeWriter = true;
        }
        holder.Writes = 1;
        return holder;
    }

    internal void ExitWrite(Holder holder)
    {
        Debug.Assert(holder.Writes > 0 && holder.Reads == 0);
        if (--holder.Writes > 0) return;
        try { ReleaseScratch(holder); }
        finally
        {
            lock (_sync)
            {
                _activeWriter = false;
                AdvanceTicket();
                Monitor.PulseAll(_sync);
            }
        }
    }

    /// <summary>The interval's private scratch, attached on first use and released at the outer exit.</summary>
    internal QueryScratch Scratch(Holder holder)
    {
        Debug.Assert(holder.Reads > 0 || holder.Writes > 0);
        return holder.Scratch ??= _scratch.Rent();
    }

    void ReleaseScratch(Holder holder)
    {
        QueryScratch? scratch = holder.Scratch;
        holder.Scratch = null;
        if (scratch is not null) _scratch.Return(scratch);
    }

    // An interrupted waiter gives up its ticket, so later writers and readers are never stranded behind it.
    void Abandon(long ticket)
    {
        if (ticket != _servingTicket) _abandonedTickets.Add(ticket);
        else
        {
            AdvanceTicket();
            Monitor.PulseAll(_sync);
        }
    }

    void AdvanceTicket()
    {
        _servingTicket++;
        while (_abandonedTickets.Remove(_servingTicket)) _servingTicket++;
    }
}
