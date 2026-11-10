namespace KhaozEngine.Tests;

/// <summary>Why an allocation measurement was refused or invalidated.</summary>
public enum AllocationMeasurementRefusal
{
    /// <summary>The collection can run beside other tests, invalidating the process-wide budget.</summary>
    CollectionNotIsolated,
    /// <summary>The process allocated at least the reserved budget before the window completed.</summary>
    BudgetExhausted,
    /// <summary>A garbage collection interrupted the measurement window.</summary>
    CollectionInterrupted,
    /// <summary>The no-GC region ended before the measurement window completed.</summary>
    RegionEnded,
    /// <summary>Another no-GC region is already active before the runtime start.</summary>
    RegionAlreadyActive,
    /// <summary>The runtime threw while starting the no-GC region.</summary>
    RuntimeStartFailed,
    /// <summary>The runtime refused to reserve the requested process-wide budget.</summary>
    BudgetReservationFailed,
}
