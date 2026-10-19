using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// One cooperative deadline for a whole selected unit, a single case or both conditions of a pair. It starts before the
/// first factory and is checked at phase boundaries: after session setup, after every frame, after diagnostics, after
/// cleanup and after each condition's publication. The clock ends once the last condition report has been serialized and flushed by its sink.
/// Building the final unit verdict, serializing it and writing the -unit JSON are outside the deadline. Nothing is
/// interrupted, so a slow or hung call overshoots until it returns or the hosted job timeout ends the process.
/// </summary>
internal sealed class CoverageCostDeadline
{
    readonly CoverageCostClock clock;
    readonly long start, limit;

    internal CoverageCostDeadline(CoverageCostClock clock, double maxWallSeconds)
    {
        this.clock = clock;
        Seconds = maxWallSeconds;
        limit = (long)Math.Ceiling(maxWallSeconds * clock.Frequency);
        start = clock.Now();
    }

    internal double Seconds { get; }
    internal long Elapsed => clock.Now() - start;

    /// <summary>Empty while time remains, otherwise the reason naming the boundary that found it spent.</summary>
    internal string Check(string boundary) => Check(boundary, Elapsed);

    /// <summary>The same check against an elapsed value already read at this boundary.</summary>
    internal string Check(string boundary, long elapsed)
    {
        return elapsed > limit
            ? $"The unit elapsed {Number((double)elapsed / clock.Frequency)} s {boundary}, above limits.maxWallSeconds {Number(Seconds)} s."
            : "";
    }

    internal static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Runs warm-up and measured frames of an authorized case through a session, enforcing the manifest's limits, and
/// materializes the report after the window. Parsing and authorization happen before the session factory runs, so a
/// refused manifest never opens a device or starts a frame. The first factory or frame failure is the report's
/// reason. Diagnostic reads, cleanup and publication run defensively afterwards. Their failures are appended, never
/// substituted, and on an otherwise complete run they fail the report. A finished report is published through the
/// caller's sink before anything else starts.
/// </summary>
internal static class CoverageCostDriver
{
    internal static CoverageCostReport Run(ReadOnlySpan<byte> manifestJson, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null, Action<CoverageCostReport>? publish = null)
    {
        CoverageCostManifest manifest;
        try { manifest = CoverageCostManifest.Parse(manifestJson); }
        catch (InvalidDataException ex)
        {
            return Publish(Empty(CoverageCostRunStatus.Refused, ex.Message, null, Sha(manifestJson), environment), publish);
        }
        return Run(manifest, factory, clock, environment, publish: publish);
    }

    /// <summary>One condition. A single case starts its own unit deadline here, before the factory. A pair passes its
    /// shared deadline.</summary>
    internal static CoverageCostReport Run(CoverageCostManifest manifest, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null, string? unit = null, CoverageCostDeadline? deadline = null,
        Action<CoverageCostReport>? publish = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.AuthorizationIdentity))
            return Publish(Empty(CoverageCostRunStatus.Refused, "Manifest authorization identity is missing.", manifest, manifest.Sha256,
                environment, unit), publish);
        // Every collector exists before the first frame.
        var samples = new CoverageCostFrameSample[manifest.MeasuredFrames];
        var warm = new CoverageCostFrameSample[manifest.WarmupFrames];
        long frameLimit = (long)Math.Ceiling(manifest.MaxFrameMilliseconds * clock.Frequency / 1000);
        bool ownsDeadline = deadline is null;
        deadline ??= new CoverageCostDeadline(clock, manifest.MaxWallSeconds);
        ICoverageCostSession session;
        try { session = factory(manifest, clock); }
        catch (CoverageCostRefusedException ex)
        {
            return Publish(Empty(CoverageCostRunStatus.Refused, ex.Message, manifest, manifest.Sha256, environment, unit), publish);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return Publish(Empty(CoverageCostRunStatus.Failed, $"Session factory failed: {Describe(ex)}", manifest, manifest.Sha256,
                environment, unit), publish);
        }

