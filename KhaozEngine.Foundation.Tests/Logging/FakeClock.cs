using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Tests.Logging;

/// <summary>Deterministic clock for tests. Returns <see cref="Now"/> until set otherwise.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Advances <see cref="Now"/> by the given span and returns the new value.</summary>
    public DateTimeOffset Advance(TimeSpan by) => Now += by;
}
