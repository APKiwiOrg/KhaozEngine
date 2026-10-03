using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The conformance suite against <see cref="SqliteContentAuthoringStore"/>, over a per-test in-memory
/// database with a SHARED cache, which is the idiom <c>SqliteWalletStoreTests</c> established: xUnit news a
/// fresh class instance per fact, so each fact gets its own uniquely named database and no cross-test state
/// can leak.
/// <para>
/// Shared cache rather than a plain <c>:memory:</c> because two of the facts need a SECOND connection into the
/// same database: fact 17 installs a trigger that makes the audit insert fail, and fact 22 reads the pointer
/// and the version row while a publish is committing. A private in-memory database is reachable only from the
/// connection that created it.
/// </para>
/// </summary>
public sealed class SqliteContentAuthoringStoreConformanceTests : ContentAuthoringStoreConformance, IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kec-conformance-sqlite-" + Guid.NewGuid().ToString("n"));
    readonly List<SqliteContentAuthoringStore> _stores = [];
    readonly Dictionary<IContentAuthoringStore, string> _connectionStrings = [];
    int _databases;

    /// <inheritdoc />
    protected override IContentAuthoringStore NewStore()
    {
        _databases++;
        string connectionString = FormattableString.Invariant(
            $"Data Source=catalog_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        var store = new SqliteContentAuthoringStore(connectionString, Registry, NewPack());
        _stores.Add(store);
        _connectionStrings.Add(store, connectionString);
        return store;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A NEW in-memory database rather than a drop of this one, which is the same journey and cheaper: the
    /// old store stays open until the class is disposed, so the two are genuinely separate databases with
    /// separate epochs, which is what the bundle facts are about.
    /// </remarks>
    protected override Task<IContentAuthoringStore> ResetToEmptyAsync() => OpenAsync();

    /// <inheritdoc />
    /// <remarks>
    /// <b>Through a TRIGGER on <c>catalog_audit</c>, installed on a second connection.</b> It needs no
    /// test-only member on the store and it fails the insert exactly where a real one would fail, inside the
    /// edit's own transaction, so what the store does next is the behaviour under test rather than a
    /// simulation of it.
    /// </remarks>
    protected override IDisposable ArmAnAuditWriteFault(IContentAuthoringStore store)
    {
        Execute(
            store,
            """
            CREATE TRIGGER catalog_audit_fault BEFORE INSERT ON catalog_audit
            BEGIN
                SELECT RAISE(ABORT, 'audit fault');
            END;
            """);
        return new Disarm(this, store);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One statement on a second connection, so both halves come out of one read. A shared-cache reader hits
    /// the writer's table lock rather than blocking on it, which arrives as an exception and is reported as no
    /// look at all: a lock is not an inconsistency.
    /// </remarks>
    protected override async Task<CatalogPointerLook?> LookAsync(IContentAuthoringStore store, int versionNumber)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionStrings[store]);
            await connection.OpenAsync().ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT (SELECT active_version FROM catalog_metadata WHERE metadata_key = 1),
                       (SELECT COUNT(*) FROM catalog_version WHERE version_number = $version);
                """;
            command.Parameters.AddWithValue("$version", versionNumber);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }

            return new CatalogPointerLook(reader.GetInt32(0), reader.GetInt32(1) == 1);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override IPackStore PackOf(IContentAuthoringStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store is SqliteContentAuthoringStore { PackStore: IPackStore pack }
            ? pack
            : throw new InvalidOperationException(
                "This subclass opens every store over a pack directory of its own, so one without a pack target did not come from here.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (SqliteContentAuthoringStore store in _stores)
        {
            store.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }

    void Execute(IContentAuthoringStore store, string sql)
    {
        using var connection = new SqliteConnection(_connectionStrings[store]);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    FileSystemPackStore NewPack()
        => new(Path.Combine(_root, "pack-" + _databases.ToString(CultureInfo.InvariantCulture)));

    sealed class Disarm(SqliteContentAuthoringStoreConformanceTests owner, IContentAuthoringStore store) : IDisposable
    {
        public void Dispose() => owner.Execute(store, "DROP TRIGGER IF EXISTS catalog_audit_fault;");
    }
}
