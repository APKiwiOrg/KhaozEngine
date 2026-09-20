using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The two schema modes as behaviour: <see cref="ContentAuthoringSchemaMode.AutoCreate"/> creates the schema
/// when the database is empty and then validates it, and
/// <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> refuses an empty or mismatched database rather than
/// creating anything.
/// <para>
/// <b>ValidateOnly is what a production host sets, and its job is the typo.</b> A connection string pointing
/// at a path nothing has created would otherwise create a second, empty catalog and serve it, which is a
/// catalog that reports healthy while carrying none of the operator's content.
/// </para>
/// <para>
/// Validation compares the objects <c>sqlite_master</c> actually holds against the same objects created in a
/// throwaway in-memory database from <see cref="SqliteCatalogSchema.Tables"/>, after stripping whitespace and
/// folding case, so a reformat of the DDL is not a mismatch and a changed constraint is. A mismatch names the
/// object and the required migration, because an operator can act on those two facts and cannot act on
/// "schema is wrong".
/// </para>
/// </summary>
internal static class SqliteCatalogSchemaValidation
{
    /// <summary>
    /// Brings the open connection to the current schema under one mode, or throws naming what is wrong.
    /// </summary>
    /// <param name="connection">The held connection, already open and already bootstrapped.</param>
    /// <param name="mode">Whether an empty database may be created into.</param>
    /// <exception cref="ContentAuthoringException">The schema is absent under ValidateOnly, carries a version this build does not support, or holds an object that does not match.</exception>
    internal static void Initialize(SqliteConnection connection, ContentAuthoringSchemaMode mode)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            IReadOnlyDictionary<string, string> actual = ReadSchemaObjects(connection);
            if (actual.Count == 0)
            {
                if (mode == ContentAuthoringSchemaMode.ValidateOnly)
                {
                    throw Mismatch("missing");
                }

                using (SqliteCommand create = connection.CreateCommand())
                {
                    create.CommandText = SqliteCatalogSchema.Tables;
                    create.ExecuteNonQuery();
                }

                actual = ReadSchemaObjects(connection);
            }

            long version = ReadSchemaVersion(connection);
            if (version != SqliteCatalogSchema.CurrentVersion)
            {
                throw Mismatch(FormattableString.Invariant($"at unsupported version '{version}'"));
            }

            ValidateSchemaObjects(actual);

            // The DDL declares every foreign key and SQLite enforces none of them unless this is on, so a
            // database opened with it off would accept a row pointing at a version that does not exist.
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw Mismatch("open with foreign key enforcement disabled");
            }
        }
        catch (ContentAuthoringException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw Mismatch("unreadable, so its metadata could not be checked", exception);
        }
    }

    /// <summary>The schema version the open database carries, which a migration compares against.</summary>
    /// <param name="connection">The held connection.</param>
    /// <exception cref="ContentAuthoringException">The metadata row is absent or does not hold a number.</exception>
    internal static long ReadSchemaVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;";
        object? raw = command.ExecuteScalar();
        return raw is long version
            ? version
            : throw Mismatch(raw is null or DBNull ? "missing its metadata row" : "carrying an unreadable schema version");
    }

    /// <summary>
    /// Every catalog object the database holds, keyed <c>type:name</c>, with the SQL normalized. Objects
    /// outside the <c>catalog_</c> namespace are invisible here on purpose: a host may keep its own tables in
    /// the same file, and this schema has no opinion about them.
    /// </summary>
    static IReadOnlyDictionary<string, string> ReadSchemaObjects(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();

        // The underscores are ESCAPED, because SQLite's LIKE reads a bare one as any single character. A host
        // table named catalogs or cataloguer would otherwise be counted as a catalog object, which decides
        // this method's own create-or-validate answer and then refuses the open as an unexpected object.
        command.CommandText = FormattableString.Invariant($"""
            SELECT type || ':' || name, sql
            FROM sqlite_master
            WHERE (type = 'table' AND {SqliteCatalogSchemaInventory.TableNameClause})
               OR (type = 'index' AND {SqliteCatalogSchemaInventory.IndexNameClause})
            ORDER BY type, name COLLATE BINARY;
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            objects.Add(reader.GetString(0), NormalizeSql(reader.GetString(1)));
        }

        return objects;
    }

    /// <summary>
    /// Compares what is there against the same DDL run into a throwaway database. Both directions matter: a
    /// missing object and an extra one are each a schema this build cannot write to safely.
    /// </summary>
    static void ValidateSchemaObjects(IReadOnlyDictionary<string, string> actual)
    {
        using var reference = new SqliteConnection("Data Source=:memory:");
        reference.Open();
        using (SqliteCommand create = reference.CreateCommand())
        {
            create.CommandText = SqliteCatalogSchema.Tables;
            create.ExecuteNonQuery();
        }

        IReadOnlyDictionary<string, string> expected = ReadSchemaObjects(reference);
        foreach ((string name, string expectedSql) in expected)
        {
            if (!actual.TryGetValue(name, out string? actualSql))
            {
                throw Mismatch(FormattableString.Invariant($"missing object '{name}'"));
            }

            if (!StringComparer.Ordinal.Equals(actualSql, expectedSql))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"carrying object '{name}', which does not match the shape version {SqliteCatalogSchema.CurrentVersion} declares"));
            }
        }

        foreach (string name in actual.Keys)
        {
            if (!expected.ContainsKey(name))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"carrying unexpected object '{name}', which version {SqliteCatalogSchema.CurrentVersion} does not declare"));
            }
        }
    }

    /// <summary>Whitespace out and case folded, so a reformat is not a mismatch and a constraint change is.</summary>
    static string NormalizeSql(string sql)
        => string.Concat(sql.Where(static value => !char.IsWhiteSpace(value))).ToUpperInvariant();

    /// <summary>
    /// The one refusal this file throws, naming the object and the migration. The cause is folded into the
    /// message rather than carried as an inner exception, because the reason token is what an operator's log
    /// line keys on and the refusal has to carry it whichever way it was reached.
    /// </summary>
    static ContentAuthoringException Mismatch(string actual, Exception? innerException = null)
        => new(
            FormattableString.Invariant(
                $"The SQLite content catalog schema is {actual}{Because(innerException)}. Apply migration '{SqliteCatalogSchema.RequiredMigration}'."),
            default,
            0,
            ContentAuthoringException.SchemaMismatchReason);

    static string Because(Exception? innerException)
        => innerException is null ? string.Empty : ": " + innerException.Message;
}
