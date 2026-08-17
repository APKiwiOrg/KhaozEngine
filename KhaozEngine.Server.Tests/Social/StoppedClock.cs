using System;

namespace KhaozEngine.Tests;

/// <summary>
/// A clock that only moves when the test says so, and counts how often it was read. Shared by the social
/// presence suites, which assert both the connect schedule exactly and the fact that a settled session
/// never reads a clock at all.
/// </summary>
internal sealed class StoppedClock
{
    private DateTimeOffset now = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    public int Reads { get; private set; }

    public DateTime UtcNow => now.UtcDateTime;

    public Func<DateTimeOffset> Now => () =>
    {
        Reads++;
        return now;
    };

    public void Advance(TimeSpan by) => now += by;
}
