using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The journal validator inventories every trigger on its tables. The serialized race fact declares only its
/// test probe for the probe's lifetime, then restores the original trigger expectations.
/// </summary>
internal sealed class SqlServerJournalMigrationBackfillProbe : IDisposable
{
    private readonly HashSet<string> expectedTriggers;
    private readonly HashSet<string> originalTriggers;
    private readonly SqlServerMigrationBackfillProbe probe;

    internal SqlServerJournalMigrationBackfillProbe(string connectionString)
    {
        const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        FieldInfo field = typeof(SqlServerJournalSchema).GetField("ExpectedTriggers", PrivateStatic)
            ?? throw new InvalidOperationException("The journal trigger expectation set is missing.");
        expectedTriggers = (HashSet<string>)field.GetValue(null)!;
        originalTriggers = new HashSet<string>(expectedTriggers, StringComparer.Ordinal);
        probe = new SqlServerMigrationBackfillProbe(connectionString, "journal_stream", "stream_key = N'legacy/a'");
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using SqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(@trigger));";
            command.Parameters.AddWithValue("@trigger", "dbo." + probe.TriggerName);
            string definition = (string)command.ExecuteScalar()!;
            MethodInfo normalize = typeof(SqlServerJournalSchema).GetMethod("Trigger", PrivateStatic)
                ?? throw new InvalidOperationException("The journal trigger normalization is missing.");
            string signature = (string)normalize.Invoke(null, [probe.TriggerName, "journal_stream", definition, false])!;
            Assert.True(expectedTriggers.Add(signature), "The probe must add only its own trigger expectation.");
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
            expectedTriggers.Clear();
            expectedTriggers.UnionWith(originalTriggers);
        }
    }
}
