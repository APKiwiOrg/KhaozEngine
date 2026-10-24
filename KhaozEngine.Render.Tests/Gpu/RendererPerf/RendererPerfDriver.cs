using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfDriver
{
    internal static RendererPerfReport Run(RendererPerfManifest manifest, RendererPerfRunIdentity identity,
        RendererPerfSessionFactory factory, RendererPerfClock clock, IRendererPerfPublication publication)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(clock.Now);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clock.Frequency);
        RendererPerfAdmission.Identity(manifest, identity);
        var state = new RunState(manifest, identity, factory, clock, null);
        return publication.Publish(state.Run().Report!, clock, state.Deadline);
    }

    internal static RendererPerfExecutionResult Execute(RendererPerfManifest manifest, RendererPerfRunIdentity identity,
        RendererPerfSessionFactory factory, RendererPerfUnitBudget budget)
    {
        try
        {
            RendererPerfAdmission.Identity(manifest, identity);
            return new RunState(manifest, identity, factory, budget.Clock, budget).Run();
        }
        catch (Exception error)
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            RendererPerfFailures.Add(failures, "renderer.execute", error);
            return new(null, failures.ToImmutable());
        }
    }

    sealed class RunState(RendererPerfManifest manifest, RendererPerfRunIdentity identity,
        RendererPerfSessionFactory factory, RendererPerfClock clock, RendererPerfUnitBudget? budget)
    {
        readonly List<RendererPerfBlockReport> _blocks = [];
        readonly List<RendererPerfControlReport> _controls = [];
        readonly Dictionary<string, RendererPerfRecipeReport> _recipes = new(StringComparer.Ordinal);
        readonly List<RendererPerfBatchReport> _batches = [];
        readonly List<RendererPerfStartupReport> _startup = [];
        readonly List<string> _reasons = [], _errors = [];
        readonly RendererPerfMemoryEvidence _memory = new();
        readonly ImmutableArray<GpuDiagnosticFailure>.Builder _failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        ImmutableArray<RendererPerfFrameSample> _conditioning = [];
        string? _failure;
        long _start, _deadline;
        bool _started;
        internal long Deadline => _deadline;

        internal RendererPerfExecutionResult Run()
        {
            if (budget is not null) { _start = budget.ColdStart; _deadline = budget.Deadline; _started = true; }
            // Construct and admit the first session before reading any clock.
            var conditioning = new RendererPerfBlock("conditioning:S3", RendererPerfLayout.TotalOnly, 0, 0, manifest.ConditioningFrames);
            Execute(conditioning, (warm, measured, info, errors) => _conditioning = measured);
            foreach (var block in manifest.Blocks)
            {
                if (_failure is not null) break;
                Execute(block, (warm, measured, info, errors) =>
                {
                    _blocks.Add(new(block, warm, measured, errors));
                    if (info?.Recipe is { } recipe) _recipes.TryAdd(block.SceneId, recipe);
                });
            }
            foreach (var control in manifest.Controls)
            {
                if (_failure is not null) break;
                var block = new RendererPerfBlock($"control:{control.Id}", RendererPerfLayout.TotalOnly, 0,
                    control.WarmupFrames, control.MeasuredFrames);
                Execute(block, (warm, measured, info, errors) =>
                {
                    string state = info?.ControlStateSha256 ?? control.WorkSha256;
                    if (info?.ControlStateSha256 is null && _failure is null) Record("renderer.execute", new InvalidOperationException("The control omitted its retained-output state identity."));
                    _controls.Add(new(control, state, warm, measured, info?.Telemetry ?? ImmutableDictionary<string, RendererPerfMetric>.Empty));
                });
            }
            RendererPerfDriverAssessment.Drift(_blocks, _reasons);
            if (_failure is null && manifest.Recipes.Any(r => !_recipes.ContainsKey(r.SceneId))) Record("renderer.execute", new InvalidOperationException("Recipe construction evidence is incomplete."));
            if (_failure is not null) _reasons.Add(_failure);
            var elapsed = Value(_started ? (clock.Now() - _start) * 1000d / clock.Frequency : 0, "ms", "unit monotonic clock");
            RendererPerfMetric unknownCost = new(null, "USD", "workflow billing", RendererPerfMetricStatus.Unsupported, "Actual billed minutes are supplied after the workflow completes.");
            var report = new RendererPerfReport("khaozengine.renderer-perf-report/2", manifest, identity,
                [.. manifest.Recipes.Where(r => _recipes.ContainsKey(r.SceneId)).Select(r => _recipes[r.SceneId])],
                [.. _blocks], _memory.Report(),
                [], [.. _controls], new(budget is not null && _failure is null && _reasons.Count == 0,
                    budget is null ? [.. _reasons.Distinct(), RendererPerfCompilerEvidence.Pending.Reason!] : [.. _reasons.Distinct()],
                    RendererPerfDriverAssessment.WindowMetrics(_blocks, _conditioning), []),
                new(_failure is null ? RendererPerfPublicationState.Completed : RendererPerfPublicationState.Failed, false, false,
                    _failure, [.. _errors], elapsed, unknownCost,
                    Value(elapsed.Value!.Value / 60000d * (double)manifest.Budget.RatePerMinute, "USD", "elapsed running time estimate, excludes external setup and teardown")), [])
            { Conditioning = _conditioning, Batches = [.. _batches], Startup = [.. _startup] };
            return new(report, _failures.ToImmutable());
        }

        void Execute(RendererPerfBlock block, Action<ImmutableArray<RendererPerfFrameSample>, ImmutableArray<RendererPerfFrameSample>, RendererPerfSessionInfo?, ImmutableArray<string>> retain)
        {
            IRendererPerfSession? session = null;
            RendererPerfSessionInfo? info = null;
            var samples = new RendererPerfFrameSample[checked(block.WarmupFrames + block.MeasuredFrames)];
            int recorded = 0, harvested = 0;
            var localErrors = new List<string>();
            try
            {
                if (_started) CheckDeadline();
                session = factory(manifest, block, clock);
                if (budget is not null) CheckDeadline();
                info = session.ReadInfo();
                RendererPerfAdmission.Session(identity, info);
                if (info.Startup is { } startup) _startup.Add(startup);
                if (!_started)
                {
                    _start = info.Startup?.Wall.StartTicks ?? clock.Now();
                    _deadline = checked(_start + manifest.Limits.UnitSeconds * clock.Frequency);
                    _started = true;
                }
                CheckDeadline();
                string operation = $"{block.SceneId}/{block.Layout}/{block.Iteration}";
                _memory.Observe(operation, "startup", null, clock.Now(), clock.Frequency, info.Memory);
                while (_failure is null && recorded < samples.Length)
                {
                    int first = recorded;
                    long batchStart = clock.Now();
                    try
                    {
                        int batchEnd = Math.Min(first + manifest.BatchSize, samples.Length);
                        while (recorded < batchEnd)
                        {
                            CheckDeadline();
                            long frameStart = clock.Now();
                            int before = recorded;
                            try { session.Frame(recorded - block.WarmupFrames, ref samples[recorded]); }
                            finally
                            {
                                // A throwing Frame may still return its failed partial sample.
                                if (samples[recorded].GpuFrame is not null) recorded++;
                            }
                            if (recorded == before) throw new InvalidOperationException("The session returned without a frame sample.");
                            if (!samples[before].Errors.IsEmpty) throw new InvalidOperationException(samples[before].Errors[0]);
                            CheckFrame(frameStart, clock.Now());
                            CheckDeadline();
                        }
                    }
                    catch (Exception frameError)
                    {
                        Record("renderer.frame", frameError);
                    }
                    finally
                    {
                        if (recorded > first)
                        {
                            RendererPerfSessionInfo? batchInfo = null;
                            string? observationFailure = null;
                            // One drain and one harvest even for a failed or final short batch.
                            try
                            {
                                try { session.CompleteBatch(samples.AsSpan(first, recorded - first)); }
                                catch (Exception completion) { Record("renderer.batch.complete", completion); }
                                finally
                                {
                                    harvested = recorded;
                                    try { batchInfo = session.ReadInfo(); info = batchInfo; }
                                    catch (Exception observationError)
                                    {
                                        observationFailure = observationError.Message;
                                        Record("renderer.batch.observe", observationError);
                                    }
                                    _memory.Observe(operation, "batch", first, clock.Now(), clock.Frequency, batchInfo?.Memory, observationFailure);
                                    _batches.Add(new($"{block.SceneId}/{block.Layout}/{block.Iteration}", first, recorded - first,
                                        batchStart, clock.Now(), clock.Frequency, batchInfo?.Drain, batchInfo?.Harvest, batchInfo?.Telemetry ?? ImmutableDictionary<string, RendererPerfMetric>.Empty));
                                }
                                if (_failure is null)
                                {
                                    CheckDeadline();
                                    if (batchInfo is not null) RendererPerfAdmission.Session(identity, batchInfo);
                                    for (int i = first; i < recorded; i++)
                                    {
                                        var sample = samples[i];
                                        CheckFrame(sample.CpuStartTicks, sample.CpuEndTicks);
                                        if (sample.GpuFrame.Value / 1000000d > manifest.Limits.FrameMilliseconds)
                                            throw new TimeoutException("FrameTimeout in retained GPU duration.");
                                        int expectedSequence = RendererPerfRecipe.SequenceIndex(i - block.WarmupFrames);
                                        if (sample.FrameId != i || sample.SequenceIndex != expectedSequence || sample.CpuFrequency != clock.Frequency)
                                            throw new InvalidOperationException("A frame was dropped, duplicated or used the wrong deterministic sequence.");
                                        if (!sample.Errors.IsEmpty || sample.GpuFrame.Status != RendererPerfMetricStatus.Available)
                                            throw new InvalidOperationException(sample.Errors.FirstOrDefault() ?? "Invalid GPU frame timing.");
                                    }
                                }
                            }
                            catch (Exception completionError)
                            {
                                Record("renderer.batch.complete", completionError);
                            }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                // A pre-clock admission failure must remain observable without creating timing evidence.
                if (!_started && error is RendererPerfProtocolException) throw;
                Record(session is null ? "renderer.session.construct" : "renderer.execute", error);
                localErrors.Add(error is GpuDiagnosticFailureException transport ? transport.Failures[0].Error.Message : error.Message);
            }
            finally
            {
                if (session is not null)
                {
                    try { session.Dispose(); }
                    catch (Exception cleanup) { Record("renderer.session.dispose", cleanup); }
                }
                if (_started)
                {
                    if (_failure is null)
                        try { CheckDeadline(); } catch (Exception error) { Record("renderer.execute.cleanup", error); }
                    int kept = Math.Min(recorded, harvested);
                    int warm = Math.Min(kept, block.WarmupFrames);
                    retain([.. samples.AsSpan(0, warm)], [.. samples.AsSpan(warm, kept - warm)], info, [.. localErrors]);
                }
            }
        }

        void CheckDeadline()
        {
            if (budget is not null) budget.Check("renderer.execute");
            else if (clock.Now() >= _deadline) throw new TimeoutException("UnitTimeout.");
        }
        void CheckFrame(long start, long end)
        {
            if (end < start) throw new InvalidOperationException("The monotonic clock moved backwards.");
            if ((end - start) * 1000d / clock.Frequency > manifest.Limits.FrameMilliseconds)
                throw new TimeoutException("FrameTimeout, including retained completion.");
        }
        void Record(string stage, Exception error)
        {
            int first = _failures.Count;
            RendererPerfFailures.Add(_failures, stage, error);
            for (int i = first; i < _failures.Count; i++)
            {
                string text = _failures[i].Error.Message;
                if (_failure is null) Fail(text);
                else _errors.Add(text);
            }
        }
        void Fail(string reason) => _failure ??= reason;
    }

    internal static RendererPerfMetric Value(double value, string unit, string source)
        => new(value, unit, source, RendererPerfMetricStatus.Available, null);
}
