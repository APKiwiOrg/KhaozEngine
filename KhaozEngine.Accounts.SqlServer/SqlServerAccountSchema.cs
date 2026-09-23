using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Accounts.SqlServer;

/// <summary>
/// The accounts table's layout on SQL Server, the first-call bootstrap that creates or adopts it, and the
/// catalog check both schema modes end with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout is Grimhollow's</b> (<c>dbo.accounts</c>), so its database migrates in place: <c>subject
/// NVARCHAR(128)</c>, <c>display_name NVARCHAR(128)</c>, <c>whitelisted BIT</c>, <c>banned BIT</c>, <c>ban_reason
/// NVARCHAR(256)</c> and <c>ban_until DATETIMEOFFSET</c>, the widths being the <see cref="AccountStoreRules"/>
/// limits. A FRESH table differs in two places only: <c>display_name</c> is nullable, and <c>subject</c> pins
/// <c>Latin1_General_100_BIN2</c>, the <c>Commerce</c> and <c>Catalog</c> precedent, instead of inheriting a
/// database default that is usually case-insensitive. An existing table keeps its collation, which is why every
/// statement the store runs names the binary collation on the comparison as well.
/// </para>
/// <para>
/// <b>The widening is additive only</b>, guarded by <c>OBJECT_ID</c> and <c>COL_LENGTH</c>, and runs under an
/// exclusive application lock so two hosts starting at once cannot both add a column. Nothing is renamed, dropped,
/// backfilled or selected beyond the six owned columns, so a game's own column (Grimhollow's <c>debug</c>) keeps its
/// values and its default.
/// </para>
/// <para>
/// <b>The check</b> reads <c>sys.columns</c> and refuses a table missing an owned column or declaring one with a type
/// the store cannot read or a width under the limits. Its message names columns and never a row.
/// </para>
/// </remarks>
internal static class SqlServerAccountSchema
{
    /// <summary>The collation every subject comparison and ordering names.</summary>
    internal const string SubjectCollation = "Latin1_General_100_BIN2";

    /// <summary>Every column the store reads, in the order it unpacks them.</summary>
    internal const string ReadColumns = "subject, display_name, whitelisted, banned, ban_reason, ban_until";

    private const string LockResource = "KhaozEngine.Accounts.Schema";
    private const int LockTimeoutMilliseconds = 30_000;

    // Owned column, the type the store reads it as, and the least max_length in bytes (0 for a fixed type).
    private static readonly (string Name, string Type, int MinBytes)[] OwnedColumns =
    {
        ("subject", "nvarchar", AccountStoreRules.MaxSubjectChars * 2),
        ("display_name", "nvarchar", AccountStoreRules.MaxDisplayNameChars * 2),
        ("whitelisted", "bit", 0),
        ("banned", "bit", 0),
        ("ban_reason", "nvarchar", AccountStoreRules.MaxBanReasonChars * 2),
        ("ban_until", "datetimeoffset", 0),
    };

    /// <summary>
    /// Brings the table to the engine layout under <paramref name="mode"/> and checks it.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="schema">The schema name, unquoted.</param>
    /// <param name="qualified">The bracket-quoted <c>[schema].[table]</c>.</param>
    /// <param name="mode">Whether DDL is allowed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether <c>display_name</c> is <c>NOT NULL</c>, which only a legacy table declares.</returns>
    /// <exception cref="InvalidOperationException">The schema does not exist, or the table is missing under
    /// <see cref="AccountSchemaMode.ValidateOnly"/>, or it does not match the layout.</exception>
    internal static async Task<bool> EnsureAsync(SqlConnection connection, string schema, string qualified,
        AccountSchemaMode mode, CancellationToken ct)
    {
        (bool schemaExists, bool tableExists) = await ProbeAsync(connection, schema, qualified, ct).ConfigureAwait(false);
        if (!schemaExists)
            throw new InvalidOperationException(
                "The account table's schema does not exist. Create it, or name an existing one in " +
                "SqlServerAccountStoreOptions.");

        if (mode == AccountSchemaMode.AutoCreate)
        {
            // An existing table is checked BEFORE any DDL, tolerating only the ban pair the widening adds, so a table
            // the store would refuse anyway is refused as it was found rather than half widened.
            if (tableExists) await CheckAsync(connection, qualified, mode, banPairMayBeMissing: true, ct).ConfigureAwait(false);
            await CreateOrWidenAsync(connection, qualified, ct).ConfigureAwait(false);
        }
        else if (!tableExists)
        {
            throw new InvalidOperationException(
                "The account table does not exist, and ValidateOnly never creates it. Run the store once under " +
                "AutoCreate with an identity that holds DDL rights, or create the table from the package README.");
        }

        return await CheckAsync(connection, qualified, mode, banPairMayBeMissing: false, ct).ConfigureAwait(false);
    }