        string reason = deadline.Check("after session setup"), phase = "warm-up";
        int measured = 0, warmed = 0, errors = 0, index = 0;
        long elapsed = 0;
        try
        {
            for (index = 0; index < warm.Length && reason.Length == 0; index++)
            {
                session.Frame(false, ref warm[index]);
                warmed++;
                elapsed = deadline.Elapsed;
                reason = Limits(phase, index, warm[index].FrameTicks, frameLimit, deadline, elapsed, manifest, clock);
            }
            if (reason.Length == 0)
            {
                phase = "measured";
                for (index = 0; index < samples.Length && reason.Length == 0; index++)
                {
                    session.Frame(true, ref samples[index]);
                    measured++;
                    elapsed = deadline.Elapsed;
                    reason = Limits(phase, index, samples[index].FrameTicks, frameLimit, deadline, elapsed, manifest, clock);
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
        string spent = deadline.Check("after diagnostics");
        if (reason.Length == 0) reason = spent;
        try { session.Dispose(); }
        catch (Exception ex) when (Recoverable(ex)) { secondary.Add($"cleanup failed: {Describe(ex)}"); }
        spent = deadline.Check("after cleanup");
        if (reason.Length == 0) reason = spent;
        report = Finish(report, reason, secondary, deadline, clock);
        report = Publish(report, publish);
        // A single case ends its unit here. The published copy stays as the sink saw it and the returned verdict carries
        // the overrun. A pair charges this publication in RunPair, after its last condition.
        if (ownsDeadline && publish is not null && report.Status == CoverageCostRunStatus.Completed
            && deadline.Check("after publication") is { Length: > 0 } late)
            report = Finish(report, late, [], deadline, clock);
        return report;
    }

    internal static CoverageCostPairReport RunPair(ReadOnlySpan<byte> pairJson, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null, Action<CoverageCostReport>? publish = null)
    {
        CoverageCostPairManifest pair;
        try { pair = CoverageCostPairManifest.Parse(pairJson); }
        catch (InvalidDataException ex) { return CoverageCostPairReport.Refused(ex.Message, Sha(pairJson)); }
        return RunPair(pair, factory, clock, environment, publish);
    }

    /// <summary>Both conditions in their authorized order, sequentially in this process with the same factory, clock and
    /// matched frame counts, under one unit deadline started before the first factory. Each finished condition is
    /// published before the next starts. A condition that does not complete, a failed publication or a spent deadline
    /// leaves the next condition unrun. The last condition's publication is charged too. The condition reports stay the
    /// exact snapshots the sink saw and the pair verdict carries any overrun.</summary>
    internal static CoverageCostPairReport RunPair(CoverageCostPairManifest pair, CoverageCostSessionFactory factory, CoverageCostClock clock,
        Func<IReadOnlyList<KeyValuePair<string, string>>>? environment = null, Action<CoverageCostReport>? publish = null)
    {
        var deadline = new CoverageCostDeadline(clock, pair.Conditions[0].MaxWallSeconds);
        var reports = new CoverageCostReport?[pair.Conditions.Length];
        string reason = "";
        for (int i = 0; i < reports.Length && reason.Length == 0; i++)
        {
            string spent = deadline.Check($"before condition {pair.Order[i]}");
            if (spent.Length > 0)
            {
                reason = $"{spent} Condition {pair.Order[i]} was not run.";
                break;
            }
            var report = Run(pair.Conditions[i], factory, clock, environment, pair.Unit, deadline, publish);
            reports[i] = report;
            if (report.Status != CoverageCostRunStatus.Completed)
                reason = $"Condition {pair.Order[i]} is {report.Status.ToString().ToLowerInvariant()}: {report.Reason}"
                    + (i + 1 < reports.Length ? $" Condition {pair.Order[i + 1]} was not run." : "");
        }
        if (reason.Length == 0) reason = deadline.Check($"after condition {pair.Order[^1]} publication");
        return new(reason.Length == 0 ? CoverageCostRunStatus.Completed : CoverageCostRunStatus.Failed, reason, pair.Unit, pair.Sha256,
            pair.Order, reports, CoverageCostBudgets.ForPair(pair, reports, reason), CoverageCostBudgets.Deltas(pair, reports));
    }

    static CoverageCostReport Finish(CoverageCostReport report, string reason, List<string> secondary, CoverageCostDeadline? deadline,
        CoverageCostClock clock)
    {
        string extra = string.Join(" ", secondary.Select(s => s.EndsWith('.') ? s : s + "."));
        string final = reason.Length == 0 ? extra : extra.Length == 0 ? reason : $"{reason} Secondary: {extra}";
        var metrics = report.Metrics.Where(m => m.Name != "unit.elapsedMilliseconds").ToList();
        if (deadline is not null)
            metrics.Add(CoverageCostMetric.Measured("unit.elapsedMilliseconds", "ms", deadline.Elapsed * 1000.0 / clock.Frequency,
                "unit deadline clock from before the first factory to this report"));
        report = report with
        {
            Status = reason.Length == 0 && secondary.Count == 0 ? CoverageCostRunStatus.Completed : CoverageCostRunStatus.Failed,
            Reason = final,
            Metrics = metrics,
        };
        return report with { Budgets = CoverageCostBudgets.ForReport(report) };
    }

    // Publishes once. A sink failure fails the report and keeps the first reason, and the caller stops the unit.
    static CoverageCostReport Publish(CoverageCostReport report, Action<CoverageCostReport>? publish)
    {
        if (publish is null) return report;
        try { publish(report); }
        catch (Exception ex) when (Recoverable(ex))
        {
            string failure = $"publication failed: {Describe(ex)}.";
            report = report with
            {
                Status = report.Status == CoverageCostRunStatus.Refused ? CoverageCostRunStatus.Refused : CoverageCostRunStatus.Failed,
                Reason = report.Reason.Length == 0 ? failure : $"{report.Reason} Secondary: {failure}",
            };
            return report with { Budgets = CoverageCostBudgets.ForReport(report) };
        }
        return report;
    }

    static bool Recoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
    static string Sha(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? "" : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    static string Number(double value) => CoverageCostDeadline.Number(value);

    static string Limits(string phase, int index, long frameTicks, long frameLimit, CoverageCostDeadline deadline, long elapsed,
        CoverageCostManifest manifest, CoverageCostClock clock) =>
        frameTicks > frameLimit
            ? $"{phase} frame {index} took {Number(frameTicks * 1000.0 / clock.Frequency)} ms, above limits.maxFrameMilliseconds {Number(manifest.MaxFrameMilliseconds)} ms."
            : deadline.Check($"after {phase} frame {index}", elapsed);

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
        bool coverage = manifest.Coverage, ordinary = session.ObservesSkinnedPreparation;
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
            "staged candidate construction: frames, interpolation, construction, core correction and normals, with no "
            + "certification, which runs once in encode.renderCertification", coverage);
        Add("prepare.other", f => (f.PrepareTicks - f.SkinningTicks - f.GeometryTicks) * ms,
            "prepare.total less skinning and geometry evaluation", coverage, true);
        Add("submission", f => f.SubmissionTicks * ms, "queueing host, equipment and outline draws, with coverage draw validation");
        Add("reserve", f => f.ReserveTicks * ms, "Scene3D.PrepareFrame resource reservation");
        Add("encode.total", f => f.EncodeTicks * ms, session.NativeDevice
            ? "Scene3D.RenderInternal CPU recording, with Scene3D.EnableTiming on"
            : "Scene3D.RenderInternal into a null command list on the recording device, not a native backend encode");
        Add("encode.resolution", f => f.ResolutionTicks * ms, "Historical CPU resolution accounting");
        Add("encode.renderCertification", f => f.CertificationTicks * ms, "CoverageCertification.Validate per complete preparation", coverage);
        Add("encode.resolutionOther", f => (f.ResolutionTicks - f.CertificationTicks) * ms, "resolution less render certification",
            coverage, true);
        Add("encode.skinnedPreparation", f => f.SkinnedPreparationTicks * ms,
            "the whole PrepareSkinnedDraws: surface append, ordinary skinning, coverage motion recording and skinned upload",
            observable: ordinary);
        Add("encode.surfaceAppend", f => f.SurfaceTicks * ms, "AppendCoverageSurfaces, a child of encode.skinnedPreparation, informational");
        Add("encode.ordinarySkinning", f => f.OrdinarySkinningTicks * ms,
            "the uncovered skinned draw loop, CPU vertex skinning on the CPU route or draw records on the GPU route, a child of encode.skinnedPreparation, informational",
            observable: ordinary);
        Add("encode.skinnedUpload", f => f.SkinnedUploadTicks * ms,
            "PrepareGpuSkinnedFrame and UploadCpuSkinnedFrame, a child of encode.skinnedPreparation, informational", observable: ordinary);
        Add("encode.skinnedPreparationOther", f => (f.SkinnedPreparationTicks - f.SurfaceTicks - f.OrdinarySkinningTicks - f.SkinnedUploadTicks) * ms,
            "encode.skinnedPreparation less its three timed children: stream setup and coverage motion recording", derived: true, observable: ordinary);
        Add("encode.motionCommit", f => f.MotionCommitTicks * ms, "the whole CompleteCoverageEncoding at the end of the render",
            observable: ordinary);
        Add("encode.other", f => (f.EncodeTicks - f.ResolutionTicks - (ordinary ? f.SkinnedPreparationTicks + f.MotionCommitTicks : f.SurfaceTicks)) * ms,
            ordinary ? "encode.total less resolution, the skinned preparation and the motion commit"
                : "encode.total less resolution and surface append, including the unobserved skinned preparation and commit", derived: true);
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
        metrics.Add(CoverageCostMetric.Measured("run.elapsedMilliseconds", "ms", elapsed * ms, "unit deadline clock at the last completed frame"));
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
