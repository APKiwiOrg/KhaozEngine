namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Stable reasons a unit budget check refuses to continue.</summary>
internal enum RendererPerfUnitBudgetRefusal
{
    /// <summary>The monotonic clock read earlier than the unit's cold start.</summary>
    ClockMovedBackwards,
    /// <summary>The clock reached the unit's absolute deadline.</summary>
    DeadlineReached
}
