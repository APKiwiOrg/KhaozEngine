using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    PreparedSceneOutput? _preparedOutput;
    long _preparedSerial;
    bool _preparedOutputDisposed;

    readonly record struct PreparedSceneOutput(long Serial, long FrameIndex, int Width, int Height,
        Matrix4x4 ViewProjection, PixelPostProcess Chain, PixelPostProcess.PreparedOutput Output,
        Scene3DPassTimingsMs Timings, bool TimingEnabled);

    internal long PrepareOutput(IGpuCommandList commands, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_preparedOutputDisposed, this);
        if (_preparedOutput.HasValue)
            throw new InvalidOperationException("The previous prepared scene output has not been consumed.");
        try
        {
            RenderInternalCore(commands, width, height, null, deferOutput: true);
            _profiling?.ThrowIfFailed();
            return _preparedOutput!.Value.Serial;
        }
        catch
        {
            CancelPreparedOutput();
            CancelShadowStageProbe();
            throw;
        }
    }

    internal void RenderPreparedOutput(IGpuCommandList commands, int width, int height,
        IGpuFramebuffer target, long token)
    {
        ObjectDisposedException.ThrowIf(_preparedOutputDisposed, this);
        if (_preparedOutput is not { } prepared || prepared.Serial != token || prepared.FrameIndex != _frameIndex)
            throw new InvalidOperationException("No matching prepared scene output exists in this frame.");
        // Consume before recording. A failed bind or draw must never replay a partial output.
        CancelPreparedOutput();
        if (prepared.Width != width || prepared.Height != height)
            throw new InvalidOperationException("The output dimensions changed after scene preparation.");
        RecordOutputTail(commands, target, prepared.ViewProjection, prepared.Chain, prepared.Output,
            prepared.Timings, prepared.TimingEnabled, true);
        _profiling?.ThrowIfFailed();
    }

    internal bool IsPreparedOutputCurrent(long token) => !_preparedOutputDisposed
        && _preparedOutput is { } output && output.Serial == token && output.FrameIndex == _frameIndex;

    internal void CancelPreparedOutput() => _preparedOutput = null;

    void RecordOutputTail(IGpuCommandList cl, IGpuFramebuffer target, Matrix4x4 displayVp,
        PixelPostProcess postChain, in PixelPostProcess.PreparedOutput output, Scene3DPassTimingsMs timings,
        bool timingEnabled, bool refreshUniform)
    {
        long timingStart = timingEnabled ? Stopwatch.GetTimestamp() : 0;
        if (refreshUniform && (_billboardAdditive.Count > 0 || _billboardAlphaItems.Count > 0))
            _atmosphere.RefreshConsumerUniforms(cl);
        if (refreshUniform && _resolveThisRender
            && _frameDebugView is SceneDebugView.History or SceneDebugView.Disocclusion or SceneDebugView.Reactive)
            _temporalResolve!.RefreshDebugUniforms(cl);
        postChain.DrawOutput(cl, target, in output, refreshUniform);
        DrawDebugView(cl, target);
        DrawTargetOutlines(cl, displayVp, target);
        float postMs = timingEnabled ? timings.PostMs + ElapsedMs(timingStart) : 0;
        timingStart = timingEnabled ? Stopwatch.GetTimestamp() : 0;

        // Filled overlay: rebind `target` and draw the accumulated translucent triangles over the post image,
        // BEFORE the lines so an outline on a fill reads crisp. Depth off, alpha blend, unjittered like picking.
        using (ProfilePass(cl, "display.fills", executed: _fillVerts.Count > 0))
            if (_fillVerts.Count > 0)
            {
                _fills.Draw(cl, displayVp, CollectionsMarshal.AsSpan(_fillVerts), target);
                _frameStats.DrawCalls++;
            }

        // Debug overlay: rebind `target` and draw the accumulated lines over the post image, depth off, alpha
        // blend, unjittered so they line up with ScreenToGround picking.
        using (ProfilePass(cl, "display.lines", executed: _lineVerts.Count > 0))
            if (_lineVerts.Count > 0)
            {
                _lines.Draw(cl, displayVp, CollectionsMarshal.AsSpan(_lineVerts), target);
                _frameStats.DrawCalls++;
            }

        // Billboards: after the line pass, additive first (glow) then alpha, same overlay framebuffer +
        // ViewProjection. Each rebinds `target` (no clear) and uploads its own vertex span. Additive is
        // order-independent (kept in submission order); the alpha stream is built back-to-front so overlapping
        // translucent billboards composite far-to-near regardless of the order the host queued them.
        using (ProfilePass(cl, "display.billboards-additive", executed: _billboardAdditive.Count > 0))
            if (_billboardAdditive.Count > 0)
            {
                _billboards.Draw(cl, displayVp, CollectionsMarshal.AsSpan(_billboardAdditive), target, additive: true);
                _frameStats.DrawCalls++;
            }
        BuildSortedAlphaBillboards();
        using (ProfilePass(cl, "display.billboards-alpha", executed: _billboardAlpha.Count > 0))
            if (_billboardAlpha.Count > 0)
            {
                _billboards.Draw(cl, displayVp, CollectionsMarshal.AsSpan(_billboardAlpha), target, additive: false);
                _frameStats.DrawCalls++;
            }

        // Screen-space teleport transition (blink / frozen-frame crossfade): a fullscreen pass over everything,
        // last so it masks the whole 3D frame. No-op (and byte-identical) when none is active.
        _transitions.Render(cl, _res, target, ScreenTransition);

        if (timingEnabled)
            _passTimingsMs = new Scene3DPassTimingsMs(timings.ShadowDepthMs, timings.ModelMs,
                timings.TransparentsMs + ElapsedMs(timingStart), timings.WaterSyncMs, postMs);
    }
}
