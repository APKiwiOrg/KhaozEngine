using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Sqlite;

/// <summary>
/// A live store's connection stays out of the provider's pool, so no pool operation on the same file can lend its
/// native handle to a second owner or close it underneath the store.
/// <para>
/// <b>The window these two facts hold open is the provider's, not ours.</b> In Microsoft.Data.Sqlite 10.0.9,
/// <c>SqliteConnectionInternal.Activate</c> marks a pooled connection active BEFORE it records the owning
/// <c>SqliteConnection</c>, and it does so after the pool's lock is released. A checkout or a clear that scans the
/// pool in that gap reads the connection as leaked and reclaims it. A reclaim into a live pool lends the handle to
/// the next opener, so two stores run statements on one native connection ("cannot start a transaction within a
/// transaction", and a rollback hook freed by one thread while the other's ROLLBACK is calling it). A reclaim during
/// a clear disposes the handle, so the store's next statement throws <see cref="ObjectDisposedException"/>. Upstream
/// fixed the order in dotnet/efcore#39009, and the 10.0.9 package this repository pins does not have that fix.
/// </para>
/// <para>
/// The gap is two instructions wide, so a loop finds it about once in twenty thousand opens. These facts hold it
/// open instead: they put a live store's connection into exactly the state <c>Activate</c> passes through, run the
/// competing operation, then complete the activation. The provider fields are reached by reflection and a missing
/// one fails the fact rather than skipping it, so a provider that renames them cannot turn this into a pass.
/// </para>
/// </summary>
public sealed class SqliteStoreConnectionPoolIsolationTests
{
    /// <summary>
    /// The competitor opens on the string the caller handed the store, and then on the string the held connection
    /// actually carries. The second is the pool the held connection would be checked out of if it had one, so a fix
    /// that only moved the store to a different pool key still fails it.
    /// </summary>
    public static TheoryData<bool> CompetitorStrings => new() { false, true };

    [Theory]
    [MemberData(nameof(CompetitorStrings))]
    public async Task A_checkout_in_the_activation_window_never_lends_a_live_store_handle(bool heldString)
    {
        string path = TempPath();
        try
        {
            using var store = new SqliteStoreConnection("Data Source=" + path, SqliteStoreConnectionFileLifetimeTests.Bootstrap);
            string competitor = heldString ? store.Connection.ConnectionString : "Data Source=" + path;
            var first = new SqliteConnection(competitor);
            var second = new SqliteConnection(competitor);
            try
            {
                ActivationWindow.Hold(store.Connection, () =>
                {
                    first.Open();
                    second.Open();
                });

                IntPtr held = store.Connection.Handle!.DangerousGetHandle();
                Assert.NotEqual(held, first.Handle!.DangerousGetHandle());
                Assert.NotEqual(held, second.Handle!.DangerousGetHandle());
                Assert.True(await HasProbeTableAsync(store));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                SqliteConnection.ClearPool(first);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(CompetitorStrings))]
    public async Task A_pool_clear_in_the_activation_window_leaves_a_live_store_open(bool heldString)
    {
        string path = TempPath();
        try
        {
            using var store = new SqliteStoreConnection("Data Source=" + path, SqliteStoreConnectionFileLifetimeTests.Bootstrap);
            string competitor = heldString ? store.Connection.ConnectionString : "Data Source=" + path;
            using (var other = new SqliteConnection(competitor))
            {
                other.Open();
                ActivationWindow.Hold(store.Connection, () => SqliteConnection.ClearPool(other));
            }

            Assert.True(await HasProbeTableAsync(store));
        }
        finally { File.Delete(path); }
    }

    static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ke-sqlite-pool-isolation-" + Guid.NewGuid().ToString("N") + ".db");

    static async Task<bool> HasProbeTableAsync(SqliteStoreConnection store)
    {
        using SqliteStoreLease _ = await store.EnterAsync();
        using SqliteCommand cmd = store.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'probe';";
        return await cmd.ExecuteScalarAsync() is "probe";
    }

    /// <summary>
    /// Holds an open connection in the provider's mid-activation state: active, with its owner not yet recorded.
    /// </summary>
    static class ActivationWindow
    {
        const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Hold(SqliteConnection connection, Action competitor)
        {
            object inner = Field(typeof(SqliteConnection), "_innerConnection").GetValue(connection)
                ?? throw new InvalidOperationException("The connection is not open.");
            var owner = (WeakReference<SqliteConnection?>)Field(inner.GetType(), "_outerConnection").GetValue(inner)!;
            owner.SetTarget(null);
            try
            {
                competitor();
            }
            finally
            {
                owner.SetTarget(connection);
            }
        }

        static FieldInfo Field(Type type, string name) =>
            type.GetField(name, Instance)
            ?? throw new InvalidOperationException(
                $"Microsoft.Data.Sqlite no longer has {type.Name}.{name}, so the activation window cannot be held.");
    }
}
