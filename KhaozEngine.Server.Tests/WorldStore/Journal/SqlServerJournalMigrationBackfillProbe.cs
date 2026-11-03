using System;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The journal validator inventories every trigger on its tables. The serialized race fact declares only its
/// test probe for the probe's lifetime, then restores the original trigger expectations.
/// </summary>
internal sealed class SqlServerJournalMigrationBackfillProbe : IDisposable
{
    private readonly IDisposable? triggerRegistration;
    private readonly SqlServerMigrationBackfillProbe probe;

    internal SqlServerJournalMigrationBackfillProbe(string connectionString)
    {
        probe = new SqlServerMigrationBackfillProbe(connectionString, "journal_stream", "stream_key = N'legacy/a'");
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using SqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(@trigger));";
            command.Parameters.AddWithValue("@trigger", "dbo." + probe.TriggerName);
            string definition = (string)command.ExecuteScalar()!;
            triggerRegistration = SqlServerJournalSchema.RegisterTriggerForTests(
                probe.TriggerName, "journal_stream", definition);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int Updates => probe.Updates;

    public void Dispose()
    {
        try
        {
            probe.Dispose();
        }
        finally
        {
            triggerRegistration?.Dispose();
        }
    }
}
