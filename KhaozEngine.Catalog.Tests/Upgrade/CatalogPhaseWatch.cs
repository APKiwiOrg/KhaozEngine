using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>An inactivity diagnostic, independent of the token used by real catalog and pack IO.</summary>
internal sealed class CatalogPhaseWatch(TimeProvider clock, TaskCompletionSource<Exception> diagnostic)
{
    static readonly AsyncLocal<CatalogPhaseWatch?> Ambient = new();
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    readonly object _sync = new();
    ITimer? _timer;
    int _generation;
    bool _stopped;
    string _phase = "runner start";
    event Action? Activity;

    public static CatalogPhaseWatch? Current => Ambient.Value;

    public static void Enter(string phase) => Ambient.Value?.Arm(phase);

    public async Task<T> RunAsync<T>(Func<Task<T>> run)
    {
        CatalogPhaseWatch? previous = Ambient.Value;
        Ambient.Value = this;
        try
        {
            Arm("runner start");
            return await run();
        }
        finally
        {
            lock (_sync)
            {
                _stopped = true;
                _generation++;
                _timer?.Dispose();
            }

            Ambient.Value = previous;
        }
    }

    public static async Task<T> ObserveAsync<T>(string phase, Func<Task<T>> operation)
    {
        Enter(phase);
        try
        {
            return await operation();
        }
        finally
        {
            Enter("runner progress after " + phase);
        }
    }

    public static async Task ObserveAsync(string phase, Func<Task> operation)
    {
        Enter(phase);
        try
        {
            await operation();
        }
        finally
        {
            Enter("runner progress after " + phase);
        }
    }

    /// <summary>A gate follows its producer's real progress, rather than timing the whole peer publish.</summary>
    public IDisposable? Follow(CatalogPhaseWatch? producer)
    {
        if (producer is null)
        {
            return null;
        }

        void Refresh() => Arm(null, announce: false);
        producer.Activity += Refresh;
        Refresh();
        return new Following(producer, Refresh);
    }

    sealed class Following(CatalogPhaseWatch producer, Action refresh) : IDisposable
    {
        public void Dispose() => producer.Activity -= refresh;
    }

    void Arm(string? phase, bool announce = true)
    {
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            _phase = phase ?? _phase;
            string name = _phase;
            _timer?.Dispose();
            int generation = ++_generation;
            _timer = clock.CreateTimer(_ =>
            {
                lock (_sync)
                {
                    if (!_stopped && _generation == generation)
                    {
                        diagnostic.TrySetResult(new TimeoutException("Catalog phase stalled: " + name));
                    }
                }
            }, null, Budget, Timeout.InfiniteTimeSpan);
        }

        // Notify after releasing this watch's lock. A refresh does not propagate, so mutual gate
        // dependencies cannot recurse and two idle peers still expire independently.
        if (announce)
        {
            Activity?.Invoke();
        }
    }
}