    private static async Task<(bool SchemaExists, bool TableExists)> ProbeAsync(SqlConnection connection,
        string schema, string qualified, CancellationToken ct)
    {
        await using SqlCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT CASE WHEN SCHEMA_ID(@schema) IS NULL THEN 0 ELSE 1 END, " +
            "CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schema;
        cmd.Parameters.Add("@table", SqlDbType.NVarChar, 261).Value = qualified;
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetInt32(0) == 1, reader.GetInt32(1) == 1);
    }

    // One batch, one transaction, one lock. The names in the DDL are the validated, bracket-quoted identifiers,
    // and every value (the lock resource, the object name the guards look up) is a bound parameter.
    private static async Task CreateOrWidenAsync(SqlConnection connection, string qualified, CancellationToken ct)
    {
        await using SqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @granted int;
            EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
                @LockOwner = N'Transaction', @LockTimeout = @timeout;
            IF @granted < 0
            BEGIN
                ROLLBACK TRANSACTION;
                THROW 51000, N'The account schema lock was not granted in time.', 1;
            END;
            IF OBJECT_ID(@table, N'U') IS NULL
                CREATE TABLE {qualified} (
                    subject NVARCHAR(128) COLLATE {SubjectCollation} NOT NULL PRIMARY KEY,
                    display_name NVARCHAR(128) NULL,
                    whitelisted BIT NOT NULL,
                    banned BIT NOT NULL,
                    ban_reason NVARCHAR(256) NULL,
                    ban_until DATETIMEOFFSET(7) NULL);
            IF COL_LENGTH(@table, N'ban_reason') IS NULL
                ALTER TABLE {qualified} ADD ban_reason NVARCHAR(256) NULL;
            IF COL_LENGTH(@table, N'ban_until') IS NULL
                ALTER TABLE {qualified} ADD ban_until DATETIMEOFFSET(7) NULL;
            COMMIT TRANSACTION;
            """;
        cmd.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
        cmd.Parameters.Add("@timeout", SqlDbType.Int).Value = LockTimeoutMilliseconds;
        cmd.Parameters.Add("@table", SqlDbType.NVarChar, 261).Value = qualified;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> CheckAsync(SqlConnection connection, string qualified, AccountSchemaMode mode,
        bool banPairMayBeMissing, CancellationToken ct)
    {
        var found = new Dictionary<string, (string Type, int MaxBytes, bool Nullable)>(StringComparer.OrdinalIgnoreCase);
        await using (SqlCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length, c.is_nullable " +
                "FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(@table, N'U');";
            cmd.Parameters.Add("@table", SqlDbType.NVarChar, 261).Value = qualified;
            await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                found[reader.GetString(0)] = (reader.GetString(1), reader.GetInt16(2), reader.GetBoolean(3));
        }

        var problems = new List<string>();
        foreach ((string name, string type, int minBytes) in OwnedColumns)
        {
            if (!found.TryGetValue(name, out (string Type, int MaxBytes, bool Nullable) column))
            {
                if (!banPairMayBeMissing || name is not ("ban_reason" or "ban_until")) problems.Add($"'{name}' is missing");
            }
            else if (!string.Equals(column.Type, type, StringComparison.OrdinalIgnoreCase))
                problems.Add($"'{name}' is {column.Type} where {type} is expected");
            else if (minBytes > 0 && column.MaxBytes != -1 && column.MaxBytes < minBytes)
                problems.Add($"'{name}' holds fewer than {minBytes / 2} characters");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "The account table does not match the engine layout: " + string.Join(", ", problems) + ". " +
                (mode == AccountSchemaMode.ValidateOnly
                    ? "ValidateOnly never alters it, so run the store once under AutoCreate with an identity that holds DDL rights."
                    : "AutoCreate adds only a missing ban column, so correct the table by hand."));

        return !found["display_name"].Nullable;
    }
}
