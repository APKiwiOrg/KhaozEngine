using System;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>A structured refusal of the SQL Server journal live fixture's connection. Existing
/// <see cref="InvalidOperationException"/> catches remain valid. Nothing carries the connection string, its server or
/// its catalog.</summary>
internal sealed class JournalLiveTestConfigurationException : InvalidOperationException
{
    public JournalLiveTestConfigurationException(JournalLiveTestConfigurationRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the fixture.</summary>
    public JournalLiveTestConfigurationRefusal Reason { get; }

    /// <summary>The environment variable the connection string is read from, for
    /// <see cref="JournalLiveTestConfigurationRefusal.ConnectionMissing"/>. Never its value.</summary>
    public string? SourceName { get; init; }

    /// <summary>The text the Initial Catalog must contain, for
    /// <see cref="JournalLiveTestConfigurationRefusal.DedicatedDatabaseRequired"/>. A code literal, never the
    /// configured catalog.</summary>
    public string? RequiredMarker { get; init; }
}
