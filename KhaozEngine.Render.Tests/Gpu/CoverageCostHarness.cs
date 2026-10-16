using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A monotonic tick source and its frequency. The native session uses <see cref="Stopwatch"/>.</summary>
internal sealed record CoverageCostClock(Func<long> Now, long Frequency)
{
    internal static CoverageCostClock Stopwatch { get; } = new(System.Diagnostics.Stopwatch.GetTimestamp,
        System.Diagnostics.Stopwatch.Frequency);
}

/// <summary>A session declined to measure, for example an adapter outside the manifest's device requirement.</summary>
internal sealed class CoverageCostRefusedException(string message) : Exception(message);

/// <summary>
/// One frame's observations. Ticks are clock ticks summed over every actor in the frame. Nested spans keep their
/// own totals and the driver derives the exclusive remainder, so nothing is counted twice:
/// prepare.total contains skinning and geometry evaluation, encode.total contains resolution and surface append, and
/// resolution contains render certification. Bytes are allocated managed bytes on the measuring thread.
/// </summary>
internal struct CoverageCostFrameSample
{
    internal long PrepareTicks, SkinningTicks, GeometryTicks, SubmissionTicks, ReserveTicks, EncodeTicks, ResolutionTicks,
        CertificationTicks, SurfaceTicks, SubmitWaitTicks, FrameTicks;
    internal long PrepareBytes, ReserveBytes, ResolutionBytes, SurfaceBytes, FrameBytes;
    internal int ActorFrames, Accepted, Declined, Covered, Fallback, Skinned, Evaluated, Certified, Passes, PointFacePasses,
        MainVisible, OutlineGroups;
    internal RenderFrameStats Stats;
    internal Scene3DPassTimingsMs PassTimings;
}

/// <summary>An opened measurement target. Opening may allocate fixtures and devices. Frame fills one sample.</summary>
internal interface ICoverageCostSession : IDisposable
{
    /// <summary>True for fakes and recording devices. A synthetic report can never prove a budget.</summary>
    bool Synthetic { get; }
    /// <summary>False for the recording device, which has no GPU submission to observe.</summary>
    bool NativeDevice { get; }
    CoverageCostSessionInfo Info { get; }
    void Frame(bool measured, ref CoverageCostFrameSample sample);
    IReadOnlyList<CoverageCostMetric> ReadCapacities();
    IReadOnlyList<KeyValuePair<string, int>> ReadRefusals();
}

internal delegate ICoverageCostSession CoverageCostSessionFactory(CoverageCostManifest manifest, CoverageCostClock clock);

/// <summary>Entry point for the separately selected cost project. Ordinary tests never call it.</summary>
internal static class CoverageCostHarness
{
    /// <summary>Measures the authorized case on the manifest's exact native backend, with no fallback, or on the
    /// recording device for CPU-only work.</summary>
    internal static CoverageCostReport Measure(CoverageCostManifest manifest) => CoverageCostDriver.Run(manifest,
        manifest.Recording ? CoverageCostRecordingSession.Open : CoverageCostNativeSession.Open, CoverageCostClock.Stopwatch);
}

