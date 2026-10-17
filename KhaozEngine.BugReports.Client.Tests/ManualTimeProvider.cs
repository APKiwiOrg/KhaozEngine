using System;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>A clock that moves only when the test advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    internal void Advance(TimeSpan by) => _timestamp += by.Ticks;
}
