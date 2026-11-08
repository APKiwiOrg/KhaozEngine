using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>An inactivity diagnostic, independent of the token used by real catalog and pack IO.</summary>
internal sealed class CatalogPhaseWatch(TimeProvider clock, TaskCompletionSource<Exception> diagnostic,
    TimeProvider? safetyClock = null)
{
    static readonly AsyncLocal<CatalogPhaseWatch?> Ambient = new();
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    readonly object _sync = new();
    readonly TimeProvider _clock = clock;
    // A controlled clock can be held before its first scripted advance. Keep real safety independent.
    readonly TimeProvider? _safetyClock = ReferenceEquals(clock, TimeProvider.System)
        ? null : safetyClock ?? TimeProvider.System;
    ITimer? _safetyTimer;
    ITimer? _timer;
    int _generation;
    bool _stopped;
    CatalogPhasePosition _phase = new(CatalogPhase.RunnerStart);
    event Action? Activity;

    public static CatalogPhaseWatch? Current => Ambient.Value;

    public static void Enter(CatalogPhase phase, ContentPublishStep? publishStep = null)
        => Ambient.Value?.Arm(new CatalogPhasePosition(phase, publishStep));

    public async Task<T> RunAsync<T>(Func<Task<T>> run)
    {
        CatalogPhaseWatch? previous = Ambient.Value;
        Ambient.Value = this;
        try
        {
            Arm(new CatalogPhasePosition(CatalogPhase.RunnerStart));
            return await run();
        }
        finally
        {
            lock (_sync)
            {
                _stopped = true;
                _generation++;
                _timer?.Dispose();
                _safetyTimer?.Dispose();
            }

            Ambient.Value = previous;
        }
    }

    public static async Task<T> ObserveAsync<T>(CatalogPhase phase, Func<Task<T>> operation)
    {
        Enter(phase);
        try
        {
            return await operation();
        }
        finally
        {
            Ambient.Value?.Arm(new CatalogPhasePosition(phase, IsRunnerProgress: true));
        }
    }

    public static async Task ObserveAsync(CatalogPhase phase, Func<Task> operation)
    {
        Enter(phase);
        try
        {
            await operation();
        }
        finally
        {
            Ambient.Value?.Arm(new CatalogPhasePosition(phase, IsRunnerProgress: true));
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

    void Arm(CatalogPhasePosition? phase, bool announce = true)
    {
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            _phase = phase ?? _phase;
            CatalogPhasePosition position = _phase;
            string name = position.ToString();
            _timer?.Dispose();
            _safetyTimer?.Dispose();
            int generation = ++_generation;
            void Expire(bool safety)
            {
                lock (_sync)
                {
                    if (!_stopped && _generation == generation)
                    {
                        string prefix = safety ? "Catalog safety phase stalled: " : "Catalog phase stalled: ";
                        diagnostic.TrySetResult(new CatalogPhaseTimeoutException(safety
                            ? CatalogPhaseRefusal.SafetyPhaseStalled : CatalogPhaseRefusal.PhaseStalled, prefix + name)
                        {
                            Phase = position.Phase,
                            Budget = Budget,
                            PublishStep = position.PublishStep,
                            IsRunnerProgress = position.IsRunnerProgress,
                        });
                    }
                }
            }

            _timer = _clock.CreateTimer(_ => Expire(false), null, Budget, Timeout.InfiniteTimeSpan);
            _safetyTimer = _safetyClock?.CreateTimer(_ => Expire(true), null, Budget, Timeout.InfiniteTimeSpan);
        }

        // Notify after releasing this watch's lock. A refresh does not propagate, so mutual gate
        // dependencies cannot recurse and two idle peers still expire independently.
        if (announce)
        {
            Activity?.Invoke();
        }
    }
}