/// <summary>
/// Runs warm-up and measured frames of an authorized manifest through a session, enforcing the manifest's limits,
/// and materializes the report after the window. Parsing and authorization happen before the session factory runs,
/// so a refused manifest never opens a device or starts a frame.
/// </summary>
internal static class CoverageCostDriver
{
    internal static CoverageCostReport Run(ReadOnlySpan<byte> manifestJson, CoverageCostSessionFactory factory, CoverageCostClock clock)
    {
        CoverageCostManifest manifest;
        try { manifest = CoverageCostManifest.Parse(manifestJson); }
        catch (InvalidDataException ex)
        {
            string sha = manifestJson.IsEmpty ? "" : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifestJson));
            return Empty(CoverageCostRunStatus.Refused, ex.Message, null, sha, true);
        }
        return Run(manifest, factory, clock);
    }

    internal static CoverageCostReport Run(CoverageCostManifest manifest, CoverageCostSessionFactory factory, CoverageCostClock clock)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.AuthorizationIdentity))
            return Empty(CoverageCostRunStatus.Refused, "Manifest authorization identity is missing.", manifest, manifest.Sha256, true);
        // Every collector exists before the first frame.
        var samples = new CoverageCostFrameSample[manifest.MeasuredFrames];
        var warm = new CoverageCostFrameSample();
        long frameLimit = (long)Math.Ceiling(manifest.MaxFrameMilliseconds * clock.Frequency / 1000);
        long wallLimit = (long)Math.Ceiling(manifest.MaxWallSeconds * clock.Frequency);
        ICoverageCostSession session;
        try { session = factory(manifest, clock); }
        catch (CoverageCostRefusedException ex)
        {
            return Empty(CoverageCostRunStatus.Refused, ex.Message, manifest, manifest.Sha256, true);
        }
        using (session)
        {
            var status = CoverageCostRunStatus.Completed;
            string reason = "";
            int measured = 0, errors = 0, warmed = 0;
            long start = clock.Now();
            try
            {
                for (; warmed < manifest.WarmupFrames && reason.Length == 0; warmed++)
                {
                    warm = default;
                    session.Frame(false, ref warm);
                    reason = Limits(warm.FrameTicks, frameLimit, clock.Now() - start, wallLimit);
                }
                for (; measured < manifest.MeasuredFrames && reason.Length == 0; measured++)
                {
                    session.Frame(true, ref samples[measured]);
                    reason = Limits(samples[measured].FrameTicks, frameLimit, clock.Now() - start, wallLimit);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors = 1;
                reason = $"{ex.GetType().Name}: {ex.Message}";
            }
            if (reason.Length > 0) status = CoverageCostRunStatus.Failed;
            return Materialize(manifest, session, clock, samples[..measured], warmed, errors, status, reason);
        }
    }

    static string Limits(long frameTicks, long frameLimit, long elapsed, long wallLimit) =>
        frameTicks > frameLimit ? "A frame exceeded limits.maxFrameMilliseconds."
        : elapsed > wallLimit ? "The run exceeded limits.maxWallSeconds." : "";

    static CoverageCostReport Empty(CoverageCostRunStatus status, string reason, CoverageCostManifest? manifest, string sha, bool synthetic) =>
        new(status, reason, manifest, sha, synthetic, CoverageCostEnvironment.Read(), null, 0, 0, [], [], [], [], []);

    static CoverageCostReport Materialize(CoverageCostManifest manifest, ICoverageCostSession session, CoverageCostClock clock,
        CoverageCostFrameSample[] frames, int warmed, int errors, CoverageCostRunStatus status, string reason)
    {
        double ms = 1000.0 / clock.Frequency;
        bool coverage = manifest.Coverage;
        var spans = new List<CoverageCostSpan>();
        void Add(string name, Func<CoverageCostFrameSample, double> value, string includes, bool applies = true, bool derived = false,
            bool observable = true)
        {
            if (!applies || !observable)
            {
                spans.Add(new(name, applies ? CoverageCostStatus.Unavailable : CoverageCostStatus.NotApplicable, 0, null, null, null,
                    null, null, includes));
                return;
            }
            var values = new double[frames.Length];
            for (int i = 0; i < frames.Length; i++) values[i] = value(frames[i]);
            spans.Add(Summarize(name, derived ? CoverageCostStatus.Derived : CoverageCostStatus.Measured, values, includes));
        }
        Add("prepare.total", f => f.PrepareTicks * ms, coverage
            ? "public HostCoverageBinding.TryPrepare per actor: equipment staging, skinning, geometry evaluation, interpolation and reductions"
            : "uncovered equipment draw writing per actor, the off-route counterpart of public preparation");
        Add("prepare.skinning", f => f.SkinningTicks * ms, "covered source vertex skinning inside staging", coverage);
        Add("prepare.geometryEvaluation", f => f.GeometryTicks * ms,
            "CoverageEvaluator.TryEvaluateGeometry: construction, interpolation, CoverageCertification.Certify and normals", coverage);
        Add("prepare.other", f => (f.PrepareTicks - f.SkinningTicks - f.GeometryTicks) * ms,
            "prepare.total less skinning and geometry evaluation", coverage, true);
        Add("submission", f => f.SubmissionTicks * ms, "queueing host, equipment and outline draws");
        Add("reserve", f => f.ReserveTicks * ms, "Scene3D.PrepareFrame resource reservation");
        Add("encode.total", f => f.EncodeTicks * ms, session.NativeDevice
            ? "Scene3D.RenderInternal CPU recording, with Scene3D.EnableTiming on"
            : "Scene3D.RenderInternal into a null command list on the recording device, not a native backend encode");
        Add("encode.resolution", f => f.ResolutionTicks * ms, "PrepareCoverageRendering: pass capture, spheres and render certification");
        Add("encode.renderCertification", f => f.CertificationTicks * ms, "CoverageCertification.Validate per complete preparation", coverage);
        Add("encode.resolutionOther", f => (f.ResolutionTicks - f.CertificationTicks) * ms, "resolution less render certification",
            coverage, true);
        Add("encode.surfaceAppend", f => f.SurfaceTicks * ms, "AppendCoverageSurfaces");
        Add("encode.other", f => (f.EncodeTicks - f.ResolutionTicks - f.SurfaceTicks) * ms,
            "encode.total less resolution and surface append, including ordinary CPU skinning on the CPU route", derived: true);
        Add("encode.pass.shadowDepth", f => f.PassTimings.ShadowDepthMs, "Scene3DPassTimingsMs CPU encode, not GPU time");
        Add("encode.pass.model", f => f.PassTimings.ModelMs, "Scene3DPassTimingsMs CPU encode, not GPU time");
        Add("encode.pass.transparents", f => f.PassTimings.TransparentsMs, "Scene3DPassTimingsMs CPU encode, not GPU time");
        Add("encode.pass.post", f => f.PassTimings.PostMs, "Scene3DPassTimingsMs CPU encode, not GPU time");
        Add("submitWait.wall", f => f.SubmitWaitTicks * ms,
            "completed IGpuDevice.Submit plus WaitForIdle wall time, including submission and CPU wait, not isolated GPU execution",
            observable: session.NativeDevice);
        Add("frame.wall", f => f.FrameTicks * ms, "whole session frame wall time, Begin through Present");

        var metrics = new List<CoverageCostMetric>();
        void Count(string name, Func<CoverageCostFrameSample, int> value, string provenance)
        {
            long total = 0;
            foreach (var f in frames) total += value(f);
            metrics.Add(CoverageCostMetric.Measured(name, "count", total, provenance));
        }
        void Mean(string name, string unit, Func<CoverageCostFrameSample, double> value, string provenance, bool estimated = false)
        {
            if (frames.Length == 0)
            {
                metrics.Add(CoverageCostMetric.Missing(name, unit, CoverageCostStatus.Unavailable, "no measured frame"));
                return;
            }
            double total = 0;
            foreach (var f in frames) total += value(f);
            double mean = total / frames.Length;
            metrics.Add(estimated ? CoverageCostMetric.Estimated(name, unit, mean, provenance) : CoverageCostMetric.Measured(name, unit, mean, provenance));
        }
        metrics.Add(CoverageCostMetric.Missing("gpu.nativeExecution", "ms/frame", CoverageCostStatus.Unavailable,
            "KhaozEngine.Gpu has no timestamp-query API, so isolated GPU execution is not observable"));
        metrics.Add(CoverageCostMetric.Missing("baseline.skinning", "ms/frame", CoverageCostStatus.Unavailable,
            "ordinary skinning runs inside encode on the CPU route or on the GPU route and is not observed separately"));
        Count("outcome.actorFrames", f => f.ActorFrames, "every submitted actor frame, the denominator for all outcomes");
        Count("outcome.accepted", f => f.Accepted, "public preparation accepted");
        Count("outcome.declined", f => f.Declined, "public preparation refused, drawn on the uncovered current pose");
        Count("outcome.covered", f => f.Covered, "render certification presented the covered surface");
        Count("outcome.fallback", f => f.Fallback, "render resolution selected the uncovered fallback");
        metrics.Add(CoverageCostMetric.Measured("outcome.errors", "count", errors, "exceptions that ended the run"));
        Count("stages.skinned", f => f.Skinned, "stage observer skinning brackets");
        Count("stages.evaluated", f => f.Evaluated, "stage observer geometry evaluation brackets");
        Count("stages.certified", f => f.Certified, "stage observer render certification brackets");
        Count("evidence.passes", f => f.Passes, "captured coverage render passes over all actors");
        Count("evidence.pointFacePasses", f => f.PointFacePasses, "captured passes using a point shadow face matrix");
        Count("evidence.mainVisible", f => f.MainVisible, "covered preparations visible in the main view");
        Count("evidence.outlineGroups", f => f.OutlineGroups, "target outline groups rendered");
        Mean("frame.drawCalls", "count/frame", f => f.Stats.DrawCalls, "RenderFrameStats.DrawCalls");
        Mean("frame.trianglesEstimated", "count/frame", f => f.Stats.Triangles, "RenderFrameStats.Triangles, an engine estimate", true);
        Mean("upload.instance.bytesPerFrame", "bytes/frame", f => f.Stats.InstanceUploadBytes, "RenderFrameStats.InstanceUploadBytes");
        Mean("upload.skinned.bytesPerFrame", "bytes/frame", f => f.Stats.SkinnedUploadBytes, "RenderFrameStats.SkinnedUploadBytes");
        Mean("upload.skinnedUniform.bytesPerFrame", "bytes/frame", f => f.Stats.SkinnedUniformUploadBytes, "RenderFrameStats.SkinnedUniformUploadBytes");
        Mean("upload.partitioned.bytesPerFrame", "bytes/frame", f => f.Stats.UploadBytesPartitioned, "RenderFrameStats.UploadBytesPartitioned");
        Mean("alloc.prepareSubmit.bytesPerFrame", "bytes/frame", f => f.PrepareBytes, "GC.GetAllocatedBytesForCurrentThread around preparation and submission");
        Mean("alloc.reserve.bytesPerFrame", "bytes/frame", f => f.ReserveBytes, "GC.GetAllocatedBytesForCurrentThread around PrepareFrame");
        Mean("alloc.resolution.bytesPerFrame", "bytes/frame", f => f.ResolutionBytes, "GC.GetAllocatedBytesForCurrentThread around coverage resolution");
        Mean("alloc.surface.bytesPerFrame", "bytes/frame", f => f.SurfaceBytes, "GC.GetAllocatedBytesForCurrentThread around surface append");
        Mean("alloc.frame.bytesPerFrame", "bytes/frame", f => f.FrameBytes,
            "GC.GetAllocatedBytesForCurrentThread over the whole frame, including backend submission and harness bookkeeping");

        var report = new CoverageCostReport(status, reason, manifest, manifest.Sha256, session.Synthetic, CoverageCostEnvironment.Read(),
            session.Info, warmed, frames.Length, spans, metrics, session.ReadCapacities(), session.ReadRefusals(), []);
        return report with { Budgets = CoverageCostBudgets.ForReport(report) };
    }

    static CoverageCostSpan Summarize(string name, CoverageCostStatus status, double[] values, string includes)
    {
        if (values.Length == 0) return new(name, CoverageCostStatus.Unavailable, 0, null, null, null, null, null, includes);
        Array.Sort(values);
        double Rank(double q) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(q * values.Length) - 1)];
        return new(name, status, values.Length, values.Average(), values[0], Rank(.5), Rank(.95), values[^1], includes);
    }
}

