using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfColdCollector
{
    internal static RendererPerfColdResult Collect(RendererPerfCompilerInventory inventory, GpuDeviceContext diagnostic,
        GpuEffectiveDiagnosticConfiguration production, RendererPerfUnitBudget budget,
        Func<GpuBackendKind, RendererPerfReadReservation?> reserveReport,
        Action<RendererPerfReadReservation, int> commitReportBytes)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        var pipelines = ImmutableArray.CreateBuilder<GpuPipelineCompilerReport>();
        var results = ImmutableArray.CreateBuilder<RendererPerfCompilerResult>();
        var spans = ImmutableArray.CreateBuilder<RendererPerfCpuSpan>();
        GpuEffectiveDiagnosticConfiguration? configuration = null;
        var attempted = new int[2]; var reported = new int[2];
        try
        {
            budget.Check("renderer.collect.before");
            configuration = (diagnostic.GpuDevice as IGpuDeviceDiagnosticConfiguration)?.DiagnosticConfiguration
                ?? throw new InvalidOperationException("Diagnostic device omitted actual configuration.");
            var reasons = RendererPerfCompilerCorrespondence.Configurations(production, configuration);
            if (!reasons.IsEmpty) throw new InvalidOperationException(string.Join(" ", reasons));
            if (!inventory.Complete) throw new InvalidOperationException("Incomplete construction inventory cannot start replay.");
            foreach (var recipe in inventory.Recipes)
            {
                budget.Check("renderer.graph.reserve.before");
                var reservation = reserveReport(diagnostic.Backend);
                if (reservation is null) throw new InvalidOperationException("Compiler report capacity refused before graph creation.");
                var graph = new List<(string Kind, IDisposable Resource)>();
                var occurrences = inventory.Occurrences.Where(o => o.RecipeId == recipe.Id).Select(o => o.Id).ToImmutableArray();
                GpuPipelineCompilerReadResult? read = null;
                bool commitAttempted = false;
                int? pipelineIndex = null;
                long start = budget.Clock.Now();
                int before = failures.Count;
                try
                {
                    budget.Check("renderer.graph.reserve.after");
                    var expected = RendererPerfCompilerReadContract.ForAvailable(diagnostic.Backend, reservation.MaximumEncodedBytes);
                    if (reservation != expected) throw new InvalidOperationException("The compiler read reservation substituted its fixed profile.");
                    budget.Check("renderer.graph.before");
                    attempted[(int)recipe.Kind]++;
                    object pipeline = RendererPerfReplayGraph.Create(inventory, recipe, diagnostic.GpuDevice.Factory, graph, budget);
                    if (pipeline is not IGpuBudgetedPipelineDiagnostics reader)
                        throw new NotSupportedException("Replay requires the optional budgeted compiler reader.");
                    if (pipeline is not IGpuPipelineConstructionDiagnostics construction)
                        throw new InvalidOperationException("Replay omitted the required actual construction witness.");
                    var replayWitness = construction.ConstructionIdentity;
                    budget.Check("renderer.graph.read.before");
                    read = reader.ReadCompilerReport(reservation.Request, budget.Check, budget.Cancellation);
                    // Transfer actual reader events and partial evidence before encoding, commits or cleanup.
                    failures.AddRange(read.Failures);
                    pipelineIndex = pipelines.Count; pipelines.Add(read.Report); reported[(int)recipe.Kind]++;
                    try { budget.Check("renderer.graph.read.after"); }
                    catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.graph.read", error); }
                    if (!ReferenceEquals(read.Request, reservation.Request))
                        throw new InvalidOperationException($"The native reader substituted the frozen request. Expected {reservation.Request}, returned {read.Request}.");
                    int bytes = RendererPerfCompilerReadContract.EncodedResultBytes(read);
                    commitAttempted = true;
                    commitReportBytes(reservation, bytes);
                    if (!read.Complete && failures.Count == before)
                        throw new InvalidOperationException(read.RefusalReason ?? "The compiler reader returned incomplete evidence.");
                    if (read.Complete && failures.Count == before)
                    {
                        if (read.Report.Identity with { Stages = replayWitness.Stages } != replayWitness
                            || !read.Report.Identity.Stages.SequenceEqual(replayWitness.Stages))
                            throw new InvalidOperationException("The compiler report differs from its actual replay construction witness.");
                        var mismatch = RendererPerfCompilerCorrespondence.Recipe(inventory, recipe, read.Report,
                            new(read.Request, read.Counts, read.Complete, read.RefusalReason), production, configuration);
                        if (!mismatch.IsEmpty) throw new InvalidOperationException(string.Join(" ", mismatch));
                    }
                }
                catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.graph.read", error); }
                finally
                {
                    if (!commitAttempted && read is null)
                    {
                        try
                        {
                            // No graph/read result is silently dropped. A failed graph still retains its frozen request.
                            int bytes = checked(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                                new RendererPerfCompilerReadEvidence(reservation.Request, null, false, "Graph or read did not complete."),
                                RendererPerfManifest.JsonOptions).Length + 256);
                            commitAttempted = true;
                            commitReportBytes(reservation, bytes);
                        }
                        catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.graph.read.commit", error); }
                    }
                    for (int i = graph.Count - 1; i >= 0; i--)
                        RendererPerfFailures.Release(failures, "renderer.graph.dispose." + graph[i].Kind + "." + i, graph[i].Resource.Dispose);
                    spans.Add(new("compiler.recipe", null, start, budget.Clock.Now()));
                    results.Add(new(recipe.Id, failures.Count == before && read is { Complete: true }
                            ? RendererPerfCollectionState.Complete : State(failures),
                        failures.Count == before && read is { Complete: true } ? null : "Replay or read did not complete.",
                        pipelineIndex, occurrences, new(read?.Request ?? reservation.Request, read?.Counts,
                            read is { Complete: true }, read?.RefusalReason ?? (read is null ? "Graph or read did not complete." : null))));
                }
                budget.Check("renderer.graph.dispose.after");
                if (failures.Count != before) break;
            }
        }
        catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.collect", error); }
        foreach (var recipe in inventory.Recipes.Skip(results.Count))
            results.Add(new(recipe.Id, RendererPerfCollectionState.Incomplete, "Not attempted after collection stopped.", null,
                inventory.Occurrences.Where(o => o.RecipeId == recipe.Id).Select(o => o.Id).ToImmutableArray(), null));
        var counts = inventory.Counts with
        {
            ReplayAttempts = new(attempted[0], attempted[1]),
            Reports = new(reported[0], reported[1]),
            Executables = pipelines.Sum(p => p.Executables.Length),
            Statistics = pipelines.Sum(p => p.Executables.Sum(e => e.Statistics.Length))
        };
        var compiler = new RendererPerfCompilerEvidence(failures.Count == 0 ? RendererPerfCollectionState.Complete : State(failures),
            failures.Count == 0 ? null : "Cold compiler collection failed. Actual causes retained by UnitOutcome.", counts,
            production, configuration, results.ToImmutable(), null, spans.ToImmutable(), []);
        if (failures.Count == 0)
        {
            var reasons = RendererPerfCompilerCorrespondence.Validate(inventory, pipelines.ToImmutable(), compiler);
            if (!reasons.IsEmpty)
            {
                failures.Add(new("renderer.graph.read", new InvalidOperationException(string.Join(" ", reasons))));
                compiler = compiler with { State = RendererPerfCollectionState.Invalid, Reason = "Compiler correspondence failed." };
            }
        }
        return new(inventory, compiler, pipelines.ToImmutable(), failures.ToImmutable());
    }
    static RendererPerfCollectionState State(ImmutableArray<GpuDiagnosticFailure>.Builder failures)
        => failures.Count != 0 && failures[0].Error is OperationCanceledException ? RendererPerfCollectionState.Cancelled
            : failures.Count != 0 && failures[0].Error is TimeoutException ? RendererPerfCollectionState.DeadlineExceeded : RendererPerfCollectionState.Invalid;
}
