using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D;

// Private archive instrumentation. Uploads, visibility, motion bookkeeping and every shadow path stay intact.
public sealed partial class Scene3D : IFrameDiagnosticsGBufferGates
{
    GBufferGate _gbufferGate = GBufferGate.Disabled, _gbufferGateForRender = GBufferGate.Disabled;
    int _appliedGBufferGate = -1;
    readonly long[] _skippedGBufferDraws = new long[5];

    void IFrameDiagnosticsGBufferGates.Apply(GBufferGate gate)
    {
        _gbufferGate = gate;
        if (gate == GBufferGate.Disabled) _gbufferGateForRender = GBufferGate.Disabled;
    }

    int IFrameDiagnosticsGBufferGates.ConsumeApplied(Span<long> skippedDraws)
    {
        _skippedGBufferDraws.CopyTo(skippedDraws);
        Array.Clear(_skippedGBufferDraws);
        int applied = _appliedGBufferGate;
        _appliedGBufferGate = -1;
        return applied;
    }

    void BeginGBufferGateFrame()
    {
        _gbufferGateForRender = _gbufferGate;
        if (_gbufferGate == GBufferGate.Disabled) return;
        int gate = (int)_gbufferGate;
        _appliedGBufferGate = _appliedGBufferGate == -1 || _appliedGBufferGate == gate ? gate : -2;
    }

    bool SkipGBufferDraw(GBufferGate category)
    {
        if (_gbufferGateForRender != category) return false;
        _skippedGBufferDraws[(int)category - 1]++;
        return true;
    }
}
