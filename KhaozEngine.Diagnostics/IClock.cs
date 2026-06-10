using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Supplies the current wall-clock time for log timestamps. Injectable so tests stay deterministic.</summary>
public interface IClock
{
    /// <summary>The current time.</summary>
    DateTimeOffset Now { get; }
}
