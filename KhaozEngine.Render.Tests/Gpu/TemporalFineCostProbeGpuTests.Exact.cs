using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFineCostProbeGpuTests
{
    const int CutSample = Samples, AfterCutSample = CutSample + 1, ExactSamples = AfterCutSample + 1;
    static readonly string[] ExactFinePasses = ["temporal.detail-classify", "temporal.resolve", "temporal.depth-store",
        "temporal.visibility-store", "temporal.detail-store"];
    static readonly string[] ExactDisplayPasses = ["temporal.prepare", "temporal.accumulate", "temporal.visibility-store"];

    [GpuFact(RequiresRealGpu = true)]
    public void Exact_Native_fully_lit() => Exact(TemporalUpscale.Native, false);

    [GpuFact(RequiresRealGpu = true)]
    public void Exact_Native_shadowed() => Exact(TemporalUpscale.Native, true);

    [GpuFact(RequiresRealGpu = true)]
    public void Exact_Quality_fully_lit() => Exact(TemporalUpscale.Quality, false);

    [GpuFact(RequiresRealGpu = true)]
    public void Exact_Quality_shadowed() => Exact(TemporalUpscale.Quality, true);

    void Exact(TemporalUpscale preset, bool shadowed)
    {
        using var gpu = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
        Assert.Equal(GpuBackendKind.MetalNative, gpu.GpuDevice.Backend);
        Assert.False(TemporalResolveCostPerfGpuTests.IsSoftwareDevice(gpu.GpuDevice.Capabilities.DeviceName));
        string scene = shadowed ? "shadowed" : "fully-lit";
        string[] paths = (Environment.GetEnvironmentVariable("KE_TAA_EXACT_PATHS") ?? "moving,still,pan08,pan33").Split(',');
        string[] names = (Environment.GetEnvironmentVariable("KE_TAA_EXACT_LEVERS")
            ?? string.Join(',', Enum.GetNames<TemporalFineExactLever>())).Split(',');
        var failures = new List<Exception>();
        foreach (string path in paths)
        {
            string key = $"EXACT|{preset}|{scene}|{path}";
            output.WriteLine($"{key}|capture|{DateTime.UtcNow:O}|{gpu.GpuDevice.Capabilities.DeviceName}|{Width}x{Height}");
            if (Environment.GetEnvironmentVariable("KE_TAA_EXACT_SKIP_BASELINE") != "1")
                MeasureBaseline(gpu.GpuDevice, preset, shadowed, path, key);
            foreach (string name in names)
            {
                if (name.Length == 0) continue;
                try { MeasurePair(gpu.GpuDevice, preset, shadowed, path, key, name); }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    failures.Add(new InvalidOperationException($"{key}|{name}", failure));
                    output.WriteLine($"{key}|{name}|unmeasured|{failure}");
                }
            }
        }
        if (failures.Count > 0) throw new AggregateException("Some exact probes did not complete.", failures);
    }

    void MeasureBaseline(IGpuDevice device, TemporalUpscale preset, bool shadowed, string path, string key)
    {
        using var fine = new ExactArm(device, preset, shadowed, path, true, null);
        using var display = new ExactArm(device, preset, shadowed, path, false, null);
        for (int frame = 0; frame < ExactSamples; frame++)
        {
            RenderPair(fine, display, frame);
            WriteFrame(key, "baseline", "fine", frame, fine);
            WriteFrame(key, "baseline", "display", frame, display);
        }
    }

    void MeasurePair(IGpuDevice device, TemporalUpscale preset, bool shadowed, string path, string key, string name)
    {
        var levers = TemporalFineExactPrograms.Parse(name);
        if (path == "moving" && shadowed && Environment.GetEnvironmentVariable("KE_TAA_EXACT_PATCHES") is { Length: > 0 } directory)
            TemporalFineExactPrograms.Export(directory, $"{preset}.{name}", TemporalResolvePrecisionPolicy.For(device),
                levers, Topology(preset, levers));
        using var control = new ExactArm(device, preset, shadowed, path, true, null);
        using var trial = new ExactArm(device, preset, shadowed, path, true, levers);
        using var oracle = new TemporalFineExactOracle(device, control.Fixture, trial.Fixture);
        for (int frame = 0; frame < ExactSamples; frame++)
        {
            RenderPair(control, trial, frame);
            WriteFrame(key, name, "control", frame, control);
            WriteFrame(key, name, "trial", frame, trial);
            foreach (string result in oracle.Compare())
                output.WriteLine($"{key}|{name}|bytes|{frame}|{result}");
        }
    }

    static void RenderPair(ExactArm first, ExactArm second, int frame)
    {
        if (frame % 2 != 0) (first, second) = (second, first);
        first.Render(frame);
        second.Render(frame);
    }

    void WriteFrame(string key, string lever, string arm, int frame, ExactArm capture)
    {
        output.WriteLine($"{key}|{lever}|{arm}|{frame}|{string.Join('|',
            capture.PassNames.Zip(capture.LastTimes, (pass, ms) => $"{pass}={F(ms)}"))}|sum={F(capture.LastTimes.Sum())}");
    }

    sealed class ExactArm : IDisposable
    {
        readonly GpuPerfTimestampRing _ring;
        readonly GpuFrameSubmission _submission;
        readonly ExactPassTimes _times;
        readonly Action<Scene3D, int> _draw;
        readonly GpuPerfTimestampResult?[] _results = new GpuPerfTimestampResult?[ExactSamples];
        readonly uint _markers;
        readonly bool _fine;
        readonly bool _shadowed;
        internal TemporalFixture Fixture { get; }
        internal string[] PassNames { get; }
        internal double[] LastTimes { get; }

        internal ExactArm(IGpuDevice device, TemporalUpscale preset, bool shadowed, string path, bool fine,
            TemporalFineExactLever[]? levers)
        {
            _fine = fine;
            _shadowed = shadowed;
            var topology = Topology(preset, levers);
            var stage = new FrontStage(Width, Height, 4.5f);
            Fixture = new TemporalFixture(Width, Height, scene =>
            {
                stage.Setup(scene, AntiAliasing.Temporal, preset);
                scene.Post.Temporal.HistoryDetail = fine ? TemporalHistoryDetail.Fine : TemporalHistoryDetail.Display;
                scene.TemporalResolveEntryForTests = fine ? TemporalResolveEntry.Fused : TemporalResolveEntry.Split;
                if (shadowed)
                {
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.4f, -0.6f, -1f));
                    scene.Post.AmbientColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                }
                if (levers is not null)
                    scene.InstallTemporalResolveForTests(source => TemporalFineExactPrograms.Rewrite(source, levers, topology), topology);
            }, device);
            PassNames = fine ? ExactFinePasses.Where(pass =>
                !(topology.AttachDepth && pass == "temporal.depth-store")
                && !(topology.AttachVisibility && pass == "temporal.visibility-store")).ToArray() : ExactDisplayPasses;
            LastTimes = new double[PassNames.Length];
            _markers = (uint)(2 * PassNames.Length + 2);
            _draw = DrawPath(stage, path);
            try
            {
                Fixture.Frames(Warm, _draw);
                CheckMode();
                byte[] visibility = TemporalVisibilityHistoryGpuTests.Read(device, Fixture.Scene.ResolvedVisibilityTarget!);
                Assert.Equal(shadowed, visibility.Any(b => b < byte.MaxValue));
                _ring = new GpuPerfTimestampRing(device, ExactSamples, _markers, (id, result) => _results[id] = result);
                _submission = new GpuFrameSubmission(device);
                _times = new ExactPassTimes(PassNames);
                Fixture.Scene.PassObserver = _times;
            }
            catch
            {
                Fixture.Dispose();
                throw;
            }
        }

        void CheckMode()
        {
            var scene = Fixture.Scene;
            var history = scene.TemporalHistory;
            if (_fine && history.DetailFailure is { } failure)
                throw new InvalidOperationException("Fine creation refused the test program.", failure);
            Assert.Equal(_fine ? TemporalHistoryDetail.Fine : TemporalHistoryDetail.Display, scene.TemporalHistoryDetailInEffect);
            var renderer = scene.TemporalResolveRendererForTests!;
            Assert.Equal(_fine ? TemporalResolveEntry.Fused : TemporalResolveEntry.Split, renderer.LastEntry);
            Assert.Equal(!_shadowed, renderer.LastCurrentVisibilityFullyLit);
        }

        internal void Render(int frame)
        {
            if (frame == CutSample) Fixture.Scene.CameraCut();
            Fixture.ProfileFrame(_draw, _submission, commands =>
            {
                if (!_ring.TryBegin(commands, _submission, frame, _markers, out var pool))
                    throw new InvalidOperationException(_ring.Failure);
                _times.BeginFrame(pool);
            }, commands =>
            {
                if (_times.Next != _markers - 1)
                    throw new InvalidOperationException($"Incomplete temporal pass capture: {_times.Next} of {_markers - 1}.");
                ((IGpuCommandListTimestampEnd)commands).FinishTimestampsOnEnd(_ring.ActivePool!, _markers - 1);
            });
            _ring.AwaitAvailability(Stopwatch.GetTimestamp, long.MaxValue);
            _ring.PollCompleted();
            CheckMode();
            if (frame == CutSample) Assert.Equal(TemporalResetReason.CameraCutRequested, Fixture.Scene.TemporalHistory.LastReset);
            if (frame == AfterCutSample && !_ring.CompleteBlock()) throw new InvalidOperationException(_ring.Failure);
            if (_results[frame] is not { Valid: true, Period: { } period } result)
                throw new InvalidOperationException("Native timestamps were invalid or absent.");
            for (int i = 0; i < LastTimes.Length; i++)
                LastTimes[i] = (result.Ticks[2 * i + 2] - result.Ticks[2 * i + 1]) * period / 1e6;
        }

        public void Dispose()
        {
            Fixture.Scene.PassObserver = null;
            _submission.Dispose();
            _ring.Dispose();
            Fixture.Dispose();
        }
    }

    static TemporalDetailPassProbe Topology(TemporalUpscale preset, TemporalFineExactLever[]? levers) => new(
        preset == TemporalUpscale.Native && levers?.Contains(TemporalFineExactLever.AttachDepth) == true,
        levers?.Contains(TemporalFineExactLever.AttachVisibility) == true,
        levers?.Contains(TemporalFineExactLever.ArrayStore) == true);

    static Action<Scene3D, int> DrawPath(FrontStage stage, string path)
    {
        var field = TemporalResolveCostPerfGpuTests.FieldScene(stage);
        if (path == "moving") return field;
        float pixels = path switch
        {
            "still" => 0f,
            "pan08" => 0.08f,
            "pan33" => 1f / 3f,
            _ => throw new ArgumentException($"Unknown test path {path}.", nameof(path)),
        };
        return (scene, frame) =>
        {
            scene.Camera.Target = Vector3.Zero;
            field(scene, 0);
            scene.Camera.Target = new Vector3(pixels * stage.PixelWorld * frame, 0f, 0f);
        };
    }

    sealed class ExactPassTimes(string[] passes) : IRenderPassObserver
    {
        IGpuTimestampPool? _pool;
        readonly HashSet<string> _selected = new(passes, StringComparer.Ordinal);
        internal uint Next { get; private set; }

        internal void BeginFrame(IGpuTimestampPool pool) { _pool = pool; Next = 1; }

        public void BeginPass(IGpuCommandList commands, RenderPassKey key)
        {
            if (_selected.Contains(key.Id)) commands.WriteTimestamp(_pool!, Next++);
        }

        public void EndPass(IGpuCommandList commands, RenderPassKey key)
        {
            if (_selected.Contains(key.Id)) commands.WriteTimestamp(_pool!, Next++);
        }

        public void SkipPass(IGpuCommandList commands, RenderPassKey key) { }
        public void BeginCpuPhase(string id) { }
        public void EndCpuPhase(string id) { }
    }
}
