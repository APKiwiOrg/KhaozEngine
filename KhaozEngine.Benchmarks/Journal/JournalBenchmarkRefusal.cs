namespace KhaozEngine.Benchmarks.Journal;

/// <summary>Rules that refuse a SQL Server journal benchmark before any I/O, carried by
/// <see cref="JournalBenchmarkConfigurationException.Reason"/> and <see cref="JournalBenchmarkArgumentException.Reason"/>.
/// No member's identifiers carry the connection string or any part of it.</summary>
public enum JournalBenchmarkRefusal
{
    /// <summary>Refuses a run whose named connection variable is not set. The variable's name is the
    /// <see cref="JournalBenchmarkConfigurationException.SourceName"/>.</summary>
    ConnectionVariableMissing,

    /// <summary>Refuses a run whose named connection variable is set but empty or whitespace. The variable's name is
    /// the <see cref="JournalBenchmarkConfigurationException.SourceName"/>.</summary>
    ConnectionVariableBlank,

    /// <summary>Refuses a connection whose Initial Catalog does not contain the dedicated benchmark marker, which
    /// <see cref="JournalBenchmarkArgumentException.RequiredMarker"/> carries, so a benchmark never writes to a shared
    /// or production database.</summary>
    DedicatedDatabaseRequired,
}
