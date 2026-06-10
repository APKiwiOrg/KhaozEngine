using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Default <see cref="IClock"/> backed by <see cref="DateTimeOffset.Now"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared instance.</summary>
    public static readonly SystemClock Instance = new();

    private SystemClock() { }

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;
}
