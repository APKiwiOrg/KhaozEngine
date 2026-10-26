using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Explicitly expires active diagnostic timers in negative tests. Normal scenarios use System.</summary>
internal sealed class ManualCatalogPhaseClock : TimeProvider
{
    readonly List<ManualTimer> _timers = [];
    long _elapsed;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsed));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            var timer = new ManualTimer(callback, state, _elapsed + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }
    }

    public void ExpireActive() => Advance(TimeSpan.FromSeconds(20));

    public void Advance(TimeSpan elapsed)
    {
        ManualTimer[] timers;
        lock (_timers)
        {
            _elapsed += elapsed.Ticks;
            timers = _timers.ToArray();
        }

        foreach (ManualTimer timer in timers)
        {
            timer.Fire(Interlocked.Read(ref _elapsed));
        }
    }

    sealed class ManualTimer(TimerCallback callback, object? state, long due) : ITimer
    {
        int _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire(long now)
        {
            if (now >= due && Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                callback(state);
            }
        }
    }
}
