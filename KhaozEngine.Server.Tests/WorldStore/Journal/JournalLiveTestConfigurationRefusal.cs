namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>Rules that refuse the SQL Server journal live fixture before any I/O, carried by
/// <see cref="JournalLiveTestConfigurationException.Reason"/>. Test-only, so it stays off every public enum. No
/// member's identifiers carry the connection string or any part of it.</summary>
internal enum JournalLiveTestConfigurationRefusal
{
    /// <summary>Refuses a connection string that is absent, empty or whitespace. The variable it is read from is the
    /// <see cref="JournalLiveTestConfigurationException.SourceName"/>.</summary>
    ConnectionMissing,

    /// <summary>Refuses a connection whose Initial Catalog does not contain the dedicated journal test marker, which
    /// <see cref="JournalLiveTestConfigurationException.RequiredMarker"/> carries.</summary>
    DedicatedDatabaseRequired,
}
