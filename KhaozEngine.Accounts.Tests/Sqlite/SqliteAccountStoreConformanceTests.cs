using System;
using System.Collections.Generic;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.Sqlite;

namespace KhaozEngine.Tests.Accounts.Sqlite;

/// <summary>
/// The conformance suite over a FRESH engine table in a private in-memory database, one per store. The store holds
/// its connection for its whole life, which is what keeps a <c>:memory:</c> database alive between calls. It
/// overrides nothing.
/// </summary>
public sealed class SqliteAccountStoreConformanceTests : AccountStoreConformance, IDisposable
{
    private readonly List<SqliteAccountStore> stores = new();

    /// <inheritdoc />
    protected override IAccountStore NewStore(bool whitelistOnCreate)
    {
        var store = new SqliteAccountStore("Data Source=:memory:", whitelistOnCreate);
        stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (SqliteAccountStore store in stores) store.Dispose();
    }
}

/// <summary>
/// The conformance suite over a configured table name, so every statement is proven to name the table it was
/// given rather than the default.
/// </summary>
public sealed class SqliteAccountStoreCustomTableConformanceTests : AccountStoreConformance, IDisposable
{
    private readonly List<SqliteAccountStore> stores = new();

    /// <inheritdoc />
    protected override IAccountStore NewStore(bool whitelistOnCreate)
    {
        var store = new SqliteAccountStore("Data Source=:memory:", whitelistOnCreate, new AccountTableOptions("grim_accounts_2"));
        stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (SqliteAccountStore store in stores) store.Dispose();
    }
}

/// <summary>
/// The conformance suite over a table Grimhollow already created, in a file, which the engine store adopts in
/// place. Every fact runs through the widening and through the legacy <c>display_name NOT NULL</c> mapping, so a
/// Grimhollow database is conformant from its first open.
/// </summary>
public abstract class SqliteGrimhollowLayoutConformance : AccountStoreConformance, IDisposable
{
    private readonly SqliteScratch scratch = new();

    /// <summary>The DDL that builds the legacy table before the store first opens the file.</summary>
    protected abstract string LegacyTableSql { get; }

    /// <inheritdoc />
    protected override IAccountStore NewStore(bool whitelistOnCreate)
    {
        string path = scratch.NewDatabase(LegacyTableSql);
        return scratch.Own(new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate));
    }

    public void Dispose() => scratch.Dispose();
}

/// <summary>Grimhollow's original four-column table, which the store widens with the ban pair.</summary>
public sealed class SqliteGrimhollowOriginalLayoutConformanceTests : SqliteGrimhollowLayoutConformance
{
    /// <inheritdoc />
    protected override string LegacyTableSql => GrimhollowSqliteLayout.Original;
}

/// <summary>Grimhollow's widened seven-column table with its <c>debug</c> flag, which the store adopts as it is.</summary>
public sealed class SqliteGrimhollowWidenedLayoutConformanceTests : SqliteGrimhollowLayoutConformance
{
    /// <inheritdoc />
    protected override string LegacyTableSql => GrimhollowSqliteLayout.Widened;
}
