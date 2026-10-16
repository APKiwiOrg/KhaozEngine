using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A scene on the recording device. Rendering runs the real CPU path into a null command list, no GPU.</summary>
internal sealed class CoverageCostRecordingScene : IDisposable
{
    internal const int Width = 64, Height = 48;
    readonly CoverageCaptureDevice device = new(GpuBackendKind.Vulkan);
    readonly IGpuTexture texture;
    readonly IGpuFramebuffer target;
    readonly NullGpuCommandList commands = new();
    readonly int width, height;
    internal Scene3D Scene { get; }

    internal CoverageCostRecordingScene(int width = Width, int height = Height)
    {
        this.width = width; this.height = height;
        var tracker = new UniformBufferTrackingGpuDevice(device);
        texture = tracker.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = device.Factory.CreateFramebuffer(null, texture);
        Scene = new Scene3D(tracker, target.Outputs);
    }

    internal void Render() => Scene.RenderInternal(commands, width, height, target);

    public void Dispose()
    {
        Scene.Dispose();
        target.Dispose();
        texture.Dispose();
    }
}

/// <summary>
/// The CPU-only measurement target for a hosted CPU runner with no GPU driver. The engine's real preparation,
/// certification, resolution and recording code runs against the recording device. Nothing is submitted, so
/// Submit plus WaitForIdle and every GPU metric are unavailable, and encode time is recording into a null command
/// list rather than a native backend encode. Reports are synthetic when the clock is not the Stopwatch clock, and
/// measurements otherwise, labelled as recording-device CPU work.
/// </summary>
internal sealed class CoverageCostRecordingSession : ICoverageCostSession
{
    internal const int Width = CoverageCostNativeSession.Width, Height = CoverageCostNativeSession.Height;
    readonly CoverageCostRecordingScene scene;
    readonly CoverageCostWorkload workload;
    readonly Func<long> clock;
    readonly int width, height;
    int frame;

    CoverageCostRecordingSession(CoverageCostManifest manifest, CoverageCostClock clock, int width, int height)
    {
        this.clock = clock.Now;
        this.width = width; this.height = height;
        scene = new(width, height);
        Synthetic = !ReferenceEquals(clock, CoverageCostClock.Stopwatch);
        workload = new CoverageCostWorkload(scene.Scene, CoverageCostGeometry.Build(manifest.Fixture), manifest.Actors, manifest.Coverage,
            manifest.GpuSkinning, manifest.Passes, this.clock);
        Info = new("recording-cpu", "none", "recording", workload.Camera, workload.Light,
            workload.Layout + $", {width} x {height} display target",
            (Synthetic ? "injected test clock" : "Stopwatch ticks")
            + ", Scene3D.EnableTiming on, coverage stage, resolution and surface observers installed, no GPU", workload.Topology);
    }

    public bool Synthetic { get; }
    public bool NativeDevice => false;
    public bool ObservesOrdinarySkinning => true;
    public CoverageCostSessionInfo Info { get; private set; }

    internal static ICoverageCostSession Open(CoverageCostManifest manifest, CoverageCostClock clock) =>
        Open(manifest, clock, Width, Height);

    /// <summary>A smaller display target keeps bounded functional tests cheap. Measurements use the default size.</summary>
    internal static ICoverageCostSession Open(CoverageCostManifest manifest, CoverageCostClock clock, int width, int height)
    {
        if (!manifest.Recording) throw new ArgumentException("A recording session needs the recording-cpu device.", nameof(manifest));
        return new CoverageCostRecordingSession(manifest, clock, width, height);
    }

    public void Frame(bool measured, ref CoverageCostFrameSample sample)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = clock();
        workload.Submit(frame++, ref sample);
        workload.Reserve(width, height, ref sample);
        long encode = clock();
        scene.Render();
        sample.EncodeTicks = clock() - encode;
        workload.Collect(ref sample, measured);
        sample.FrameTicks = clock() - start;
        sample.FrameBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        if (frame == 1) Info = Info with { Topology = workload.Topology };
    }

    public IReadOnlyList<CoverageCostMetric> ReadCapacities() => workload.ReadCapacities();
    public IReadOnlyList<KeyValuePair<string, int>> ReadRefusals() => workload.ReadRefusals();
    public IReadOnlyList<CoverageCostMetric> ReadEvidence() => workload.ReadEvidence();

    public void Dispose()
    {
        workload.Dispose();
        scene.Dispose();
    }
}
