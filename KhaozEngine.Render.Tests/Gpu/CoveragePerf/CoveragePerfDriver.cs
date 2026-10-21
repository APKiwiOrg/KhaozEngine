using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.Gpu;

internal static class CoveragePerfDriver
{
    internal static CoveragePerfReport Run(ReadOnlySpan<byte> manifestJson, CoveragePerfRunIdentity identity,
        CoveragePerfSessionFactory factory, CoveragePerfClock clock, Action<CoveragePerfBlockResult> publishBlock,
        Action<CoveragePerfReport> publishUnit)
    {
        CoveragePerfManifest? manifest = null;
        try
        {
            // This refusal boundary must stay ahead of every clock read and factory call.
            manifest = CoveragePerfManifest.Parse(manifestJson);
            manifest.ValidateAuthorization(identity);
            if (clock.Frequency <= 0) throw new InvalidDataException("Clock frequency must be positive.");
            identity = identity with { Execution = CoveragePerfExecutionIdentity.Capture(identity.Execution, clock, manifest) };
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            string reason = Error("validation", ex);
            var refusal = new CoveragePerfReport(CoveragePerfRunStatus.Refused, reason, manifest,
                Convert.ToHexStringLower(SHA256.HashData(manifestJson)), identity, [])
            { Errors = [reason] };
            return PublishUnit(refusal, publishUnit, null);
        }

        var blocks = ImmutableArray.CreateBuilder<CoveragePerfBlockResult>(manifest.Blocks.Length);
        var failure = new FailureState();
        Deadline? deadline = null;
        try
        {
            deadline = new(clock, manifest);
            foreach (var block in manifest.Blocks)
            {
                deadline.Check("before factory");
                var result = RunBlock(manifest, block, factory, clock, deadline) with { RunIdentity = identity };
                blocks.Add(result);
                failure.Add(result.Status, result.Errors);
                try { publishBlock(result); }
                catch (Exception ex) { failure.Add("block publication", ex); }
                try { deadline.Check("after block publication"); }
                catch (Exception ex) { failure.Add("block publication", ex); }
                // Invalid evidence is terminal, including during the one-actor and phase-layout GPU stages.
                if (failure.Failed) break;
            }
        }
        catch (Exception ex) { failure.Add("unit", ex); }

        bool synthetic = false;
        foreach (var block in blocks) synthetic |= block.Session?.Synthetic == true;
        var report = new CoveragePerfReport(failure.Status,
            failure.Failed ? failure.Reason : synthetic ? "Synthetic evidence cannot establish native proof." : "Budget evaluation pending.",
            manifest, manifest.Sha256, identity, blocks.ToImmutable())
        { Errors = failure.Errors.ToImmutableArray() };
        return PublishUnit(report, publishUnit, deadline);
    }

    static CoveragePerfBlockResult RunBlock(CoveragePerfManifest manifest, CoveragePerfBlock block,
        CoveragePerfSessionFactory factory, CoveragePerfClock clock, Deadline deadline)
    {
        // Retain the same slots through Frame, batch completion, cleanup and the eventual immutable report copy.
        var warmup = new CoveragePerfFrameSample[block.WarmupFrames];
        var samples = new CoveragePerfFrameSample[block.MeasuredFrames];
        int warmCount = 0, sampleCount = 0;
        var failure = new FailureState();
        ICoveragePerfSession? session = null;
        CoveragePerfSessionInfo? info = null;
        try
        {
            deadline.Check("before setup");
            session = factory(manifest, block, clock) ?? throw new InvalidOperationException("Factory returned no session.");
            deadline.Check("after setup");
            info = session.ReadInfo();
            deadline.Check("after setup diagnostics");
            string? refusal = ValidateInfo(manifest, block, info, requirePeriod: false);
            if (refusal is not null) failure.Refuse(refusal);
            else
            {
                RunFrames(session, manifest, block, deadline, warmup, 0, measured: false, ref warmCount, failure);
                if (!failure.Failed && block.TimerLayout != CoveragePerfTimerLayout.CpuOnly)
                {
                    info = session.ReadInfo();
                    deadline.Check("after warm-up diagnostics");
                    refusal = ValidateInfo(manifest, block, info);
                    if (refusal is not null) failure.Refuse(refusal);
                }
                if (!failure.Failed)
                    RunFrames(session, manifest, block, deadline, samples, warmup.Length, measured: true, ref sampleCount, failure);
                if (!failure.Failed)
                {
                    info = session.ReadInfo();
                    deadline.Check("after completion diagnostics");
                    refusal = ValidateInfo(manifest, block, info);
                    if (refusal is not null) failure.Refuse(refusal);
                }
            }
        }
        catch (Exception ex) { failure.Add("block", ex); }
        finally
        {
            try { session?.Dispose(); }
            catch (Exception ex) { failure.Add("cleanup", ex); }
            try { deadline.Check("after cleanup"); }
            catch (Exception ex) { failure.Add("cleanup", ex); }
        }
        return new(block, failure.Status, failure.Reason, info,
            ImmutableArray.Create(warmup, 0, warmCount), ImmutableArray.Create(samples, 0, sampleCount))
        { Errors = failure.Errors.ToImmutableArray() };
    }

