using System;

namespace KhaozEngine.Benchmarks.Journal;

/// <summary>A structured refusal of the SQL Server database a journal benchmark would write to. Existing
/// <see cref="ArgumentException"/> catches remain valid, and <see cref="ArgumentException.ParamName"/> is
/// <c>config</c>. Nothing carries the connection string, its server or its catalog.</summary>
public sealed class JournalBenchmarkArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason, retaining the parameter name.</summary>
    /// <param name="reason">The rule that refused the run.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    /// <param name="paramName">The refused parameter.</param>
    public JournalBenchmarkArgumentException(JournalBenchmarkRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the run.</summary>
    public JournalBenchmarkRefusal Reason { get; }

    /// <summary>The text the Initial Catalog must contain, <c>-journal-benchmark-</c>, for
    /// <see cref="JournalBenchmarkRefusal.DedicatedDatabaseRequired"/>. A code literal, never the configured
    /// catalog.</summary>
    public string? RequiredMarker { get; init; }
}
