using System.Data;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>Creates and widens the world table under one database-scoped application lock.</summary>
internal static class SqlServerWorldSchema
{
    internal static void Ensure(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using SqlTransaction transaction = conn.BeginTransaction();
        using SqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SET XACT_ABORT ON;
            DECLARE @granted int;
            EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
                @LockOwner = N'Transaction', @LockTimeout = @timeout;
            IF @granted < 0
                THROW 51000, N'The world store schema lock was not granted.', 1;
            """ + " " +
            "IF OBJECT_ID(N'dbo.world_store', N'U') IS NULL " +
            "CREATE TABLE dbo.world_store (" +
            "[key] NVARCHAR(450) NOT NULL PRIMARY KEY, " +
            "data VARBINARY(MAX) NOT NULL, " +
            "updated_at DATETIME2 NOT NULL, " +
            "created_at DATETIME2 NULL); " +
            "IF COL_LENGTH(N'dbo.world_store', N'created_at') IS NULL " +
            "ALTER TABLE dbo.world_store ADD created_at DATETIME2 NULL;";
        cmd.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = "KhaozEngine.WorldStore.Schema";
        cmd.Parameters.Add("@timeout", SqlDbType.Int).Value = 30_000;
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

}
