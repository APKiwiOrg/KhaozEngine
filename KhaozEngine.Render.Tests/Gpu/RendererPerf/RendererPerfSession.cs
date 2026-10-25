using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfClock(Func<long> Now, long Frequency);
internal delegate IRendererPerfSession RendererPerfSessionFactory(RendererPerfManifest manifest,
    RendererPerfBlock block, RendererPerfClock clock);

internal interface IRendererPerfSession : IDisposable
{
    void Frame(int frameIndex, ref RendererPerfFrameSample sample);
    void CompleteBatch(Span<RendererPerfFrameSample> samples);
    RendererPerfSessionInfo ReadInfo();
}

internal sealed record RendererPerfSessionInfo(RendererPerfRunIdentity Identity, bool Enabled,
    bool CompletionFences, RendererPerfRecipeReport? Recipe,
    ImmutableDictionary<string, RendererPerfMetric> Telemetry, string? Failure = null,
    string? ControlStateSha256 = null, RendererPerfMemoryReport? Memory = null,
    RendererPerfCpuSpan? Drain = null, RendererPerfCpuSpan? Harvest = null, RendererPerfStartupReport? Startup = null);

// The factory supplies a cold, frozen pass layout. No native discovery frames are allowed.
internal interface IRendererPerfNativeWork : IDisposable
{
    IGpuFramebuffer Target { get; }
    Color ClearColor { get; }
    RendererPerfRecipeReport? Recipe { get; }
    string? ControlStateSha256 => null;
    ImmutableDictionary<string, RendererPerfMetric> ControlMetrics => ImmutableDictionary<string, RendererPerfMetric>.Empty;
    void PrepareCold(int firstFrameIndex);
    void Prepare(int frameIndex);
    void Prepare(int frameIndex, GpuFrameSubmission submission) => Prepare(frameIndex);
    void Record(IGpuCommandList commands, int frameIndex);
    void ValidateOutput() { }
}

internal interface IRendererPerfPublication
{
    RendererPerfReport Publish(RendererPerfReport report, RendererPerfClock clock, long deadline);
}

internal sealed record RendererPerfBatchReport(string Operation, int FirstFrame, int Count,
    long StartTicks, long EndTicks, long Frequency, RendererPerfCpuSpan? Drain,
    RendererPerfCpuSpan? Harvest, ImmutableDictionary<string, RendererPerfMetric> Telemetry);

internal sealed record RendererPerfStartupReport(string Operation, RendererPerfCpuSpan Wall, long Frequency,
    ImmutableDictionary<string, RendererPerfMetric> Metrics);
