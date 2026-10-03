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
/// The text conformance suite against <see cref="SqliteContentAuthoringStore"/>, over one FILE database and
/// one pack directory per store, so a second raw connection can arm a fault behind the store's back.
/// </summary>
public sealed class SqliteContentAuthoringTextConformanceTests : ContentAuthoringTextStoreConformance, IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kec-text-conformance-sqlite-" + Guid.NewGuid().ToString("n"));
    readonly List<SqliteContentAuthoringStore> _stores = [];
    readonly Dictionary<IContentTextAuthoringStore, string> _connectionStrings = [];

    /// <inheritdoc />
    protected override async Task<IContentTextAuthoringStore> OpenAsync(Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(_root);
        string name = _stores.Count.ToString(CultureInfo.InvariantCulture);
        string connectionString = "Data Source=" + Path.Combine(_root, "catalog-" + name + ".db");
        var store = new SqliteContentAuthoringStore(
            connectionString, TextRegistry(), new FileSystemPackStore(Path.Combine(_root, "pack-" + name)), clock);
        _stores.Add(store);
        _connectionStrings.Add(store, connectionString);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Through a TRIGGER on <c>catalog_audit</c> that refuses only a row carrying a language, so the batch's
    /// row audit lands first and the text audit fails inside the same transaction.
    /// </remarks>
    protected override IDisposable ArmATextAuditFault(IContentTextAuthoringStore store)
    {
        Execute(
            store,
            """
            CREATE TRIGGER catalog_audit_text_fault BEFORE INSERT ON catalog_audit
            WHEN NEW.language_tag IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'text audit fault');
            END;
            """);
        return new Disarm(this, store);
    }

    /// <inheritdoc />
    protected override IPackStore PackOf(IContentTextAuthoringStore store)
        => store is SqliteContentAuthoringStore { PackStore: IPackStore pack }
            ? pack
            : throw new InvalidOperationException("Every store here is opened over a pack directory of its own.");

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (SqliteContentAuthoringStore store in _stores)
        {
            store.Dispose();
        }

        SqliteConnection.ClearAllPools();
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

    void Execute(IContentTextAuthoringStore store, string sql)
    {
        using var connection = new SqliteConnection(_connectionStrings[store] + ";Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    sealed class Disarm(SqliteContentAuthoringTextConformanceTests owner, IContentTextAuthoringStore store) : IDisposable
    {
        public void Dispose() => owner.Execute(store, "DROP TRIGGER IF EXISTS catalog_audit_text_fault;");
    }
}
