using System;
using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>
/// A clock that moves only when the test advances it. Wall time starts at a fixed instant and moves with it, and
/// timers fire inside <see cref="Advance"/> once it reaches their due time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    static readonly DateTimeOffset Start = new(2026, 10, 17, 3, 4, 5, TimeSpan.Zero);

    readonly object _gate = new();
    readonly List<ManualTimer> _timers = new();
    long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _timestamp);

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(GetTimestamp());

    /// <summary>The timers armed to fire exactly when the clock moves on by <paramref name="by"/>.</summary>
    internal int TimersDueIn(TimeSpan by)
    {
        lock (_gate)
        {
            long dueAt = GetTimestamp() + by.Ticks;
            int count = 0;
            foreach (ManualTimer timer in _timers)
                if (timer.DueAt == dueAt) count++;
            return count;
        }
    }

    /// <summary>Runs on the creating thread once a new timer counts in <see cref="TimersDueIn"/>. A test seam.</summary>
    internal Action? TimerVisible { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        // Armed before it is listed, so a test that sees it counted and moves the clock cannot push its due time on.
        lock (_gate)
        {
            timer.Change(dueTime, period);
            _timers.Add(timer);
        }
        TimerVisible?.Invoke();
        return timer;
    }

    /// <summary>Moves the clock forward, then runs every timer now due on the calling thread.</summary>
    internal void Advance(TimeSpan by)
    {
        long now = Interlocked.Add(ref _timestamp, by.Ticks);
        var due = new List<ManualTimer>();
        lock (_gate)
            foreach (ManualTimer timer in _timers)
                if (timer.TakeIfDue(now)) due.Add(timer);
        foreach (ManualTimer timer in due) timer.Fire();
    }

    void Remove(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        long _dueAt = long.MaxValue;
        long _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            }
            return true;
        }

        // Read under the owner's gate.
        internal long DueAt => _dueAt;

        // Called under the owner's gate. Re-arms a periodic timer, disarms a one-shot.
        internal bool TakeIfDue(long now)
        {
            if (_dueAt > now) return false;
            _dueAt = _period > 0 ? _dueAt + _period : long.MaxValue;
            return true;
        }

        internal void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
