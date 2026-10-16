using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The real measurement target: the manifest's exact native backend through
/// <see cref="GpuDeviceContext.CreateHeadless(GpuBackendKind)"/>, which never falls back to another backend. A
/// frame is Begin, preparation and queueing, PrepareFrame, CPU recording, Submit plus WaitForIdle and Present.
/// Submit plus WaitForIdle is completed wall time including submission and the CPU wait, not isolated GPU
/// execution. Only <see cref="CoverageCostHarness.Measure"/> opens this session.
/// </summary>
internal sealed class CoverageCostNativeSession : ICoverageCostSession
{
    internal const int Width = 1280, Height = 720;
    // Known software and paravirtual adapters. They prove function, never native performance.
    static readonly string[] SoftwareAdapters = ["llvmpipe", "lavapipe", "SwiftShader", "Microsoft Basic Render Driver", "WARP",
        "Apple Paravirtual device"];
    readonly GpuDeviceContext gpu;
    readonly IGpuTexture texture;
    readonly IGpuFramebuffer target;
    readonly IGpuCommandList commands;
    readonly Scene3D scene;
    readonly CoverageCostWorkload workload;
    readonly Func<long> clock;
    int frame;

    CoverageCostNativeSession(CoverageCostManifest manifest, CoverageCostClock clock, GpuDeviceContext gpu, string adapterClass)
    {
        this.gpu = gpu;
        this.clock = clock.Now;
        IGpuResourceFactory factory = gpu.GpuDevice.Factory;
        texture = factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = factory.CreateFramebuffer(null, texture);
        commands = factory.CreateCommandList();
        scene = new Scene3D(gpu.GpuDevice, target.Outputs) { RenderOrigin = System.Numerics.Vector3.Zero };
        workload = new CoverageCostWorkload(scene, CoverageCostGeometry.Build(manifest.Fixture), manifest.Actors, manifest.Coverage,
            manifest.GpuSkinning, manifest.Passes, this.clock, executesOnGpu: true);
        Info = new(gpu.Selection.Backend.ToString(), gpu.AdapterDescription, adapterClass, workload.Camera, workload.Light,
            workload.Layout + $", {Width} x {Height} display target",
            "Stopwatch ticks, Scene3D.EnableTiming on, coverage stage, resolution and surface observers installed", workload.Topology);
    }

    public bool Synthetic => false;
    public bool NativeDevice => true;
    public bool ObservesSkinnedPreparation => true;
    public CoverageCostSessionInfo Info { get; private set; }

    internal static ICoverageCostSession Open(CoverageCostManifest manifest, CoverageCostClock clock)
    {
        GpuBackendKind backend = manifest.Backend ?? throw new ArgumentException("A native session needs a native backend.", nameof(manifest));
        GpuDeviceContext gpu;
        try { gpu = GpuDeviceContext.CreateHeadless(backend); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new CoverageCostRefusedException($"{backend} device unavailable: {ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            if (gpu.Selection.Backend != backend)
                throw new CoverageCostRefusedException($"Opened {gpu.Selection.Backend}, manifest requires {backend}.");
            string adapter = gpu.AdapterDescription;
            bool software = Array.Exists(SoftwareAdapters, s => adapter.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (manifest.RejectSoftwareAdapter && (software || adapter.Length == 0))
                throw new CoverageCostRefusedException($"Adapter '{adapter}' is software, paravirtual or unnamed.");
            if (manifest.AdapterContains is { } required && !adapter.Contains(required, StringComparison.Ordinal))
                throw new CoverageCostRefusedException($"Adapter '{adapter}' does not contain '{required}'.");
            return new CoverageCostNativeSession(manifest, clock, gpu, software ? "known-software-or-paravirtual" : "unclassified");
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    public void Frame(bool measured, ref CoverageCostFrameSample sample)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = clock();
        workload.Submit(frame++, ref sample);
        workload.Reserve(Width, Height, ref sample);
        long encode = clock();
        using (GpuRecording.Open(gpu.GpuDevice, commands, "CoverageCost.Frame"))
            scene.RenderInternal(commands, Width, Height, target);
        long submitted = clock();
        gpu.GpuDevice.Submit(commands);
        gpu.GpuDevice.WaitForIdle();
        long drained = clock();
        gpu.GpuDevice.Present();
        sample.EncodeTicks = submitted - encode;
        sample.SubmitWaitTicks = drained - submitted;
        workload.Collect(ref sample, measured);
        sample.FrameTicks = clock() - start;
        sample.FrameBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        // The conformer's own topology is observed once a preparation is accepted.
        if (frame == 1) Info = Info with { Topology = workload.Topology };
    }

    public IReadOnlyList<CoverageCostMetric> ReadCapacities() => workload.ReadCapacities();
    public IReadOnlyList<KeyValuePair<string, int>> ReadRefusals() => workload.ReadRefusals();
    public IReadOnlyList<CoverageCostMetric> ReadEvidence() => workload.ReadEvidence();

    public void Dispose()
    {
        workload.Dispose();
        scene.Dispose();
        commands.Dispose();
        target.Dispose();
        texture.Dispose();
        gpu.Dispose();
    }
}
