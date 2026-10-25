using System;
using System.Collections.Generic;

namespace KhaozEngine.Physics;

/// <summary>A synchronous, thread-affine opt-in capture. Nested labels own exclusive work and contribute
/// once to their parent on close. Never hold a capture across await or a thread handoff.</summary>
public sealed class SupportQueryCapture : IDisposable
{
    [ThreadStatic] static SupportQueryCapture? t_current;

    readonly int _thread = Environment.CurrentManagedThreadId;
    readonly string _label;
    readonly SupportQueryCapture? _parent;
    readonly SupportQueryDiagnostics.ThreadState _prior;
    readonly List<SupportQueryMeasurement> _children = [];
    SupportQueryMeasurement? _final;
    bool _closed;

    SupportQueryCapture(string label)
    {
        _label = label;
        _parent = t_current;
        _prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true) with
        {
            MeasurementLabel = label,
        });
        t_current = this;
    }

    /// <summary>Starts a fresh enabled recorder on this thread, suspending prior counters and observers.
    /// Nonblank labels are preserved exactly.</summary>
    public static SupportQueryCapture Begin(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A capture needs a nonblank label.", nameof(label));
        return new(label);
    }

    /// <summary>Returns detached cumulative evidence without resetting counters. An open capture must be
    /// the active innermost scope. After closing, the creating thread can retrieve its frozen result.</summary>
    public SupportQueryMeasurement Snapshot()
    {
        AssertThread();
        if (_closed) return _final ?? throw new InvalidOperationException("Capture finalization failed.");
        AssertActive();
        var direct = new SupportQueryMeasurement(_label, new(SupportQueryDiagnostics.Snapshot(),
            SupportQueryDiagnostics.Routes, SupportQueryDiagnostics.Retention, SupportQueryDiagnostics.HasCompleteLifetimes));
        if (_children.Count == 0) return direct;
        return SupportQueryMeasurement.Combine(WithChildren(direct));
    }

    IEnumerable<SupportQueryMeasurement> WithChildren(SupportQueryMeasurement direct)
    {
        yield return direct;
        foreach (SupportQueryMeasurement child in _children) yield return child;
    }

    /// <summary>Freezes this interval and restores prior diagnostics even if materialization fails.
    /// Repeated close is harmless. Close nested scopes in reverse order on their creating thread.</summary>
    public void Dispose()
    {
        AssertThread();
        if (_closed) return;
        AssertActive();
        try { _final = Snapshot(); }
        finally
        {
            SupportQueryDiagnostics.Restore(_prior);
            t_current = _parent;
            _closed = true;
        }
        if (_parent is not null) _parent._children.Add(_final);
    }

    void AssertThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("A support-query capture belongs to its creating thread.");
    }

    void AssertActive()
    {
        if (!ReferenceEquals(t_current, this))
            throw new InvalidOperationException("Only the innermost support-query capture can be sampled or closed.");
    }
}
