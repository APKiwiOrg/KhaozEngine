using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Runs warm-up and measured frames of an authorized case through a session, enforcing the manifest's limits, and
/// materializes the report after the window. Parsing and authorization happen before the session factory runs, so a
/// refused manifest never opens a device or starts a frame. The first factory or frame failure is the report's
/// reason. Diagnostic reads and cleanup run defensively afterwards. Their failures are appended, never substituted,
/// and on an otherwise complete run they fail the report.
/// </summary>
internal static class CoverageCostDriver
{
    internal static CoverageCostReport Run(ReadOnlySpan<byte> manifestJson, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null)
    {
        CoverageCostManifest manifest;
        try { manifest = CoverageCostManifest.Parse(manifestJson); }
        catch (InvalidDataException ex)
        {
            return Empty(CoverageCostRunStatus.Refused, ex.Message, null, Sha(manifestJson), environment);
        }
        return Run(manifest, factory, clock, environment);
    }

    internal static CoverageCostReport Run(CoverageCostManifest manifest, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null, string? unit = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.AuthorizationIdentity))
            return Empty(CoverageCostRunStatus.Refused, "Manifest authorization identity is missing.", manifest, manifest.Sha256, environment, unit);
        // Every collector exists before the first frame.
        var samples = new CoverageCostFrameSample[manifest.MeasuredFrames];
        var warm = new CoverageCostFrameSample[manifest.WarmupFrames];
        long frameLimit = (long)Math.Ceiling(manifest.MaxFrameMilliseconds * clock.Frequency / 1000);
        long wallLimit = (long)Math.Ceiling(manifest.MaxWallSeconds * clock.Frequency);
        ICoverageCostSession session;
        try { session = factory(manifest, clock); }
        catch (CoverageCostRefusedException ex)
        {
            return Empty(CoverageCostRunStatus.Refused, ex.Message, manifest, manifest.Sha256, environment, unit);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return Empty(CoverageCostRunStatus.Failed, $"Session factory failed: {Describe(ex)}", manifest, manifest.Sha256, environment, unit);
        }

        string reason = "", phase = "warm-up";
        int measured = 0, warmed = 0, errors = 0, index = 0;
        long start = clock.Now(), elapsed = 0;
        try
        {
            for (index = 0; index < warm.Length && reason.Length == 0; index++)
            {
                session.Frame(false, ref warm[index]);
                warmed++;
                elapsed = clock.Now() - start;
                reason = Limits(phase, index, warm[index].FrameTicks, frameLimit, elapsed, wallLimit, manifest, clock);
            }
            if (reason.Length == 0)
            {
                phase = "measured";
                for (index = 0; index < samples.Length && reason.Length == 0; index++)
                {
                    session.Frame(true, ref samples[index]);
                    measured++;
                    elapsed = clock.Now() - start;
                    reason = Limits(phase, index, samples[index].FrameTicks, frameLimit, elapsed, wallLimit, manifest, clock);
                }
            }
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            errors = 1;
            reason = $"{phase} frame {index} failed: {Describe(ex)}";
        }

        var secondary = new List<string>();
        var report = Materialize(manifest, session, clock, samples[..measured], warm[..warmed], errors, elapsed, secondary, environment, unit);
        try { session.Dispose(); }
        catch (Exception ex) when (Recoverable(ex)) { secondary.Add($"cleanup failed: {Describe(ex)}"); }
        string extra = string.Join(" ", secondary.Select(s => s.EndsWith('.') ? s : s + "."));
        string final = reason.Length == 0 ? extra : extra.Length == 0 ? reason : $"{reason} Secondary: {extra}";
        report = report with
        {
            Status = reason.Length == 0 && secondary.Count == 0 ? CoverageCostRunStatus.Completed : CoverageCostRunStatus.Failed,
            Reason = final,
        };
        return report with { Budgets = CoverageCostBudgets.ForReport(report) };
    }

    internal static CoverageCostPairReport RunPair(ReadOnlySpan<byte> pairJson, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null)
    {
        CoverageCostPairManifest pair;
        try { pair = CoverageCostPairManifest.Parse(pairJson); }
        catch (InvalidDataException ex) { return CoverageCostPairReport.Refused(ex.Message, Sha(pairJson)); }
        return RunPair(pair, factory, clock, environment);
    }

    /// <summary>Both conditions in their authorized order, sequentially in this process with the same factory, clock
    /// and matched frame counts. A condition that does not complete leaves the next one unrun.</summary>
    internal static CoverageCostPairReport RunPair(CoverageCostPairManifest pair, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null)
    {
        var reports = new CoverageCostReport?[pair.Conditions.Length];
        string reason = "";
        for (int i = 0; i < reports.Length && reason.Length == 0; i++)
        {
            var report = Run(pair.Conditions[i], factory, clock, environment, pair.Unit);
            reports[i] = report;
            if (report.Status != CoverageCostRunStatus.Completed)
                reason = $"Condition {pair.Order[i]} is {report.Status.ToString().ToLowerInvariant()}: {report.Reason}"
                    + (i + 1 < reports.Length ? $" Condition {pair.Order[i + 1]} was not run." : "");
        }
        return new(reason.Length == 0 ? CoverageCostRunStatus.Completed : CoverageCostRunStatus.Failed, reason, pair.Unit, pair.Sha256,
            pair.Order, reports, CoverageCostBudgets.ForPair(pair, reports), CoverageCostBudgets.Deltas(pair, reports));
    }

    static bool Recoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
    static string Sha(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? "" : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    static string Limits(string phase, int index, long frameTicks, long frameLimit, long elapsed, long wallLimit,
        CoverageCostManifest manifest, CoverageCostClock clock) =>
        frameTicks > frameLimit
            ? $"{phase} frame {index} took {Number(frameTicks * 1000.0 / clock.Frequency)} ms, above limits.maxFrameMilliseconds {Number(manifest.MaxFrameMilliseconds)} ms."
        : elapsed > wallLimit
            ? $"The run elapsed {Number((double)elapsed / clock.Frequency)} s after {phase} frame {index}, above limits.maxWallSeconds {Number(manifest.MaxWallSeconds)} s."
        : "";

    static CoverageCostReport Empty(CoverageCostRunStatus status, string reason, CoverageCostManifest? manifest, string sha,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment, string? unit = null)
    {
        var report = new CoverageCostReport(status, reason, manifest, sha, true, (environment ?? CoverageCostEnvironment.Read)(), null, 0, 0,
            [], [], [], [], [], [], unit);
        return report with { Budgets = CoverageCostBudgets.ForReport(report) };
    }

    static CoverageCostReport Materialize(CoverageCostManifest manifest, ICoverageCostSession session, CoverageCostClock clock,
        CoverageCostFrameSample[] frames, CoverageCostFrameSample[] warm, int errors, long elapsed, List<string> secondary,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment, string? unit)
    {
        double ms = 1000.0 / clock.Frequency;
        bool coverage = manifest.Coverage, ordinary = session.ObservesOrdinarySkinning;
        var spans = new List<CoverageCostSpan>();
        void Add(string name, Func<CoverageCostFrameSample, double> value, string includes, bool applies = true, bool derived = false,
            bool observable = true, CoverageCostFrameSample[]? source = null)
        {
            if (!applies || !observable)
            {
                spans.Add(new(name, applies ? CoverageCostStatus.Unavailable : CoverageCostStatus.NotApplicable, 0, null, null, null,
                    null, null, includes));
                return;
            }
            source ??= frames;
            var values = new double[source.Length];
            for (int i = 0; i < source.Length; i++) values[i] = value(source[i]);
            spans.Add(Summarize(name, derived ? CoverageCostStatus.Derived : CoverageCostStatus.Measured, values, includes));
        }
        Add("warmup.frame.wall", f => f.FrameTicks * ms, "completed warm-up frame wall times, kept as sizing evidence", source: warm);
        Add("prepare.total", f => f.PrepareTicks * ms, coverage
            ? "public HostCoverageBinding.TryPrepare per actor: equipment staging, skinning, geometry evaluation, interpolation and reductions"
            : "uncovered equipment draw writing per actor, the off-route counterpart of public preparation");
        Add("prepare.skinning", f => f.SkinningTicks * ms, "covered source vertex skinning inside staging", coverage);
        Add("prepare.geometryEvaluation", f => f.GeometryTicks * ms,
            "CoverageEvaluator.TryEvaluateGeometry, containing prepare.geometryCertification", coverage);
        Add("prepare.geometryCertification", f => f.GeometryCertificationTicks * ms, "CoverageCertification.Certify inside the evaluator", coverage);
        Add("prepare.geometryConstruction", f => (f.GeometryTicks - f.GeometryCertificationTicks) * ms,
            "geometry evaluation less geometry certification: frames, interpolation, construction, core correction and normals", coverage, true);
        Add("prepare.other", f => (f.PrepareTicks - f.SkinningTicks - f.GeometryTicks) * ms,
            "prepare.total less skinning and geometry evaluation", coverage, true);
        Add("submission", f => f.SubmissionTicks * ms, "queueing host, equipment and outline draws, with coverage draw validation");
        Add("reserve", f => f.ReserveTicks * ms, "Scene3D.PrepareFrame resource reservation");
        Add("encode.total", f => f.EncodeTicks * ms, session.NativeDevice
            ? "Scene3D.RenderInternal CPU recording, with Scene3D.EnableTiming on"
            : "Scene3D.RenderInternal into a null command list on the recording device, not a native backend encode");
        Add("encode.resolution", f => f.ResolutionTicks * ms, "PrepareCoverageRendering: pass capture, spheres and render certification");
        Add("encode.renderCertification", f => f.CertificationTicks * ms, "CoverageCertification.Validate per complete preparation", coverage);
        Add("encode.resolutionOther", f => (f.ResolutionTicks - f.CertificationTicks) * ms, "resolution less render certification",
            coverage, true);
        Add("encode.surfaceAppend", f => f.SurfaceTicks * ms, "AppendCoverageSurfaces");
        Add("encode.ordinarySkinning", f => f.OrdinarySkinningTicks * ms,
            "the uncovered skinned draw loop: classification and CPU vertex skinning on the CPU route, draw records on the GPU route",
            observable: ordinary);
        Add("encode.skinnedUpload", f => f.SkinnedUploadTicks * ms, "PrepareGpuSkinnedFrame and UploadCpuSkinnedFrame, shared by both routes",
            observable: ordinary);
        Add("encode.other", f => (f.EncodeTicks - f.ResolutionTicks - f.SurfaceTicks - (ordinary ? f.OrdinarySkinningTicks + f.SkinnedUploadTicks : 0)) * ms,
            ordinary ? "encode.total less resolution, surface append, ordinary skinning and skinned upload"
                : "encode.total less resolution and surface append, including unobserved ordinary skinning and upload", derived: true);
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
        metrics.Add(new("limit.maxFrameMilliseconds", "ms", CoverageCostStatus.Derived, manifest.MaxFrameMilliseconds, "configured manifest limit"));
        metrics.Add(new("limit.maxWallSeconds", "s", CoverageCostStatus.Derived, manifest.MaxWallSeconds, "configured manifest limit"));
        metrics.Add(CoverageCostMetric.Measured("run.elapsedMilliseconds", "ms", elapsed * ms, "driver clock from the first frame to the last completed frame"));
        if (ordinary)
            Count("ordinary.cpuSkinnedVertices", f => f.OrdinaryCpuSkinnedVertices,
                "uncovered source vertices skinned on the CPU route, zero on the GPU route is a measured absence");
        else metrics.Add(CoverageCostMetric.Missing("ordinary.cpuSkinnedVertices", "count", CoverageCostStatus.Unavailable,
            "the session does not observe the ordinary skinned route"));
        Count("outcome.actorFrames", f => f.ActorFrames, "every submitted actor frame, the denominator for all outcomes");
        Count("outcome.accepted", f => f.Accepted, "public preparation accepted");
        Count("outcome.declined", f => f.Declined, "public preparation refused, drawn on the uncovered current pose");
        Count("outcome.covered", f => f.Covered, "render certification presented the covered surface");
        Count("outcome.fallback", f => f.Fallback, "render resolution selected the uncovered fallback");
        metrics.Add(CoverageCostMetric.Measured("outcome.errors", "count", errors, "exceptions that ended the run"));
        Count("stages.skinned", f => f.Skinned, "stage observer skinning brackets");
        Count("stages.evaluated", f => f.Evaluated, "stage observer geometry evaluation brackets");
        Count("stages.geometryCertified", f => f.GeometryCertified, "stage observer geometry certification brackets");
        Count("stages.certified", f => f.Certified, "stage observer render certification brackets");
        Count("evidence.passes", f => f.Passes, "captured coverage certification contexts over all actors");
        Count("evidence.mainVisible", f => f.MainVisible, "covered preparations visible in the main view");
        Count("evidence.outlineGroups", f => f.OutlineGroups, "target outline groups rendered");
        Mean("frame.drawCalls", "count/frame", f => f.Stats.DrawCalls, session.NativeDevice
            ? "RenderFrameStats.DrawCalls" : "RenderFrameStats.DrawCalls recorded into a null command list, not executed on a GPU");
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

        T Guard<T>(Func<T> read, string what, Func<string, T> unavailable)
        {
            try { return read(); }
            catch (Exception ex) when (Recoverable(ex))
            {
                secondary.Add($"diagnostics failed: {what}: {Describe(ex)}");
                return unavailable(Describe(ex));
            }
        }
        var info = Guard<CoverageCostSessionInfo?>(() => session.Info, "session info", _ => null);
        var capacities = Guard(session.ReadCapacities, "capacities",
            m => [CoverageCostMetric.Missing("diagnostics.capacities", "n/a", CoverageCostStatus.Unavailable, m)]);
        var refusals = Guard(session.ReadRefusals, "refusals", m =>
        {
            metrics.Add(CoverageCostMetric.Missing("diagnostics.refusals", "n/a", CoverageCostStatus.Unavailable, m));
            return (IReadOnlyList<KeyValuePair<string, int>>)[];
        });
        var evidence = Guard(session.ReadEvidence, "evidence",
            m => [CoverageCostMetric.Missing("diagnostics.evidence", "n/a", CoverageCostStatus.Unavailable, m)]);
        return new CoverageCostReport(CoverageCostRunStatus.Completed, "", manifest, manifest.Sha256, session.Synthetic,
            (environment ?? CoverageCostEnvironment.Read)(), info, warm.Length, frames.Length, spans, metrics, capacities, refusals, evidence,
            [], unit);
    }

    static CoverageCostSpan Summarize(string name, CoverageCostStatus status, double[] values, string includes)
    {
        if (values.Length == 0) return new(name, CoverageCostStatus.Unavailable, 0, null, null, null, null, null, includes);
        Array.Sort(values);
        double Rank(double q) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(q * values.Length) - 1)];
        return new(name, status, values.Length, values.Average(), values[0], Rank(.5), Rank(.95), values[^1], includes);
    }
}
