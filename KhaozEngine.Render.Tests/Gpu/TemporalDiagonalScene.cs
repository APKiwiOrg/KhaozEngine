using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One lazy resolve scene for the diagonal sample protocol, with independent cold capture boundaries.</summary>
public sealed class TemporalDiagonalScene : IDisposable
{
    TemporalResolveRig? _rig;
    TemporalDetailGate _gate;
    internal sealed record Measurement(TemporalReviewFigures Figures, int Missed, float HalfWidth);
    readonly Dictionary<string, Measurement> _measured = new();

    internal Measurement Measure(string key, Func<Measurement> capture)
    {
        if (!_measured.TryGetValue(key, out var value)) _measured.Add(key, value = capture());
        return value;
    }

    internal TemporalResolveRig Borrow(int iw, int ih, int width, int height, float ndc,
        TemporalHistoryDetail detail, TemporalDetailGate gate)
    {
        if (gate == TemporalDetailGate.Production) gate = TemporalDetailGateVariants.Selected;
        string Rewrite(string source) => TemporalDetailGateVariants.Rewrite(source, gate);
        if (_rig is null) _rig = new TemporalResolveRig(iw, ih, width, height, ndc,
            fragmentSource: Rewrite, detail: detail);
        else _rig.ResetCapture(iw, ih, width, height, ndc, detail, Rewrite, gate != _gate);
        _gate = gate;
        return _rig;
    }

    public void Dispose() => _rig?.Dispose();
}
