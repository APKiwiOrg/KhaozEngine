using System;

namespace KhaozEngine.Tests.MapDocCompatibility;

/// <summary>A test-only refusal of the legacy water comparison's source scope.</summary>
internal sealed class LegacyWaterComparisonException : Exception
{
    /// <summary>Creates a refusal with its rule and existing developer prose.</summary>
    public LegacyWaterComparisonException(LegacyWaterComparisonRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Creates a refusal retaining the original failure.</summary>
    public LegacyWaterComparisonException(LegacyWaterComparisonRefusal reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;

    /// <summary>Gets the rule that refused the comparison source.</summary>
    public LegacyWaterComparisonRefusal Reason { get; }

    /// <summary>Gets the refused plane, when the guard has one.</summary>
    public int? Plane { get; init; }
}
