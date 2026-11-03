using System;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>A unit budget check that found the monotonic clock unusable.</summary>
internal sealed class RendererPerfUnitBudgetClockException(RendererPerfUnitBudgetRefusal reason, string message)
    : InvalidOperationException(message)
{
    /// <summary>Why the budget refused.</summary>
    internal RendererPerfUnitBudgetRefusal Reason { get; } = reason;

    /// <summary>The boundary stage that ran the check.</summary>
    internal string? Stage { get; init; }
}
