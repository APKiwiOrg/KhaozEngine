using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

// These in-memory audit fixtures authorize no native work. All time is explicitly injected numeric time.
internal sealed class CoveragePerfFakeClock
{
    internal long Ticks { get; set; }
    internal int Reads { get; private set; }
    internal CoveragePerfClock Clock(long frequency = 1000) => new(() => { Reads++; return Ticks; }, frequency);
    internal void Advance(long ticks) => Ticks = checked(Ticks + ticks);
}

internal delegate void CoveragePerfFakeFrame(CoveragePerfFakeSession session, int index, ref CoveragePerfFrameSample sample);

internal sealed class CoveragePerfFakeSession : ICoveragePerfSession
{
    internal CoveragePerfBlock Block { get; }
    internal CoveragePerfSessionInfo Info { get; set; }
    internal CoveragePerfFakeFrame? OnFrame { get; set; }
    internal Action<CoveragePerfFakeSession>? OnBatch { get; set; }
    internal Action<CoveragePerfFakeSession>? OnReadInfo { get; set; }
    internal Action<CoveragePerfFakeSession>? OnDispose { get; set; }
    internal int Frames { get; private set; }
    internal int Batches { get; private set; }
    internal bool Disposed { get; private set; }

    internal CoveragePerfFakeSession(CoveragePerfManifest manifest, CoveragePerfBlock block)
    {
        Block = block;
        Info = new()
        {
            Synthetic = true,
            Backend = manifest.Backend,
            Adapter = "synthetic",
            Vendor = "synthetic",
            Device = "synthetic",
            Driver = "synthetic",
            TimestampSupported = block.TimerLayout != CoveragePerfTimerLayout.CpuOnly,
            TimestampPeriodNanoseconds = block.TimerLayout == CoveragePerfTimerLayout.CpuOnly ? null : 1,
            OrdinarySkinningRoute = CoveragePerfManifest.OrdinarySkinningRoute,
            CoveredSurfaceRoute = CoveragePerfManifest.CoveredSurfaceRoute,
            FixtureFingerprint = "synthetic-fixture",
            ProfileFingerprint = "synthetic-profile",
            CompletionPolicy = "synthetic fixed batches",
        };
    }

    public void Frame(int frameIndex, ref CoveragePerfFrameSample sample)
    {
        Frames++;
        sample = new()
        {
            FrameIndex = frameIndex,
            PoseId = frameIndex % 2,
            FrameTicks = 1,
            CpuFramePreparationTicks = 1,
            CpuChildren = [],
            Gpu = Block.TimerLayout == CoveragePerfTimerLayout.CpuOnly ? null :
                new(Block.TimerLayout, [1, 2], 1, true, false, "synthetic"),
            Actors = Block.Actors,
            MainVisibleActors = Block.Actors,
            Passes = 1,
            Submissions = 1,
            StageComplete = true,
            DeviceComplete = true,
            PoseFingerprint = "synthetic-pose",
            PassFingerprint = "synthetic-passes",
        };
        OnFrame?.Invoke(this, frameIndex, ref sample);
    }

    public void CompleteBatch(Span<CoveragePerfFrameSample> samples) { Batches++; OnBatch?.Invoke(this); }
    public CoveragePerfSessionInfo ReadInfo() { OnReadInfo?.Invoke(this); return Info; }
    public void Dispose() { Disposed = true; OnDispose?.Invoke(this); }
}

internal delegate void CoveragePerfFakeBatch(Span<CoveragePerfFrameSample> samples);

internal sealed class CoveragePerfDeferredFakeSession(CoveragePerfFakeSession session) : ICoveragePerfSession
{
    int _pendingFrames, _nextCompletionFrame;
    internal CoveragePerfFakeBatch? OnCompleteBatch { get; set; }

    public void Frame(int frameIndex, ref CoveragePerfFrameSample sample)
    {
        session.Frame(frameIndex, ref sample);
        sample.Gpu = null;
        _pendingFrames++;
    }

    public void CompleteBatch(Span<CoveragePerfFrameSample> samples)
    {
        if (samples.Length != _pendingFrames) throw new InvalidOperationException("Completion did not receive the pending batch.");
        for (int i = 0; i < samples.Length; i++)
            if (samples[i].FrameIndex != _nextCompletionFrame + i || samples[i].Gpu is not null)
                throw new InvalidOperationException("Completion did not receive the unresolved frame slots in order.");
        if (OnCompleteBatch is { } complete) complete(samples);
        else Resolve(samples);
        _nextCompletionFrame += samples.Length;
        _pendingFrames = 0;
        session.CompleteBatch(samples);
    }

    internal void Resolve(Span<CoveragePerfFrameSample> samples)
    {
        if (session.Block.TimerLayout == CoveragePerfTimerLayout.CpuOnly) return;
        foreach (ref var sample in samples)
            sample.Gpu = new(session.Block.TimerLayout, [(ulong)sample.FrameIndex + 1, (ulong)sample.FrameIndex + 2],
                1, true, false, "synthetic deferred");
    }

