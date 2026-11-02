using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

/// <summary>Private recorder seam for a renderer's logical pass boundaries.</summary>
public enum FrameDiagnosticsPassEvent { Begin, End, Skip }

/// <summary>Private recorder seam. Pass records no GPU commands, allocates nothing once warmed, and does nothing
/// unless the recorder is sampling.</summary>
public interface IFrameDiagnosticsPassSink
{
    /// <summary>One boundary of the pass named <paramref name="id"/> on <paramref name="commands"/>.</summary>
    void Pass(IGpuCommandList commands, FrameDiagnosticsPassEvent kind, string id, int occurrence);
}

internal sealed class FrameDiagnosticsPassForwarder : IFrameDiagnosticsPassSink
{
    internal static readonly FrameDiagnosticsPassForwarder Instance = new();

    public void Pass(IGpuCommandList commands, FrameDiagnosticsPassEvent kind, string id, int occurrence)
    {
        if (!MetalFrameTimingProbe.Sampling) return;
        try
        {
            MetalFrameTimingProbe.ObservePass(commands, kind switch
            {
                FrameDiagnosticsPassEvent.Begin => PassLabelEventKind.Begin,
                FrameDiagnosticsPassEvent.End => PassLabelEventKind.End,
                _ => PassLabelEventKind.Skip,
            }, id, occurrence);
        }
        catch (Exception) { MetalFrameTimingProbe.PassObserverFailed(); }
    }
}
