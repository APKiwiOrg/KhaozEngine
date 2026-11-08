using System;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>A unit budget check that found the deadline reached.</summary>
internal sealed class RendererPerfUnitBudgetTimeoutException(RendererPerfUnitBudgetRefusal reason, string message)
    : TimeoutException(message)
{
    /// <summary>Why the budget refused.</summary>
    internal RendererPerfUnitBudgetRefusal Reason { get; } = reason;

    /// <summary>The boundary stage that ran the check.</summary>
    internal string? Stage { get; init; }
}
