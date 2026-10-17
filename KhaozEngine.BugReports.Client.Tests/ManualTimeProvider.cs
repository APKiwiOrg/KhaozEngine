using System;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>A clock that moves only when the test advances it. Wall time starts at a fixed instant and moves with it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    static readonly DateTimeOffset Start = new(2026, 10, 17, 3, 4, 5, TimeSpan.Zero);

    long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(_timestamp);

    internal void Advance(TimeSpan by) => _timestamp += by.Ticks;
}
