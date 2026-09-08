using System;
using System.Data;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public sealed class SqlServerJournalMetadataTests
{
    [Theory]
    [InlineData("ck_journal_metadata_version", 1)]
    [InlineData("df_journal_operation_retention", 3)]
    [InlineData("trg_journal_operation_delete_guard", 2)]
    public void Hidden_definition_reports_a_whole_store_schema_failure(string name, int ordinal)
    {
        using DataTable table = MetadataRow(ordinal, DBNull.Value);
        using DataTableReader reader = table.CreateDataReader();
        Assert.True(reader.Read());

        JournalStoreException error = Assert.Throws<JournalStoreException>(
            () => SqlServerJournalMetadata.ReadDefinition(reader, ordinal, name));

        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, error.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, error.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, error.Scope);
        Assert.Empty(error.StreamKeys);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Contains("VIEW DEFINITION", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("([schema_version]=(2))", 1)]
    [InlineData("(todatetimeoffset(sysutcdatetime(),'+00:00'))", 3)]
    [InlineData("CREATE TRIGGER dbo.trg_journal_operation_delete_guard", 2)]
    public void Visible_definition_is_preserved_for_shape_validation(string definition, int ordinal)
    {
        using DataTable table = MetadataRow(ordinal, definition);
        using DataTableReader reader = table.CreateDataReader();
        Assert.True(reader.Read());

        Assert.Equal(definition, SqlServerJournalMetadata.ReadDefinition(reader, ordinal, "object"));
    }

    private static DataTable MetadataRow(int ordinal, object definition)
    {
        var table = new DataTable();
        for (int index = 0; index <= ordinal; index++) table.Columns.Add("column" + index, typeof(string));
        DataRow row = table.NewRow();
        row[ordinal] = definition;
        table.Rows.Add(row);
        return table;
    }
}