    static void RunFrames(ICoveragePerfSession session, CoveragePerfManifest manifest, CoveragePerfBlock block,
        Deadline deadline, CoveragePerfFrameSample[] storage, int offset, bool measured, ref int count, FailureState failure)
    {
        int batchStart = 0;
        for (int i = 0; i < storage.Length; i++)
        {
            long start = deadline.Check("before frame");
            ref var sample = ref storage[i];
            sample.FrameIndex = offset + i;
            count = i + 1; // A throwing Frame can already have written useful partial evidence into this slot.
            try { session.Frame(offset + i, ref sample); }
            catch (Exception ex)
            {
                failure.Add("frame", ex);
                sample.Error ??= failure.Reason;
            }
            try
            {
                // The wall is recorded before either limit can refuse, so a refused frame still reports it.
                long end = deadline.Read();
                sample.FrameWallTicks = checked(end - start);
                deadline.CheckFrame(start, end, measured);
            }
            catch (Exception ex) { failure.Add("frame", ex); }
            if (failure.Failed) return;
            if (count - batchStart != manifest.BatchFrames && count != storage.Length) continue;

            deadline.Check("before batch completion");
            try { session.CompleteBatch(storage.AsSpan(batchStart, count - batchStart)); }
            catch (Exception ex) { failure.Add("batch completion", ex); }
            try { deadline.Check("after batch completion"); }
            catch (Exception ex) { failure.Add("batch completion", ex); }
            if (failure.Failed) return;
            for (int j = batchStart; j < count; j++)
            {
                string? invalid = ValidateSample(block, storage[j], offset + j, measured);
                if (invalid is null) continue;
                failure.Add("outcome", new InvalidDataException(invalid));
                return;
            }
            batchStart = count;
        }
    }

    static string? ValidateInfo(CoveragePerfManifest manifest, CoveragePerfBlock block, CoveragePerfSessionInfo info,
        bool requirePeriod = true)
    {
        if (info.OrdinarySkinningRoute != manifest.OrdinarySkinning || info.CoveredSurfaceRoute != manifest.CoveredSurface)
            return "Effective routes do not match the approved GPU routes.";
        if (info.Backend != manifest.Backend) return "Effective backend does not match the approved backend.";
        if (!info.Synthetic && !info.NativeDevice) return "Session has no native device evidence.";
        if (block.TimerLayout == CoveragePerfTimerLayout.CpuOnly) return null;
        if (info.SoftwareAdapter) return "Software adapter cannot establish GPU proof.";
        if (!info.TimestampSupported || requirePeriod &&
            (info.TimestampPeriodNanoseconds is not double period || !double.IsFinite(period) || period <= 0))
            return "GPU timestamp capability or period is unavailable.";
        return null;
    }

    static string? ValidateSample(CoveragePerfBlock block, CoveragePerfFrameSample sample, int frameIndex, bool measured)
    {
        if (!string.IsNullOrEmpty(sample.Error)) return sample.Error;
        if (sample.FrameIndex != frameIndex) return "Frame identity does not match the declared sequence.";
        if (!sample.StageComplete || !sample.DeviceComplete) return "Stage or device outcome is incomplete.";
        if (sample.Fallback) return "Frame used a fallback route.";
        if (sample.Actors != block.Actors || sample.MainVisibleActors != block.Actors)
            return "Actual or visible actor count does not match the approved block.";
        if (measured && sample.ProductionWaits != 0) return "Frame performed a production wait.";
        if (measured && sample.ResourceGrowth != 0) return "Measured frame grew hot resources.";
        if (block.TimerLayout == CoveragePerfTimerLayout.CpuOnly) return null;
        if (sample.Gpu is not { } gpu) return "GPU timestamp outcome is unresolved.";
        if (gpu.Layout != block.TimerLayout || !gpu.Valid || gpu.Disjoint || gpu.Ticks.IsDefaultOrEmpty
            || gpu.Ticks.Length < 2 || !double.IsFinite(gpu.PeriodNanoseconds) || gpu.PeriodNanoseconds <= 0)
            return $"GPU timestamp outcome is invalid or disjoint. {gpu.Reason}";
        return null;
    }

