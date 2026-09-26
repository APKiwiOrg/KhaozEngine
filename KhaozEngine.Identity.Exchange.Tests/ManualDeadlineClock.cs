using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>A fixed clock whose single timer fires only when the test advances the deadline.</summary>
internal sealed class ManualDeadlineClock(DateTimeOffset now) : TimeProvider
{
    private readonly object sync = new();
    private ManualTimer? timer;

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (sync)
        {
            if (timer is not null)
                throw new InvalidOperationException("The manual deadline clock supports one timer.");
            timer = new ManualTimer(callback, state);
            return timer;
        }
    }

    public void ExpireDeadline()
    {
        ManualTimer current;
        lock (sync)
        {
            current = timer ?? throw new InvalidOperationException("No deadline timer was created.");
        }
        current.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly object sync = new();
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (sync) return !disposed;
        }

        public void Dispose()
        {
            lock (sync) disposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire()
        {
            lock (sync)
            {
                if (disposed) return;
            }
            callback(state);
        }
    }
}
