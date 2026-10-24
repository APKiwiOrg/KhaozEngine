using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Windowing;

internal static class RendererFramePolicy
{
    internal static void Run(Frame frame, bool render, IGpuDevice device, IGpuCommandList commands,
        Color clearColor, Action<Frame>? prepare, Action<Frame> record, FrameCaptureTaps? taps,
        bool rendererInitializesSwapchain)
        => FramePhases.Run(frame, render, device, commands, clearColor, prepare, record, taps, 16,
            rendererInitializesSwapchain);
}
