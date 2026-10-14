using System;
using System.Collections.Generic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>World-owned pool of reusable query scratch. Intervals rent one scratch each and return it at
/// their outer exit. Closing happens under exclusive world disposal, after every interval has left, and
/// disposes each constructed scratch exactly once. A scratch returned after closing is disposed instead.</summary>
internal sealed class QueryScratchPool
{
    readonly object _sync = new();
    readonly Stack<QueryScratch> _idle = new(4);
    readonly List<QueryScratch> _constructed = [];
    bool _closed;

    internal QueryScratch Rent()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_idle.TryPop(out QueryScratch? pooled)) return pooled;
        }
        var scratch = new QueryScratch();
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _constructed.Add(scratch);
            }
            return scratch;
        }
        catch
        {
            scratch.Dispose();
            throw;
        }
    }

    internal void Return(QueryScratch scratch)
    {
        scratch.Reset();
        lock (_sync)
        {
            if (!_closed)
            {
                _idle.Push(scratch);
                return;
            }
        }
        scratch.Dispose();
    }

    /// <summary>Disposes every pooled scratch and refuses further rentals.</summary>
    internal void Close()
    {
        QueryScratch[] drained;
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            drained = _idle.ToArray();
            _idle.Clear();
        }
        List<Exception>? failures = null;
        foreach (QueryScratch scratch in drained)
        {
            try { scratch.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException(failures);
    }

    /// <summary>Scratch waiting in the pool for its next interval, for accounting.</summary>
    internal int IdleCount
    {
        get { lock (_sync) return _idle.Count; }
    }

    /// <summary>Every scratch this pool has constructed, for disposal accounting.</summary>
    internal QueryScratch[] Constructed()
    {
        lock (_sync) return [.. _constructed];
    }
}
