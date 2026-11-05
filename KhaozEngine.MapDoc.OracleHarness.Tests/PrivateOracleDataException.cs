using System;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>A test-only malformed-data refusal. Private subjects and original errors are never attached.</summary>
internal sealed class PrivateOracleDataException : MalformedDataException
{
    /// <summary>Creates a refusal with its required rule and existing developer prose.</summary>
    public PrivateOracleDataException(PrivateOracleDataRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a refusal without retaining the supplied private original exception.</summary>
    public PrivateOracleDataException(PrivateOracleDataRefusal reason, string message, Exception inner) : this(reason, message) { }

    /// <summary>Gets the rule that refused the attested comparison data.</summary>
    public PrivateOracleDataRefusal Reason { get; }
}