    public CoveragePerfSessionInfo ReadInfo() => session.ReadInfo();
    public void Dispose() => session.Dispose();
}

internal sealed class CoveragePerfFakePublisher
{
    internal List<CoveragePerfBlockResult> Blocks { get; } = [];
    internal List<CoveragePerfReport> Units { get; } = [];
    internal Action<CoveragePerfBlockResult>? OnBlock { get; set; }
    internal Action<CoveragePerfReport>? OnUnit { get; set; }

    internal void PublishBlock(CoveragePerfBlockResult block) { Blocks.Add(block); OnBlock?.Invoke(block); }
    internal void PublishUnit(CoveragePerfReport report) { Units.Add(report); OnUnit?.Invoke(report); }
}

internal sealed class CoveragePerfFakeRun
{
    internal CoveragePerfFakeClock Time { get; } = new();
    internal CoveragePerfFakePublisher Publisher { get; } = new();
    internal List<CoveragePerfFakeSession> Sessions { get; } = [];
    internal List<string> Events { get; } = [];
    internal Action<CoveragePerfFakeSession>? Setup { get; set; }
    internal Func<CoveragePerfFakeSession, ICoveragePerfSession>? WrapSession { get; set; }
    internal byte[] Json { get; set; }
    internal CoveragePerfRunIdentity Identity { get; set; }

    internal CoveragePerfFakeRun(string presetName = "C0")
    {
        var preset = CoveragePerfManifest.Presets.Single(p => p.Name == presetName);
        Json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = CoveragePerfManifest.Schema,
            preset = preset.Name,
            unitId = "inline-driver",
            workloadSha = new string('1', 40),
            baselineMainSha = new string('3', 40),
            fixture = CoveragePerfManifest.FixtureVersion,
            profile = CoveragePerfManifest.ProfileVersion,
            configuration = CoveragePerfManifest.ReleaseConfiguration,
            routes = new { ordinarySkinning = CoveragePerfManifest.OrdinarySkinningRoute, coveredSurface = CoveragePerfManifest.CoveredSurfaceRoute },
            device = new
            {
                backend = preset.Backend == GpuBackendKind.Direct3D11Native ? "direct3d11-native" : "vulkan-native",
                adapterPolicy = preset.AdapterPolicy == CoveragePerfAdapterPolicy.NvidiaT4 ? "nvidia-t4" : "lavapipe-cpu-only",
            },
            batchFrames = CoveragePerfManifest.BatchFrameCount,
            runner = preset.Runner,
            workflow = preset.Workflow,
            leg = preset.Leg,
            iterations = CoveragePerfManifest.IterationCount,
            load = CoveragePerfManifest.AddedLoad,
            limits = new { maxUnitSeconds = preset.MaxUnitSeconds, maxFrameMilliseconds = CoveragePerfManifest.MaxFrameMillisecondsValue, jobMinutes = preset.JobMinutes },
            authorization = new
            {
                identity = CoveragePerfManifest.AuthorizationPrefix + "inline-driver",
                approvedBy = "inline fixture, no owner approval",
                reference = "Protocol test only, no measurement authorized",
                workflowSha = new string('2', 40),
                workflowRunId = "123456789",
                workflowRunAttempt = 1,
            },
            blocks = preset.Blocks.Select(b => new
            {
                index = b.Index,
                actors = b.Actors,
                condition = b.Condition.ToString(),
                timerLayout = b.TimerLayout.ToString(),
                warmupFrames = b.WarmupFrames,
                measuredFrames = b.MeasuredFrames,
                fixture = b.Fixture,
            }),
        });
        Identity = new(new string('1', 40), new string('2', 40), "inline-driver", Hash(Json), preset.Runner,
            preset.Workflow, preset.Leg, "123456789", 1, 42, CoveragePerfManifest.AuthorizationPrefix + "inline-driver");
    }

    internal void ReplaceJson(string from, string to)
    {
        Json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Json).Replace(from, to, StringComparison.Ordinal));
        Identity = Identity with { ManifestSha256 = Hash(Json) };
    }

    internal CoveragePerfReport Run(long frequency = 1000) => CoveragePerfDriver.Run(Json, Identity, Create,
        Time.Clock(frequency), b => { Events.Add($"block-{b.Block.Index}"); Publisher.PublishBlock(b); },
        r => { Events.Add("unit"); Publisher.PublishUnit(r); });

    ICoveragePerfSession Create(CoveragePerfManifest manifest, CoveragePerfBlock block, CoveragePerfClock clock)
    {
        Events.Add($"factory-{block.Index}");
        var session = new CoveragePerfFakeSession(manifest, block);
        Sessions.Add(session);
        session.OnDispose = s => Events.Add($"dispose-{s.Block.Index}");
        Setup?.Invoke(session);
        return WrapSession?.Invoke(session) ?? session;
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
