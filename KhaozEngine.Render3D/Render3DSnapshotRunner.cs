using System;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D;

/// <summary>Owns the headless scene and submission loop shared by final-frame and sequence captures.</summary>
internal static class Render3DSnapshotRunner
{
    internal static void Validate(int width, int height, int frames, int warmupFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(warmupFrames, frames);
        if ((long)width * height > Array.MaxLength / 4)
            throw new ArgumentOutOfRangeException(nameof(width), "the RGBA8 image exceeds the maximum array length.");
    }

    internal static void Capture(int width, int height, Action<Scene3D> setup,
        Action<Scene3D, int> drawFrame, int frames, Action<int, Render3DCapture> onFrame,
        int firstReadbackFrame, ShadowSettings? shadows)
    {
        // A headless host has no AppWindow to register the native backend. Keep providers already seated by
        // the caller, and register both the resolved backend and the platform's fallback only when absent.
        GpuBackends.RegisterResolvedIfUnregistered();
        // Keep the original snapshot's no-depth, no-sync, standard clip-Y headless options.
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = gpu.GpuDevice;
        var factory = gd.Factory;
        using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
            (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
        using var scene = new Scene3D(gd, framebuffer.Outputs, shadows);
        using IGpuCommandList commands = factory.CreateCommandList();
        try
        {
            setup(scene);
            for (int frame = 0; frame < frames; frame++)
            {
                scene.Begin();
                drawFrame(scene, frame);
                // Producers run before opening the render list. Nested recording corrupts Direct3D11's
                // immediate context, and PrepareFrame also advances pending temporal diagnostics.
                scene.PrepareFrame();
                using (GpuRecording.Open(gd, commands, "Render3DSnapshot.Capture"))
                    scene.RenderInternal(commands, width, height, framebuffer);
                gd.Submit(commands);
                if (frame < firstReadbackFrame) continue;
                byte[] rgba = GpuReadback.ToRgba(gd, target, width, height);
                onFrame(frame, new Render3DCapture(rgba, width, height, gd.Backend));
            }
        }
        finally
        {
            // Also drain an all-warm-up sequence or a callback failure before disposing the command list.
            gd.WaitForIdle();
        }
    }
}
