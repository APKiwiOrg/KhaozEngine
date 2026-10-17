using System;
using System.Threading;

namespace KhaozEngine.Tests.BugReports;

/// <summary>A wall clock that moves only when the test advances it.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    long utcTicks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref utcTicks, by.Ticks);
}
