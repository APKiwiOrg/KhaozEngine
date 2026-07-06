using System;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.Sqlite;

namespace KhaozEngine.Tests.Commerce;

public sealed class SqliteWalletStoreTests : WalletStoreContract, IDisposable
{
    // Shared in-memory SQLite kept alive by one open connection for the test's lifetime.
    // xUnit news a fresh class instance per [Fact], so each test gets its own uniquely named
    // in-memory database: no cross-test state leakage.
    private readonly SqliteWalletStore store = new($"Data Source=commerce_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");

    protected override IWalletStore NewStore() => store;

    public void Dispose() => store.Dispose();
}
