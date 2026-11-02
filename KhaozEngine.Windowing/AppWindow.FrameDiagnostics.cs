using System;
using System.Diagnostics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

public sealed partial class AppWindow
{
    IFrameDiagnosticsAnnotator? _frameDiagnosticsAnnotator;
    IFrameDiagnosticsShaderGates? _frameDiagnosticsShaderGates;

    /// <summary>Whether this process requested the private frame recorder through KE_FRAME_DIAGNOSTICS_PATH.</summary>
    public bool FrameDiagnosticsEnabled => FrameDiagnosticsRecorder.IsEnabled;

    /// <summary>Register the fixed workload channels before Run. No capture tap or copy-source request is added.</summary>
    public void RegisterFrameDiagnosticsAnnotator(IFrameDiagnosticsAnnotator annotator)
    {
        ArgumentNullException.ThrowIfNull(annotator);
        _frameDiagnosticsAnnotator = annotator;
    }

    /// <summary>Register what the recorder's block boundaries drive while sampling. Nothing else turns a gate on.</summary>
    public void RegisterFrameDiagnosticsShaderGates(IFrameDiagnosticsShaderGates gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        _frameDiagnosticsShaderGates = gates;
    }

    /// <summary>Where a 3D surface forwards its render pass boundaries. Does nothing unless the recorder is sampling.</summary>
    public IFrameDiagnosticsPassSink FrameDiagnosticsPasses => FrameDiagnosticsPassForwarder.Instance;

    void ObserveFrameDiagnostics(FrameDiagnosticsRecorder diagnostics, double dt)
    {
        long timestamp = Stopwatch.GetTimestamp();
        if (!diagnostics.NeedsBoundary)
        {
            diagnostics.Observe(dt, timestamp, default, default);
            return;
        }
        diagnostics.Observe(dt, timestamp,
            new FrameDiagnosticsWindowState(_accumulator.IsFocused, _minimized,
                !_backgroundThrottle.Plan(new WindowActivity(_accumulator.IsFocused, _minimized), _effectiveBaseCapHz).RenderAndPresent,
                _window.FramebufferSize.X, _window.FramebufferSize.Y, _effectiveBaseCapHz, _presentMode == PresentMode.Vsync,
                RendererInitializesSwapchain), _device.Counters,
            new FrameDiagnosticsCaptureState(_captureTaps?.Count ?? 0,
                (_device as IGpuSwapchainCapture)?.SwapchainCopySourceRequested ?? false));
    }
}