    static CoveragePerfReport PublishUnit(CoveragePerfReport report, Action<CoveragePerfReport> publish, Deadline? deadline)
    {
        // The callback receives a publication candidate. Only Run's returned report includes its outcome and deadline check.
        bool published = true;
        try { publish(report); }
        catch (Exception ex) { published = false; report = PublicationFailure(report, Error("unit publication", ex)); }
        report = report with { ReturnedByRun = true, FinalUnitPublished = published };
        var verdict = CoveragePerfBudgets.Evaluate(report);
        report = report with { CpuStatus = verdict.Cpu.Status, GpuStatus = verdict.Gpu.Status };
        try { deadline?.Check("after unit publication and evaluation"); }
        catch (Exception ex) { report = PublicationFailure(report, Error("unit publication", ex)) with { FinalUnitPublished = false }; }
        return report;
    }

    static CoveragePerfReport PublicationFailure(CoveragePerfReport report, string error) => report with
    {
        Status = CoveragePerfRunStatus.Failed,
        Reason = report.Errors.IsEmpty ? error : report.Reason,
        Errors = report.Errors.Add(error),
        CpuStatus = CoveragePerfBudgetStatus.Unproved,
        GpuStatus = CoveragePerfBudgetStatus.Unproved,
    };

    static string Error(string phase, Exception error) => $"{phase}: {error.GetType().Name}: {error.Message}";

    sealed class FailureState
    {
        internal List<string> Errors { get; } = new(4);
        internal CoveragePerfRunStatus Status { get; private set; } = CoveragePerfRunStatus.Completed;
        internal bool Failed => Errors.Count != 0;
        internal string Reason => Failed ? Errors[0] : "";

        internal void Refuse(string reason) => Add(CoveragePerfRunStatus.Refused, [reason]);
        internal void Add(string phase, Exception error) => Add(CoveragePerfRunStatus.Failed, [Error(phase, error)]);
        internal void Add(CoveragePerfRunStatus status, ImmutableArray<string> errors)
        {
            if (errors.IsEmpty) return;
            if (!Failed) Status = status;
            Errors.AddRange(errors);
        }
    }

    // Cooperative checks cannot interrupt a blocked native call, including cleanup or publication.
    // Such a call can exceed these limits until the separately enforced hosted process timeout.
    sealed class Deadline
    {
        readonly CoveragePerfClock _clock;
        readonly long _start;
        readonly Int128 _unitTicks, _frameTicksTimes1000;
        long _last;

        internal Deadline(CoveragePerfClock clock, CoveragePerfManifest manifest)
        {
            _clock = clock;
            _start = _last = clock.Now();
            _unitTicks = (Int128)clock.Frequency * manifest.MaxUnitSeconds;
            _frameTicksTimes1000 = (Int128)clock.Frequency * manifest.MaxFrameMilliseconds;
        }

        internal long Check(string phase) => CheckUnit(Read(), phase);

        internal long Read()
        {
            long now = _clock.Now();
            if (now < _last) throw new InvalidDataException("Injected clock moved backwards.");
            _last = now;
            return now;
        }

        long CheckUnit(long now, string phase)
        {
            if ((Int128)now - _start >= _unitTicks) throw new TimeoutException($"Unit deadline reached {phase}.");
            return now;
        }

        // Warm-up absorbs cold setup, so only the unit deadline bounds a warm-up frame. The frame limit is measured only.
        internal void CheckFrame(long start, long now, bool measured)
        {
            CheckUnit(now, "after frame");
            if (measured && ((Int128)now - start) * 1000 >= _frameTicksTimes1000)
                throw new TimeoutException("Frame deadline reached after frame.");
        }
    }
}
