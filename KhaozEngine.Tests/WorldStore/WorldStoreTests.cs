using System;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>The shared conformance suite against the dependency-free in-memory backend.</summary>
public class InMemoryWorldStoreConformanceTests
{
    private static IWorldStore New() => new InMemoryWorldStore();
    private static string Ns() => Guid.NewGuid().ToString("N");

    [Fact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(New(), Ns());
    [Fact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(New(), Ns());
    [Fact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(New(), Ns());
    [Fact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(New(), Ns());
    [Fact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(New(), Ns());
    [Fact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(New(), Ns());
    [Fact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(New(), Ns());
    [Fact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(New(), Ns());

    [Fact]
    public async Task Load_ReturnsIndependentCopy()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("k", new byte[] { 1, 2, 3 });
        byte[] first = (await store.LoadAsync("k"))!;
        first[0] = 99;                                  // mutate the returned array
        byte[] second = (await store.LoadAsync("k"))!;
        Assert.Equal(new byte[] { 1, 2, 3 }, second);   // stored state unaffected
    }
}
