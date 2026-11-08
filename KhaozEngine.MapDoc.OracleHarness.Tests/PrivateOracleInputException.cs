using System;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>An input admission refusal carrying only sanitized metadata, without a private inner exception.</summary>
internal sealed class PrivateOracleInputException(PrivateOracleInputRefusal reason, string message) : Exception(message)
{
    /// <summary>Gets the rule that refused the inputs.</summary>
    public PrivateOracleInputRefusal Reason { get; } = reason;

    /// <summary>Gets the missing static input variable name, or null for other refusals. Never an input value.</summary>
    public string? InputName { get; init; }

    /// <summary>Gets the source verification mismatch count, or null for other refusals. Never a source path.</summary>
    public int? MismatchCount { get; init; }
}
