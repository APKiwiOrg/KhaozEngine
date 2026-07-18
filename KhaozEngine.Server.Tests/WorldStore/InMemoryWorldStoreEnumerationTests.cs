using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public class InMemoryWorldStoreEnumerationTests
{
    private static async Task<List<WorldStoreEntry>> Drain(IEnumerableWorldStore s, string? prefix = null)
    {
        var list = new List<WorldStoreEntry>();
        await foreach (WorldStoreEntry e in s.EnumerateAsync(prefix)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Enumerate_ReturnsAllEntries_WithSizeAndTimestamp()
    {
        var when = new DateTimeOffset(2026, 6, 29, 0, 0, 0, TimeSpan.Zero);
        var store = new InMemoryWorldStore(() => when);
        await store.SaveAsync("player:1", Encoding.UTF8.GetBytes("abc"));
        await store.SaveAsync("player:2", Encoding.UTF8.GetBytes("de"));

        List<WorldStoreEntry> all = await Drain(store);

        Assert.Equal(2, all.Count);
        WorldStoreEntry p1 = all.Single(e => e.Key == "player:1");
        Assert.Equal(3L, p1.Size);
        Assert.Equal(when, p1.UpdatedAt);
    }

    [Fact]
    public async Task Enumerate_FiltersByPrefix()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:1", new byte[] { 1 });
        await store.SaveAsync("ban:bob", new byte[] { 2 });

        List<WorldStoreEntry> bans = await Drain(store, "ban:");

        Assert.Single(bans);
        Assert.Equal("ban:bob", bans[0].Key);
    }
}
