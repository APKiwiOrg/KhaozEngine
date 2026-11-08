using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>Complete content and column shapes of every journal and host table or view, across schemas.</summary>
internal static class SqlServerJournalSnapshot
{
    internal static async Task<string> CaptureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var objects = new List<(string Schema, string Name)>();
        await using (SqlCommand list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT s.name, o.name FROM sys.objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.type IN (N'U', N'V') AND o.is_ms_shipped = 0
                ORDER BY s.name, o.name;
                """;
            await using SqlDataReader reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync()) objects.Add((reader.GetString(0), reader.GetString(1)));
        }
        var snapshot = new List<string>();
        foreach ((string schema, string name) in objects)
        {
            await using SqlCommand read = connection.CreateCommand();
            read.CommandText = "SELECT * FROM [" + schema.Replace("]", "]]", StringComparison.Ordinal)
                + "].[" + name.Replace("]", "]]", StringComparison.Ordinal) + "];";
            await using SqlDataReader reader = await read.ExecuteReaderAsync();
            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i) + ":" + reader.GetDataTypeName(i));
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal);
            snapshot.Add(JsonSerializer.Serialize(new { Schema = schema, Name = name, Columns = columns, Rows = rows }));
        }
        return JsonSerializer.Serialize(snapshot);
    }
}
