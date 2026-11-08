using System;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Rule-specific offline geometry workspace refusals.</summary>
public enum CoverageToolStateReason
{
    HierarchyWalkCapacity = 1,
}

/// <summary>A geometry hierarchy walk requires more reserved entries than its stack provides.</summary>
public sealed class CoverageToolStateException : InvalidOperationException
{
    internal CoverageToolStateException(int required, int capacity)
        : base("Coverage hierarchy exceeds its reserved walk capacity.")
    {
        Reason = CoverageToolStateReason.HierarchyWalkCapacity;
        Required = required;
        Capacity = capacity;
    }

    public CoverageToolStateReason Reason { get; }
    public int Required { get; }
    public int Capacity { get; }
}