/// <summary>Identity of the process that produced a report. Values that cannot be read stay "unavailable".</summary>
internal static class CoverageCostEnvironment
{
    const string Unavailable = "unavailable";

    internal static IReadOnlyList<KeyValuePair<string, string>> Read() =>
    [
        new("commit", Env("GITHUB_SHA") ?? Commit()),
        new("runtime", RuntimeInformation.FrameworkDescription),
        new("os", RuntimeInformation.OSDescription),
        new("architecture", RuntimeInformation.ProcessArchitecture.ToString()),
        new("cpu", Cpu()),
        new("processorCount", Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("runner", string.Join(' ', new[] { Env("RUNNER_NAME"), Env("RUNNER_OS"), Env("ImageOS"), Env("ImageVersion") }
            .Where(v => v is not null)) is { Length: > 0 } runner ? runner : Unavailable),
        new("gc", $"server={GCSettings.IsServerGC} latency={GCSettings.LatencyMode}"),
        new("tieredCompilation", Env("DOTNET_TieredCompilation") ?? "default"),
        new("tieredPgo", Env("DOTNET_TieredPGO") ?? "default"),
        new("stopwatchFrequency", System.Diagnostics.Stopwatch.Frequency.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("stopwatchHighResolution", System.Diagnostics.Stopwatch.IsHighResolution.ToString()),
    ];

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    static string Commit()
    {
        string? version = typeof(CoverageCostEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = version?.IndexOf('+') ?? -1;
        return plus >= 0 ? version![(plus + 1)..] : Unavailable;
    }

    static string Cpu()
    {
        if (Env("PROCESSOR_IDENTIFIER") is { } windows) return windows;
        try
        {
            if (OperatingSystem.IsLinux())
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal)) return line[(line.IndexOf(':') + 1)..].Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Unavailable;
    }
}
