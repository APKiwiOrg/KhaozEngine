using System;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>An execution refusal carrying only sanitized metadata, without a private inner exception.</summary>
internal sealed class PrivateOracleFailure(PrivateOracleFailureRefusal reason, string message) : Exception(message)
{
    /// <summary>Gets the rule that failed execution.</summary>
    public PrivateOracleFailureRefusal Reason { get; } = reason;

    /// <summary>Gets the fixed first failing comparison category, or null for other refusals.</summary>
    public string? Category { get; init; }

    /// <summary>Gets the first comparison category's mismatch count, or null for other refusals.</summary>
    public int? MismatchCount { get; init; }

    /// <summary>Gets the completed secure report's SHA-256, or null when no secure report is available.</summary>
    public string? ReportSha256 { get; init; }
}
