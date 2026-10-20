using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A caller-supplied monotonic source. Creating a clock does not read it.</summary>
internal sealed record CoveragePerfClock(Func<long> Now, long Frequency);

internal sealed record CoveragePerfCpuSpan(string Name, long Ticks);

/// <summary>Absent until native completion. Ticks and validity belong to the completed frame's timestamp layout.</summary>
internal sealed record CoveragePerfGpuResult(CoveragePerfTimerLayout Layout, ImmutableArray<ulong> Ticks,
    double PeriodNanoseconds, bool Valid, bool Disjoint, string Reason);

/// <summary>
/// Bounded per-frame storage filled by Frame and later completion. A null GPU result is unresolved, never zero cost.
/// Child spans are diagnostics under the aggregate preparation bracket, not an alternative gate denominator.
/// </summary>
internal struct CoveragePerfFrameSample
{
    internal int FrameIndex { get; set; }
    internal int PoseId { get; set; }
    internal long FrameTicks { get; set; }
    internal long CpuFramePreparationTicks { get; set; }
    internal ImmutableArray<CoveragePerfCpuSpan> CpuChildren { get; set; }
    internal CoveragePerfGpuResult? Gpu { get; set; }
    internal int Actors { get; set; }
    internal int MainVisibleActors { get; set; }
    internal int Passes { get; set; }
    internal int PointShadowFaces { get; set; }
    internal long UploadedBytes { get; set; }
    internal long ResourceGrowth { get; set; }
    internal int ProductionWaits { get; set; }
    internal int Submissions { get; set; }
    internal bool StageComplete { get; set; }
    internal bool DeviceComplete { get; set; }
    internal bool Fallback { get; set; }
    internal string? PoseFingerprint { get; set; }
    internal string? PassFingerprint { get; set; }
    internal string? Error { get; set; }
}

/// <summary>Read outside measurement. Defaults disclose missing evidence and cannot establish native proof.</summary>
internal sealed record CoveragePerfSessionInfo
{
    internal bool Synthetic { get; init; } = true;
    internal bool NativeDevice { get; init; }
    internal GpuBackendKind? Backend { get; init; }
    internal string Adapter { get; init; } = "unavailable";
    internal string Vendor { get; init; } = "unavailable";
    internal string Device { get; init; } = "unavailable";
    internal string Driver { get; init; } = "unavailable";
    internal bool SoftwareAdapter { get; init; }
    internal bool TimestampSupported { get; init; }
    internal double? TimestampPeriodNanoseconds { get; init; }
    internal string D3DRecordingMode { get; init; } = "unavailable";
    internal string OrdinarySkinningRoute { get; init; } = "unavailable";
    internal string CoveredSurfaceRoute { get; init; } = "unavailable";
    internal string FixtureFingerprint { get; init; } = "unavailable";
    internal string ProfileFingerprint { get; init; } = "unavailable";
    internal string CompletionPolicy { get; init; } = "unavailable";
    internal ImmutableArray<CoveragePerfEnvironmentValue> Environment { get; init; } = [];
}

internal sealed record CoveragePerfBlockResult(CoveragePerfBlock Block, CoveragePerfRunStatus Status, string Reason,
    CoveragePerfSessionInfo? Session, ImmutableArray<CoveragePerfFrameSample> Warmup, ImmutableArray<CoveragePerfFrameSample> Samples);

/// <summary>One retained block. Setup, completion drains, diagnostics and disposal are outside measured intervals.</summary>
internal interface ICoveragePerfSession : IDisposable
{
    void Frame(int frameIndex, ref CoveragePerfFrameSample sample);
    void CompleteBatch();
    CoveragePerfSessionInfo ReadInfo();
}

internal delegate ICoveragePerfSession CoveragePerfSessionFactory(CoveragePerfManifest manifest, CoveragePerfBlock block,
    CoveragePerfClock clock);
