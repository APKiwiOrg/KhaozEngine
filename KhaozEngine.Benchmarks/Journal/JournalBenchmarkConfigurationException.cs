using System;

namespace KhaozEngine.Benchmarks.Journal;

/// <summary>A structured refusal of the SQL Server connection variable a journal benchmark names. Existing
/// <see cref="InvalidOperationException"/> catches remain valid. Nothing carries the variable's value.</summary>
public sealed class JournalBenchmarkConfigurationException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    /// <param name="reason">The rule that refused the run.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public JournalBenchmarkConfigurationException(JournalBenchmarkRefusal reason, string message) : base(message)
        => Reason = reason;

    /// <summary>The rule that refused the run.</summary>
    public JournalBenchmarkRefusal Reason { get; }

    /// <summary>The name of the environment variable the connection string was read from, for
    /// <see cref="JournalBenchmarkRefusal.ConnectionVariableMissing"/> and
    /// <see cref="JournalBenchmarkRefusal.ConnectionVariableBlank"/>. Never its value.</summary>
    public string? SourceName { get; init; }
}
