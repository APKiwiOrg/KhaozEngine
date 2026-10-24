using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>One program's device-free compile, joined by its first bytecode consumer.</summary>
internal sealed class D3D11ShaderCompilation<T>
{
    static readonly object flightsGate = new();
    static readonly Dictionary<string, D3D11ShaderCompilation<T>> flights = new(StringComparer.Ordinal);
    readonly Task<Outcome> result;
    readonly TaskScheduler scheduler;

    D3D11ShaderCompilation(Func<T> compile, TaskScheduler scheduler)
    {
        this.scheduler = scheduler;
        result = Task.Factory.StartNew(() =>
        {
            // A discarded shader may never join. Keep failures as data so abandonment cannot leave an
            // unobserved task exception, and rethrow the original exception and worker stack at first use.
            try { return new Outcome(compile(), null); }
            catch (Exception ex) { return new Outcome(default!, ExceptionDispatchInfo.Capture(ex)); }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler);
    }

    D3D11ShaderCompilation(Task<Outcome> result, TaskScheduler scheduler)
    { this.result = result; this.scheduler = scheduler; }

    internal static D3D11ShaderCompilation<T> Start(Func<T> compile, string? cacheKey = null,
        TaskScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(compile);
        scheduler ??= D3D11ShaderCompileWorkers.Scheduler;
        if (cacheKey is null) return new(compile, scheduler);

        // Only active flights are retained. Once a compile writes its disk entries, the next request reads
        // those entries normally. The key includes the cache directory, so independent caches stay independent.
        lock (flightsGate)
        {
            if (flights.TryGetValue(cacheKey, out var existing)) return existing;
            var flight = new D3D11ShaderCompilation<T>(() =>
            {
                try { return compile(); }
                finally { lock (flightsGate) flights.Remove(cacheKey); }
            }, scheduler);
            flights.Add(cacheKey, flight);
            return flight;
        }
    }

    internal static D3D11ShaderCompilation<T> Completed(T value)
        => new(Task.FromResult(new Outcome(value, null)), D3D11ShaderCompileWorkers.Scheduler);

    /// <summary>Prepare dependent CPU evidence only after this compile completes, without blocking a worker.</summary>
    internal D3D11ShaderCompilation<TResult> Select<TResult>(Func<T, TResult> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        D3D11ShaderCompilation<TResult>.Outcome Project(Outcome source)
        {
            if (source.Failure is not null) return new(default!, source.Failure);
            try { return new(project(source.Value), null); }
            catch (Exception error) { return new(default!, ExceptionDispatchInfo.Capture(error)); }
        }
        if (result.IsCompletedSuccessfully)
            return new(Task.FromResult(Project(result.GetAwaiter().GetResult())), scheduler);
        return new(result.ContinueWith(completed => Project(completed.GetAwaiter().GetResult()),
            CancellationToken.None, TaskContinuationOptions.DenyChildAttach, scheduler), scheduler);
    }

    internal T Join()
    {
        var outcome = result.GetAwaiter().GetResult();
        outcome.Failure?.Throw();
        return outcome.Value;
    }

    readonly record struct Outcome(T Value, ExceptionDispatchInfo? Failure);
}

/// <summary>One process-wide CPU compile bound, shared by graphics and compute programs on every device.</summary>
internal static class D3D11ShaderCompileWorkers
{
    internal static readonly TaskScheduler Scheduler = new ConcurrentExclusiveSchedulerPair(
        TaskScheduler.Default, Math.Max(1, Environment.ProcessorCount - 1)).ConcurrentScheduler;
}
