using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Evidence checks are independent of arithmetic and run only after measurement.</summary>
internal static class CoveragePerfValidity
{
    internal static ImmutableArray<string> Reasons(CoveragePerfReport report)
    {
        var reasons = new List<string>();
        void Refuse(string text) { if (!reasons.Contains(text)) reasons.Add(text); }
        if (!report.ReturnedByRun || !report.FinalUnitPublished) Refuse("Missing final unit publication returned by Run.");
        if (report.Status != CoveragePerfRunStatus.Completed || !report.Errors.IsEmpty)
            Refuse("Partial publication or failed unit: " + report.Reason + " " + string.Join(" | ", report.Errors));
        var manifest = report.Manifest;
        if (manifest is null) { Refuse("Missing manifest identity."); return reasons.ToImmutableArray(); }
        try { manifest.ValidateAuthorization(report.Identity); }
        catch (Exception error) { Refuse("Manifest/workload identity refusal: " + error.Message); }
        if (report.ManifestSha256 != manifest.Sha256 || manifest.Sha256 !=
            Convert.ToHexStringLower(SHA256.HashData(manifest.Utf8.AsSpan()))) Refuse("Manifest bytes hash identity mismatch.");
        var execution = report.Identity.Execution;
        if (execution.ClockFrequency <= 0 || new[] { execution.HarnessAssembly, execution.Runtime, execution.GcSettings,
            execution.TieringSettings, execution.HostCpu, execution.HostIdentity, execution.RunnerImage,
            execution.BatchCompletionPolicy, execution.TimestampCompletionPolicy, execution.ProductionCompletionPolicy }.Any(Unknown))
            Refuse("Incomplete runtime, host, clock or completion policy identity.");
        if (execution.LegacyBaselineSha != CoveragePerfLegacyHost.BaselineSha ||
            execution.LegacySequenceFingerprint != CoveragePerfLegacyHost.SequenceFingerprint)
            Refuse("Legacy baseline SHA or semantic sequence fingerprint mismatch.");
        if (report.Blocks.Length != manifest.Blocks.Length) Refuse("Missing final unit block or partial publication.");
        for (int i = 0; i < report.Blocks.Length; i++)
        {
            var result = report.Blocks[i];
            string prefix = $"Block {result.Block.Index}: ";
            void Invalid(string text) => Refuse(prefix + text);
            if (i >= manifest.Blocks.Length || result.Block != manifest.Blocks[i]) Invalid("configuration or block sequence differs from the manifest.");
            if (result.Status != CoveragePerfRunStatus.Completed || !result.Errors.IsEmpty)
                Invalid("partial publication or failed block: " + result.Reason + " " + string.Join(" | ", result.Errors));
            if (!SameRun(result.RunIdentity, report.Identity)) Invalid("pair identity belongs to a different process, machine, job or configuration.");
            if (result.Session is not { } info) { Invalid("missing device/session identity."); continue; }
            if (info.Synthetic) Invalid("synthetic session cannot establish native proof.");
            if (!info.NativeDevice) Invalid("device has no native execution evidence.");
            if (info.Backend != manifest.Backend) Invalid("device backend differs from the approved backend.");
            if (new[] { info.Adapter, info.Vendor, info.Device }.Any(Unknown)) Invalid("device identity is unresolved.");
            if (Unknown(info.Driver)) Invalid("driver identity is unresolved.");
            if (info.OrdinarySkinningRoute != manifest.OrdinarySkinning || info.CoveredSurfaceRoute != manifest.CoveredSurface)
                Invalid("effective route differs from the approved GPU route.");
            if (Unknown(info.FixtureFingerprint) || Unknown(info.ProfileFingerprint) || Unknown(info.CompletionPolicy))
                Invalid("fixture, configuration or completion policy identity is unresolved.");
            bool gpu = result.Block.TimerLayout != CoveragePerfTimerLayout.CpuOnly;
            if (result.Block.Condition == CoveragePerfCondition.DirectOff && gpu) Invalid("DirectOff GPU layout is unsupported.");
            if (gpu)
            {
                if (!CoveragePerfHardwareIdentity.IsT4(info))
                    Invalid("device is not the approved NVIDIA T4 hardware route.");
                if (!info.TimestampSupported) Invalid("unsupported GPU timestamp capability.");
                if (info.TimestampPeriodNanoseconds is not double period || !double.IsFinite(period) || period <= 0)
                    Invalid("timestamp period is unresolved or invalid.");
            }
            else if (!info.SoftwareAdapter || !(info.Adapter.Contains("lavapipe", StringComparison.OrdinalIgnoreCase) ||
                info.Adapter.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase))) Invalid("device is not the approved lavapipe CPU route.");
            if (manifest.Backend == KhaozEngine.Gpu.GpuBackendKind.Direct3D11Native && Unknown(info.D3DRecordingMode))
                Invalid("D3D recording mode configuration is unresolved.");
            if ((result.Samples.Length & 1) != 0 || (result.Block.MeasuredFrames & 1) != 0) Invalid("measured counts must be even.");
            if (result.Warmup.Length != result.Block.WarmupFrames || result.Samples.Length != result.Block.MeasuredFrames)
                Invalid("dropped frames or incomplete warm-up/measured counts.");
            if (result.Samples.Count(s => s.PoseId == 0) != result.Samples.Length / 2 ||
                result.Samples.Count(s => s.PoseId == 1) != result.Samples.Length / 2) Invalid("pose counts are not balanced.");
            CheckFrames(result.Warmup, 0, false, result, execution.ClockFrequency, Invalid);
            CheckFrames(result.Samples, result.Block.WarmupFrames, true, result, execution.ClockFrequency, Invalid);
        }
        // Diagnostic stages and shared-host controls must be comparable too. A bad earlier stage cannot
        // lend validity to a later completed-frame pair.
        var pairs = manifest.Preset switch
        {
            "C0" => new[] { (0, 1), (2, 3) },
            "C1" => new[] { (0, 1), (1, 2), (4, 3), (5, 4) },
            _ => new[] { (0, 1), (2, 3), (4, 5), (7, 6) },
        };
        foreach (var (offIndex, onIndex) in pairs)
            if (offIndex < report.Blocks.Length && onIndex < report.Blocks.Length)
                foreach (string reason in PairReasons(report.Blocks[offIndex], report.Blocks[onIndex])) Refuse(reason);
        return reasons.ToImmutableArray();
    }

    internal static ImmutableArray<string> PairReasons(CoveragePerfBlockResult off, CoveragePerfBlockResult on)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        void Refuse(string text) => reasons.Add($"Pair {off.Block.Index}/{on.Block.Index}: {text}");
        if (!SameRun(off.RunIdentity, on.RunIdentity)) Refuse("pair identity differs across machines or jobs.");
        if (off.Block.Actors != on.Block.Actors || off.Block.TimerLayout != on.Block.TimerLayout ||
            off.Block.Fixture != on.Block.Fixture || off.Block.WarmupFrames != on.Block.WarmupFrames ||
            off.Block.MeasuredFrames != on.Block.MeasuredFrames) Refuse("pair configuration differs.");
        if (off.Session is not { } a || on.Session is not { } b) Refuse("pair device identity is missing.");
        else
        {
            if (a.Backend != b.Backend || a.Adapter != b.Adapter || a.Vendor != b.Vendor || a.Device != b.Device ||
                a.HardwareIdentityBasis != b.HardwareIdentityBasis ||
                a.SoftwareAdapter != b.SoftwareAdapter) Refuse("device identity differs.");
            if (a.Driver != b.Driver) Refuse("driver identity differs.");
            if (a.FixtureFingerprint != b.FixtureFingerprint || a.ProfileFingerprint != b.ProfileFingerprint ||
                a.D3DRecordingMode != b.D3DRecordingMode || a.CompletionPolicy != b.CompletionPolicy ||
                a.TimestampSupported != b.TimestampSupported || a.TimestampPeriodNanoseconds != b.TimestampPeriodNanoseconds ||
                !a.Environment.SequenceEqual(b.Environment)) Refuse("pair configuration differs.");
            if (a.OrdinarySkinningRoute != b.OrdinarySkinningRoute || a.CoveredSurfaceRoute != b.CoveredSurfaceRoute)
                Refuse("pair route differs.");
        }
        Compare(off.Warmup, on.Warmup);
        Compare(off.Samples, on.Samples);
        return reasons.ToImmutable();

        void Compare(ImmutableArray<CoveragePerfFrameSample> left, ImmutableArray<CoveragePerfFrameSample> right)
        {
            if (left.Length != right.Length) { Refuse("dropped paired frames."); return; }
            for (int i = 0; i < left.Length; i++)
            {
                var x = left[i]; var y = right[i];
                if (x.FrameIndex != y.FrameIndex || x.PoseId != y.PoseId || x.PoseFingerprint != y.PoseFingerprint)
                { Refuse("pair identity or pose fingerprint differs."); break; }
                if (x.Passes != y.Passes || x.PointShadowFaces != y.PointShadowFaces || x.PassFingerprint != y.PassFingerprint)
                { Refuse("actual pass schedule differs."); break; }
            }
        }
    }

    static void CheckFrames(ImmutableArray<CoveragePerfFrameSample> frames, int offset, bool measured,
        CoveragePerfBlockResult result, long frequency, Action<string> refuse)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            var sample = frames[i];
            string phase = measured ? "measured" : "warm-up";
            void Invalid(string reason) => refuse($"{phase} frame {sample.FrameIndex}: {reason}");
            if (sample.FrameIndex != offset + i) Invalid("dropped frame or frame identity mismatch.");
            if (sample.PoseId != (offset + i) % 2 || Unknown(sample.PoseFingerprint)) Invalid("pose identity mismatch.");
            if (Unknown(sample.PassFingerprint) || sample.Passes <= 0 || sample.PointShadowFaces < 0) Invalid("schedule identity is unresolved.");
            if (!sample.StageComplete || !sample.DeviceComplete) Invalid("stage or device outcome is incomplete.");
            if (!string.IsNullOrEmpty(sample.Error)) Invalid("outcome error: " + sample.Error);
            if (sample.Fallback) Invalid("GPU fallback or route refusal.");
            if (sample.Actors != result.Block.Actors || sample.MainVisibleActors != result.Block.Actors) Invalid("actual or visible actor count differs.");
            if (sample.CpuFramePreparationTicks < 0 || frequency <= 0) Invalid("CPU aggregate or clock identity is invalid.");
            if (sample.Submissions != 1) Invalid("submission schedule is not one frame submission.");
            if (sample.SubmittedCompletionFences < 0 || sample.SubmissionParticipants < 0 || sample.CompletionConsumers < 0)
                Invalid("submission observations cannot be negative.");
            if (measured && sample.ResourceGrowth != 0) Invalid("hot resource growth.");
            if (measured && sample.ProductionWaits != 0) Invalid("production wait inside measurement.");
            if (result.Block.TimerLayout == CoveragePerfTimerLayout.CpuOnly)
            {
                if (sample.Gpu is not null) Invalid("CPU route contains unexpected query instrumentation.");
                if (result.Block.Condition != CoveragePerfCondition.SharedOn && (sample.SubmittedCompletionFences != 0 ||
                    sample.CompletionConsumers != 0 || sample.SubmissionParticipants !=
                    (result.Block.Condition == CoveragePerfCondition.DirectOff ? 0 : 1)))
                    Invalid("shared-host control requires zero completion fences/consumers and the direct/shared participant schedule.");
                continue;
            }
            if (sample.Gpu is not { } gpu) { Invalid("unresolved GPU baseline/query outcome."); continue; }
            if (!sample.CompletedFrameTail) Invalid("completed-frame tail is missing. A pre-End sample cannot prove a full frame.");
            if (gpu.Disjoint) Invalid("disjoint GPU query batch.");
            if (!gpu.Valid || gpu.Layout != result.Block.TimerLayout || gpu.Ticks.IsDefaultOrEmpty || gpu.Ticks.Length !=
                (result.Block.TimerLayout == CoveragePerfTimerLayout.Total ? 2 : 5)) Invalid("invalid GPU query layout or status. " + gpu.Reason);
            if (!double.IsFinite(gpu.PeriodNanoseconds) || gpu.PeriodNanoseconds <= 0 ||
                gpu.PeriodNanoseconds != result.Session?.TimestampPeriodNanoseconds) Invalid("invalid or mismatched timestamp period.");
            for (int marker = 1; !gpu.Ticks.IsDefault && marker < gpu.Ticks.Length; marker++)
                if (gpu.Ticks[marker] < gpu.Ticks[marker - 1]) { Invalid("invalid GPU query marker order."); break; }
        }
    }

    static bool SameRun(CoveragePerfRunIdentity? a, CoveragePerfRunIdentity? b) => a is not null && b is not null &&
        a.WorkloadSha == b.WorkloadSha && a.WorkflowSha == b.WorkflowSha && a.ManifestSha256 == b.ManifestSha256 &&
        a.UnitId == b.UnitId && a.RunId == b.RunId && a.Runner == b.Runner && a.Workflow == b.Workflow && a.Leg == b.Leg &&
        a.AuthorizationIdentity == b.AuthorizationIdentity && SameExecution(a.Execution, b.Execution);
    static bool SameExecution(CoveragePerfExecutionIdentity a, CoveragePerfExecutionIdentity b) =>
        (a with { Environment = [] }) == (b with { Environment = [] }) && a.Environment.SequenceEqual(b.Environment);
    static bool Unknown(string? value) => string.IsNullOrWhiteSpace(value) || value == "unavailable";
}
