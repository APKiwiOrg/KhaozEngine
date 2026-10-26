using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Sole owner of native contexts. Publication is a typed handoff after both teardown boundaries.
internal static class RendererPerfUnit
{
    internal static RendererPerfUnitOutcome Run(RendererPerfManifest manifest, RendererPerfAuthorization authorization,
        RendererPerfUnitBudget budget, IGpuHeadlessDiagnosticSettings settings,
        Func<GpuBackendKind, IGpuHeadlessDiagnosticSettings, GpuDeviceCreationAttempt> createDevice,
        Func<IGpuDevice, RendererPerfRunIdentity> identifyProduction,
        Func<IGpuDevice, RendererPerfConstructionCapture, RendererPerfSessionFactory> createSessions,
        IRendererPerfUnitPublication publication)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        var facts = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var stages = ImmutableArray.CreateBuilder<RendererPerfCpuSpan>();
        var capture = new RendererPerfConstructionCapture(manifest);
        GpuDeviceContext? production = null, diagnostic = null;
        GpuEffectiveDiagnosticConfiguration? configuration = null;
        RendererPerfReport? report = null;
        RendererPerfCompilerInventory? inventory = null;
        RendererPerfUnitFailure? early = null;
        bool certified = false;
        long start = budget.ColdStart;
        try
        {
            budget.Check("renderer.production.create.before");
            if (settings.Diagnostics != new GpuPipelineDiagnosticRequest(false, false))
                throw new InvalidOperationException("Timed settings must explicitly disable compiler metadata and invocations.");
            facts.Add("requestedBackend", settings.Backend.ToString());
            var attempt = createDevice(settings.Backend, settings);
            failures.AddRange(attempt.Failures); production = attempt.Context;
            if (production is null)
            {
                if (attempt.Failures.IsEmpty) throw new InvalidOperationException("Creation returned neither context nor failures.");
            }
            else
            {
                if (!attempt.Failures.IsEmpty) throw new InvalidOperationException("Creation returned a context with failures.");
                facts.Add("actualBackend", production.Backend.ToString());
                if (production.InjectedModules is { Count: > 0 })
                    throw new InvalidOperationException("An injected capture or overlay module was detected.");
                budget.Check("renderer.production.create.after");
                configuration = (production.GpuDevice as IGpuDeviceDiagnosticConfiguration)?.DiagnosticConfiguration
                    ?? throw new InvalidOperationException("The timed device omitted its actual effective configuration.");
                facts.Add("adapterKey", configuration.AdapterKey); facts.Add("driver", configuration.Driver);
                _ = GpuDiagnosticIdentityEncoding.Configuration(configuration);
                if (configuration.Backend != settings.Backend || configuration.Requested != settings.Diagnostics
                    || configuration.CompilerMetadataEnabled || configuration.RuntimeInvocationsEnabled || configuration.DebugLayerEnabled
                    || configuration.ValidationEnabled || configuration.PipelineCaptureFlags != 0 || configuration.PipelineExecutableFeatureEnabled)
                    throw new InvalidOperationException("Timed actual configuration enabled prohibited diagnostics or differs from its request.");
                budget.Check("renderer.production.identify.before");
                var identity = identifyProduction(production.GpuDevice);
                foreach (var control in manifest.Controls)
                    RendererPerfControlBuffers.For(RendererPerfControls.Definition(control.Id))
                        .Admit(production.GpuDevice.Capabilities, control.Id);
                RendererPerfSessionFactory? sessions = null;
                // Execute checks the original budget before the first native session. Factory setup failures keep a partial report.
                var execution = RendererPerfDriver.Execute(manifest, identity, (approved, block, clock) =>
                {
                    sessions ??= createSessions(production.GpuDevice, capture);
                    return sessions(approved, block, clock);
                }, budget);
                report = execution.Report; failures.AddRange(execution.Failures);
            }
        }
        catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.production", error); }
        finally
        {
            if (production is not null)
            {
                if (report is not null)
                {
                    var memory = new RendererPerfMemoryEvidence(report.Memory.Observations);
                    RendererPerfMemoryReport? final = null; string? reason = null;
                    try
                    {
                        budget.Check("renderer.production.memory.before");
                        final = RendererPerfSessionObservations.Memory(production.GpuDevice);
                        budget.Check("renderer.production.memory.after");
                    }
                    catch (Exception error)
                    {
                        reason = "Final production memory observation failed. Actual cause retained by UnitOutcome.";
                        RendererPerfFailures.Add(failures, "renderer.production.memory", error);
                    }
                    memory.Observe("unit", "unit-final", null, budget.Clock.Now(), budget.Clock.Frequency, final, reason);
                    report = report with { Memory = memory.Report() };
                }
                RendererPerfFailures.Release(failures, "gpu.context.dispose.native", production.Dispose);
            }
            stages.Add(new("unit.production", null, start, budget.Clock.Now()));
        }
        try
        {
            // Freeze runs after all production operation owners and the native context have been released.
            // Freezing retained managed evidence is recovery work even after a deadline or workload failure.
            inventory = capture.Freeze();
            if (report is not null)
                report = report with
                {
                    Compiler = new(RendererPerfCollectionState.Incomplete,
                    "Cold collection has not completed.", inventory.Counts, configuration, null,
                    [.. inventory.Recipes.Select(recipe => new RendererPerfCompilerResult(recipe.Id,
                        RendererPerfCollectionState.Incomplete, "Not attempted.", null,
                        [.. inventory.Occurrences.Where(o => o.RecipeId == recipe.Id).Select(o => o.Id)], null))], null, [], [])
                };
            if (!inventory.Complete && failures.Count == 0)
                throw new InvalidOperationException(inventory.Reason ?? "Frozen construction inventory is incomplete.");
            if (failures.Count == 0) budget.Check("renderer.inventory.freeze.after");
            if (report is not null && failures.Count == 0 && configuration is not null)
            {
                // Freeze the same bounded empty failure projection that final Unit publication uses.
                // Its required-byte metadata is retained base evidence, even with no failure events.
                report = RendererPerfFailureText.Project(report, failures.ToImmutable(), 16777216);
                var preparation = publication.PrepareCollection(report, inventory, budget);
                failures.AddRange(preparation.Failures);
                budget.Check("renderer.publication.prepare.after");
                if (!preparation.Ready && preparation.Failures.IsEmpty)
                    throw new InvalidOperationException("Publication refused collection preparation.");
                if (preparation.Ready && failures.Count == 0)
                {
                    budget.Check("renderer.diagnostic.create.before");
                    var replay = settings.WithDiagnostics(new(true, false)).WithAdapter(configuration.AdapterKey);
                    var attempt = createDevice(settings.Backend, replay);
                    diagnostic = attempt.Context; failures.AddRange(attempt.Failures);
                    if (diagnostic is null && attempt.Failures.IsEmpty)
                        throw new InvalidOperationException("Diagnostic creation returned neither context nor failures.");
                    if (diagnostic is not null && !attempt.Failures.IsEmpty)
                        throw new InvalidOperationException("Diagnostic creation returned a context with failures.");
                    budget.Check("renderer.diagnostic.create.after");
                    if (diagnostic is not null)
                    {
                        var cold = RendererPerfColdCollector.Collect(inventory, diagnostic, configuration, budget,
                            publication.ReserveReport, publication.CommitReportBytes);
                        report = report with { Compiler = cold.Compiler, Pipelines = cold.Pipelines };
                        failures.AddRange(cold.Failures);
                    }
                }
            }
        }
        catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.publication.prepare", error); }
        finally
        {
            if (diagnostic is not null) RendererPerfFailures.Release(failures, "gpu.context.dispose.native", diagnostic.Dispose);
        }
        // Cleanup is unconditional. Expired/cancelled budgets still reach the bounded failure publication owner.
        if (report is not null && inventory is not null)
        {
            try { budget.Check("renderer.publication.before"); }
            catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.publication.final", error); }
            report = Project(report, failures.ToImmutable(), budget);
            bool unexpectedFailure = false;
            try
            {
                var final = publication.Publish(report, inventory, budget);
                report = final.Report; failures.AddRange(final.Failures); certified = final.Certified;
                // Publish owns terminal enforcement through publication.return and its bounded
                // disk correction. No separate failure-producing check may outlive that owner.
            }
            catch (Exception error)
            {
                unexpectedFailure = true;
                RendererPerfFailures.Add(failures, "renderer.publication.final", error);
            }
            if (unexpectedFailure) report = RendererPerfFailureText.Project(Project(report, failures.ToImmutable(), budget), failures.ToImmutable(), 0);
            else if (failures.Count != 0) report = report with
            {
                Publication = report.Publication with { State = RendererPerfPublicationState.Failed, FinalUnitPublished = false },
                Assessment = report.Assessment with { ValidForPromotion = false }
            };
        }
        else
        {
            if (failures.Count == 0) failures.Add(new("renderer.unit", new InvalidOperationException("No publishable workload evidence was produced.")));
            early = new("khaozengine.renderer-unit-failure/1", authorization, facts.ToImmutable(), budget.ColdStart,
                budget.Clock.Now(), budget.Clock.Frequency, "", [], stages.ToImmutable(), new(false, 0, 0));
            early = RendererPerfFailureText.Project(early, failures.ToImmutable(), 16777216);
            try
            {
                var written = publication.WriteFailure(early, budget);
                early = written.Evidence; failures.AddRange(written.Failures);
            }
            catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.publication.early.write", error); }
        }
        return new(report, inventory, early, failures.ToImmutable()) { Certified = certified && failures.Count == 0 };
    }
    internal static RendererPerfUnitOutcome RefuseBeforeCreation(RendererPerfAuthorization authorization,
        RendererPerfUnitBudget budget, string stage, Exception error, IRendererPerfUnitPublication publication)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        RendererPerfFailures.Add(failures, stage, error);
        var early = new RendererPerfUnitFailure("khaozengine.renderer-unit-failure/1", authorization,
            ImmutableDictionary<string, string>.Empty, budget.ColdStart, budget.Clock.Now(), budget.Clock.Frequency,
            "", [], [], new(false, 0, 0));
        early = RendererPerfFailureText.Project(early, failures.ToImmutable(), 16777216);
        try
        {
            var result = publication.WriteFailure(early, budget);
            early = result.Evidence; failures.AddRange(result.Failures);
        }
        catch (Exception failure) { RendererPerfFailures.Add(failures, "renderer.publication.early.write", failure); }
        return new(null, null, early, failures.ToImmutable());
    }

    static RendererPerfReport Project(RendererPerfReport report, ImmutableArray<GpuDiagnosticFailure> failures,
        RendererPerfUnitBudget budget)
    {
        double elapsed = budget.ElapsedMilliseconds();
        report = report with
        {
            Publication = report.Publication with
            {
                State = failures.IsEmpty ? report.Publication.State : RendererPerfPublicationState.Failed,
                FinalUnitPublished = failures.IsEmpty && report.Publication.FinalUnitPublished,
                ElapsedMilliseconds = RendererPerfDriver.Value(elapsed, "ms", "unit monotonic clock"),
                EstimatedCostUsd = RendererPerfDriver.Value(elapsed / 60000 * (double)report.Manifest.Budget.RatePerMinute, "USD", "unit elapsed running time estimate")
            }
        };
        report = report with
        {
            Assessment = report.Assessment with
            { ValidForPromotion = report.Assessment.ValidForPromotion && report.Compiler.State == RendererPerfCollectionState.Complete }
        };
        return RendererPerfFailureText.Project(report, failures, 16777216);
    }
}
