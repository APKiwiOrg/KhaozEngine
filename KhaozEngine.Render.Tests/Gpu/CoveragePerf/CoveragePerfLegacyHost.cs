using System;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gpu;

internal static class CoveragePerfLegacyHost
{
    internal const string BaselineSha = "5d896b333";
    internal const string Sequence = "prepare>open>bind>clear>render>End>device.Submit>Present";
    internal static string SequenceFingerprint { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sequence)));
    internal static void Run(Frame frame, bool render, IGpuDevice device, IGpuCommandList commands,
        Color clearColor, Action<Frame>? onPrepare, Action<Frame> onFrame)
    {
        // Pinned to FramePhases.RunPlain at 5d896b333. Keep the recording scope's exceptional End policy too.
        onPrepare?.Invoke(frame);
        GpuRecordingScope recording = render
            ? GpuRecording.Open(device, commands, "the window's frame list")
            : default;
        try
        {
            if (render)
            {
                commands.SetFramebuffer(device.SwapchainFramebuffer!);
                commands.ClearColorTarget(0, clearColor);
            }
            onFrame(frame);
        }
        finally { recording.Dispose(); }
        if (render)
        {
            device.Submit(commands);
            device.Present();
        }
    }
}
