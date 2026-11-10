using System;
using Xunit.Sdk;

namespace KhaozEngine.Tests;

/// <summary>An xUnit failure with the allocation measurement's refusal and diagnostics.</summary>
internal sealed class AllocationMeasurementException : XunitException
{
    internal AllocationMeasurementException(AllocationMeasurementRefusal reason, string description, string message)
        : base(message) => (Reason, Description) = (reason, description);

    internal AllocationMeasurementException(AllocationMeasurementRefusal reason, string description, string message, Exception inner)
        : base(message, inner) => (Reason, Description) = (reason, description);

    /// <summary>The refused isolation rule or the cause of the invalid window.</summary>
    public AllocationMeasurementRefusal Reason { get; }
    /// <summary>The caller's measurement description.</summary>
    public string Description { get; }
    /// <summary>The requested process-wide byte budget, including a refused start.</summary>
    public long? Budget { get; init; }
    /// <summary>The bytes allocated by the process during the window.</summary>
    public long? ProcessBytes { get; init; }
    /// <summary>The bytes allocated by the measured thread during the window.</summary>
    public long? ThreadBytes { get; init; }
    /// <summary>The bytes allocated by other threads during the window.</summary>
    public long? OtherThreadBytes => ProcessBytes - ThreadBytes;
    /// <summary>The number of test messages reported during the window.</summary>
    public long? ReportedMessages { get; init; }
    /// <summary>The collection counts during the window, by generation.</summary>
    public (int Gen0, int Gen1, int Gen2)? Collections { get; init; }
    /// <summary>The runtime's reason for refusing to start or end the region, if one was reported.</summary>
    public string? RuntimeReason { get; init; }
}
