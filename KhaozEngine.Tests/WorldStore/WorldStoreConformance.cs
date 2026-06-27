using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// The single shared <see cref="IWorldStore"/> contract suite. Run against every backend (InMemory, SQLite
/// always; SQL Server gated). Every key is prefixed with <c>ns</c> so a shared-database backend can isolate a
/// run by passing a fresh namespace; the in-memory/file backends get a fresh store per test and ignore it.
/// </summary>
internal static class WorldStoreConformance
{
    public static async Task SaveLoad_RoundTrips(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "k", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await s.LoadAsync(ns + "k"));
    }

    public static async Task Save_Overwrites(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "k", new byte[] { 1 });
        await s.SaveAsync(ns + "k", new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 9, 9 }, await s.LoadAsync(ns + "k"));
    }

    public static async Task Load_Absent_ReturnsNull(IWorldStore s, string ns)
        => Assert.Null(await s.LoadAsync(ns + "missing"));

    public static async Task Delete_PresentThenAbsent(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "k", new byte[] { 1 });
        Assert.True(await s.DeleteAsync(ns + "k"));     // present -> removed
        Assert.False(await s.DeleteAsync(ns + "k"));    // already gone
    }

    public static async Task Exists_TracksPresence(IWorldStore s, string ns)
    {
        Assert.False(await s.ExistsAsync(ns + "k"));
        await s.SaveAsync(ns + "k", new byte[] { 1 });
        Assert.True(await s.ExistsAsync(ns + "k"));
        await s.DeleteAsync(ns + "k");
        Assert.False(await s.ExistsAsync(ns + "k"));
    }

    public static async Task Keys_AreIsolated(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "a", new byte[] { 1 });
        await s.SaveAsync(ns + "b", new byte[] { 2 });
        await s.DeleteAsync(ns + "a");
        Assert.Null(await s.LoadAsync(ns + "a"));
        Assert.Equal(new byte[] { 2 }, await s.LoadAsync(ns + "b"));   // b untouched
    }

    public static async Task Bytes_AreExact(IWorldStore s, string ns)
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;   // every byte value incl 0x00
        await s.SaveAsync(ns + "blob", data);
        Assert.Equal(data, await s.LoadAsync(ns + "blob"));
    }

    public static async Task Concurrent_DistinctKeys(IWorldStore s, string ns)
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            int n = i;
            tasks.Add(s.SaveAsync(ns + "k" + n, new byte[] { (byte)n }));
        }
        await Task.WhenAll(tasks);
        for (int i = 0; i < 50; i++)
            Assert.Equal(new byte[] { (byte)i }, await s.LoadAsync(ns + "k" + i));
    }
}
