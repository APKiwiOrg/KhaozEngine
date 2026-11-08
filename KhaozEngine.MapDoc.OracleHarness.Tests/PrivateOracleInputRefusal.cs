namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Sanitized reasons for refusing private oracle inputs.</summary>
public enum PrivateOracleInputRefusal
{
    /// <summary>Refuses a missing named oracle input.</summary>
    MissingInput,
    /// <summary>Refuses a provenance digest differing from the supplied digest.</summary>
    ProvenanceDigestMismatch,
    /// <summary>Refuses unavailable or invalid provenance without exposing its source.</summary>
    ProvenanceUnreadable,
    /// <summary>Refuses overwriting a report path or symlink.</summary>
    ReportAlreadyExists,
    /// <summary>Refuses writing a private report inside a git worktree.</summary>
    ReportInsideWorktree,
    /// <summary>Refuses a source extraction that differs from its attestation.</summary>
    SourceVerificationFailed,
}
