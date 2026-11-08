namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Sanitized reasons for failing private oracle execution.</summary>
public enum PrivateOracleFailureRefusal
{
    /// <summary>Reports comparison mismatches through a safe category, count and secure report digest.</summary>
    ComparisonMismatches,
    /// <summary>Reports an execution failure through its secure report digest.</summary>
    ExecutionFailed,
    /// <summary>Refuses continuing without a securely created and written private report.</summary>
    SecureReportUnavailable,
}
