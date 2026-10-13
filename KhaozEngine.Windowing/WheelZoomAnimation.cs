using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing;

/// <summary>Ordered log-factor animation with lazy per-operation wheel policy resolution.</summary>
internal sealed class WheelZoomAnimation
{
    sealed class Operation
    {
        internal float Delta;
        internal readonly Vector2 Anchor;
        internal readonly float NotchFactor;
        internal readonly double Duration;
        internal readonly Func<float, float, float>? Resolver;
        internal bool Started;
        internal double LogFactor, AppliedLog, Elapsed;

        internal Operation(float delta, Vector2 anchor, PanZoomOptions options)
        {
            Delta = delta;
            Anchor = anchor;
            NotchFactor = options.WheelZoomFactor;
            Duration = options.WheelZoomDuration;
            Resolver = options.WheelZoomFactorResolver;
        }
    }

    readonly LinkedList<Operation> _pending = new();
    PanZoomWheelMode _mode;
    Func<float, float, float>? _resolver;
    bool _hasPolicy;

    internal void SynchronizePolicy(PanZoomOptions options)
    {
        if (_hasPolicy && (_mode != options.WheelMode || !ReferenceEquals(_resolver, options.WheelZoomFactorResolver)))
            Clear();
        _mode = options.WheelMode;
        _resolver = options.WheelZoomFactorResolver;
        _hasPolicy = true;
    }

    internal void Enqueue(float delta, Vector2 anchor, PanZoomOptions options)
    {
        if (!float.IsFinite(delta) || delta == 0 || !DragMotionTracker.IsFinite(anchor)) return;
        Operation? last = _pending.Last?.Value;
        if (last != null && !last.Started && last.Resolver == null && options.WheelZoomFactorResolver == null
            && last.Anchor == anchor && last.NotchFactor == options.WheelZoomFactor
            && last.Duration == options.WheelZoomDuration && MathF.Sign(last.Delta) == MathF.Sign(delta)
            && float.IsFinite(last.Delta + delta) && HasRepresentableFactor(last.NotchFactor, last.Delta + delta))
            last.Delta += delta;
        else _pending.AddLast(new Operation(delta, anchor, options));
    }

    internal void Advance(IPanZoomTarget target, float dtSeconds)
    {
        double remaining = dtSeconds;
        while (_pending.First != null)
        {
            Operation op = _pending.First.Value;
            if (!Start(op)) { _pending.RemoveFirst(); continue; }
            double step = Math.Min(remaining, op.Duration - op.Elapsed);
            op.Elapsed += step;
            remaining -= step;
            double next = op.Duration == 0 ? op.LogFactor : op.LogFactor * (op.Elapsed / op.Duration);
            Apply(target, op, next);
            if (op.Elapsed < op.Duration) return;
            _pending.RemoveFirst();
        }
    }

    internal void Finish(IPanZoomTarget target)
    {
        while (_pending.First != null)
        {
            Operation op = _pending.First.Value;
            if (Start(op)) Apply(target, op, op.LogFactor);
            _pending.RemoveFirst();
        }
    }

    internal void Clear() => _pending.Clear();

    static bool Start(Operation op)
    {
        if (op.Started) return true;
        float proposed = (float)Math.Pow(op.NotchFactor, op.Delta);
        float resolved = op.Resolver == null ? proposed : op.Resolver(op.Delta, proposed);
        if (!float.IsFinite(resolved) || resolved <= 0 || resolved == 1) return false;
        op.LogFactor = Math.Log(resolved);
        op.Started = true;
        return true;
    }

    static bool HasRepresentableFactor(float notchFactor, float delta)
    {
        float factor = (float)Math.Pow(notchFactor, delta);
        return float.IsFinite(factor) && factor > 0;
    }

    static void Apply(IPanZoomTarget target, Operation op, double nextLog)
    {
        float factor = (float)Math.Exp(nextLog - op.AppliedLog);
        if (float.IsFinite(factor) && factor > 0 && factor != 1) target.ZoomByFactor(factor, op.Anchor);
        op.AppliedLog = nextLog;
    }
}
