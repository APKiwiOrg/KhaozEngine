using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public class SqliteWorldStoreEnumerationTests
{
    private static async Task<List<WorldStoreEntry>> Drain(IEnumerableWorldStore s, string? prefix = null)
    {
        var list = new List<WorldStoreEntry>();
        await foreach (WorldStoreEntry e in s.EnumerateAsync(prefix)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Enumerate_FiltersByPrefix_AndReportsSize()
    {
        using var store = new SqliteWorldStore("Data Source=:memory:");
        await store.SaveAsync("player:1", new byte[] { 1, 2, 3 });
        await store.SaveAsync("ban:bob", new byte[] { 9 });

        List<WorldStoreEntry> bans = await Drain(store, "ban:");

        Assert.Single(bans);
        Assert.Equal("ban:bob", bans[0].Key);
        Assert.Equal(1L, bans[0].Size);
        Assert.True(bans[0].UpdatedAt > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Enumerate_TreatsWildcardInPrefixAsLiteral()
    {
        using var store = new SqliteWorldStore("Data Source=:memory:");
        await store.SaveAsync("a%b", new byte[] { 1 });
        await store.SaveAsync("axb", new byte[] { 2 });

        List<WorldStoreEntry> hits = await Drain(store, "a%");

        Assert.Single(hits);
        Assert.Equal("a%b", hits[0].Key);
    }
}
