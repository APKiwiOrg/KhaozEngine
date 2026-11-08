using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>Complete row content for catalog tables and views and the fixture's host objects.</summary>
internal static class SqlServerCatalogSnapshot
{
    internal static string Read(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var objects = new List<string>();
        using (DbCommand list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT name FROM sys.objects
                WHERE schema_id = SCHEMA_ID(N'dbo') AND type IN (N'U', N'V')
                  AND (name LIKE N'catalog[_]%' OR name LIKE N'host[_]%')
                ORDER BY name;
                """;
            using DbDataReader reader = list.ExecuteReader();
            while (reader.Read()) objects.Add(reader.GetString(0));
        }
        var snapshot = new List<string>();
        foreach (string name in objects)
        {
            using DbCommand read = connection.CreateCommand();
            read.CommandText = "SELECT * FROM dbo.[" + name.Replace("]", "]]", StringComparison.Ordinal) + "];";
            using DbDataReader reader = read.ExecuteReader();
            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i) + ":" + reader.GetDataTypeName(i));
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal);
            snapshot.Add(JsonSerializer.Serialize(new { Name = name, Columns = columns, Rows = rows }));
        }
        return JsonSerializer.Serialize(snapshot);
    }
}
