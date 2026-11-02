using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game;

// Names the frame recorder's encoders with Render3D's own pass keys. Records no GPU commands. A throwing observer makes
// Scene3D throw after recording, so nothing a sink does may escape from here.
internal sealed class SceneFramePassObserver(IFrameDiagnosticsPassSink sink) : IRenderPassObserver
{
    public void BeginPass(IGpuCommandList commands, RenderPassKey key) => Forward(commands, FrameDiagnosticsPassEvent.Begin, key);
    public void EndPass(IGpuCommandList commands, RenderPassKey key) => Forward(commands, FrameDiagnosticsPassEvent.End, key);
    public void SkipPass(IGpuCommandList commands, RenderPassKey key) => Forward(commands, FrameDiagnosticsPassEvent.Skip, key);
    public void BeginCpuPhase(string id) { }
    public void EndCpuPhase(string id) { }

    void Forward(IGpuCommandList commands, FrameDiagnosticsPassEvent kind, RenderPassKey key)
    {
        try { sink.Pass(commands, kind, key.Id, key.Occurrence); }
        catch (Exception) { /* A lost label is visible as an unbalanced stream. Rendering must carry on. */ }
    }
}
