using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Owns every started Catalog runner until it has joined, including exceptional exits.</summary>
internal sealed class CatalogRunnerLifetime(TimeProvider? clock = null, Action<Exception>? abort = null)
{
    readonly CancellationTokenSource _lifetime = new();
    readonly List<Task<ContentUpgradeReport>> _runs = [];
    readonly HashSet<Exception> _observed = [];
    Task? _finish;

    public TaskCompletionSource<Exception> Diagnostic { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<Task<ContentUpgradeReport>> Runs => _runs;
    public CancellationToken Token => _lifetime.Token;

    public Task<ContentUpgradeReport> Start(Func<CancellationToken, Task<ContentUpgradeReport>> run)
    {
        // The placeholder is owned before invoking code that can reach its first asynchronous wait.
        var owned = new TaskCompletionSource<ContentUpgradeReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runs.Add(owned.Task);
        _ = ProduceAsync();
        return owned.Task;

        async Task ProduceAsync()
        {
            try
            {
                var watch = new CatalogPhaseWatch(clock ?? TimeProvider.System, Diagnostic);
                owned.TrySetResult(await watch.RunAsync(() => run(Token)));
            }
            catch (Exception failure)
            {
                owned.TrySetException(failure);
            }
        }
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> scenario)
    {
        T result = default!;
        Exception? primary = null;
        try
        {
            result = await scenario();
            foreach (Task<ContentUpgradeReport> run in _runs)
            {
                await AwaitAsync(run);
            }
        }
        catch (Exception failure)
        {
            primary = failure;
        }

        await FinishAsync(primary);
        return result;
    }

    public async Task<T> AwaitAsync<T>(Task<T> task)
    {
        if (Diagnostic.Task.IsCompletedSuccessfully || await Task.WhenAny(task, Diagnostic.Task) == Diagnostic.Task)
        {
            ExceptionDispatchInfo.Capture(await Diagnostic.Task).Throw();
        }

        try
        {
            return await task;
        }
        catch (Exception failure)
        {
            _observed.Add(failure);
            throw;
        }
    }

    public async Task ReadyAsync(Task signal, Task<ContentUpgradeReport> producer, string phase)
    {
        Task winner = await AwaitAsync(Task.WhenAny(signal, producer));
        if (producer.IsFaulted || producer.IsCanceled || winner == producer && !signal.IsCompleted)
        {
            ContentUpgradeReport ended = await AwaitAsync(producer);
            throw new InvalidOperationException("runner completed before " + phase + ": " + string.Join(" | ", ended.Lines));
        }

        await signal;
    }

    public Task FinishAsync(Exception? primary = null) => _finish ??= FinishCoreAsync(primary);

    async Task FinishCoreAsync(Exception? primary)
    {
        var failures = new List<Exception>();
        if (primary is not null)
        {
            _observed.Add(primary);
            try
            {
                abort?.Invoke(primary);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
            finally
            {
                try
                {
                    _lifetime.Cancel();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
        }

        try
        {
            foreach (Task<ContentUpgradeReport> run in _runs)
            {
                try
                {
                    // Teardown cannot leave an in-flight store operation behind its lease or files.
                    await run;
                }
                catch (Exception failure)
                {
                    Collect(failure);
                }
            }
        }
        finally
        {
            _lifetime.Dispose();
        }

        if (failures.Count > 0)
        {
            if (primary is not null)
            {
                failures.Insert(0, primary);
            }

            throw new AggregateException("Catalog scenario and cleanup failures", failures);
        }

        if (primary is not null)
        {
            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        void Collect(Exception failure)
        {
            if (_observed.Contains(failure) || failure is OperationCanceledException && _lifetime.IsCancellationRequested)
            {
                return;
            }

            if (failure is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    Collect(inner);
                }
            }
            else
            {
                _observed.Add(failure);
                failures.Add(failure);
            }
        }
    }
}
