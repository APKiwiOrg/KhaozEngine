using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class CellPersistenceAdmissionTests
{
    static readonly CellCoord Coord = new(0, 0);

    [Theory]
    [InlineData(CellAdmissionOutcome.Unresolved)]
    [InlineData(CellAdmissionOutcome.Refused)]
    public async Task DeferredEnvironmentRetainsBytesAndSaveFenceUntilAdmissionSucceeds(CellAdmissionOutcome refusal)
    {
        var store = new InMemoryWorldStore();
        byte[] saved = [0, 0, 0, 0];
        var seed = new Host { Snapshot = saved };
        var seeder = new CellPersistence(seed, store);
        seeder.SaveDirtyPass();
        await seeder.FlushAsync();
        byte[]? original = await store.LoadAsync("cell:0:0");
        Assert.NotNull(original);
        var host = new Host { Outcome = refusal, Snapshot = [1, 2, 3, 4] };
        var persistence = new CellPersistence(host, store);
        int applied = 0;
        persistence.CellRestoreApplied += _ => applied++;
        host.EnsureCell(Coord);
        await persistence.FlushAsync();
        Assert.True(persistence.IsBusy(Coord));
        Assert.Equal(0, applied);
        Assert.False(await persistence.SaveCellAsync(Coord, new byte[] { 0, 0, 0, 0 }));
        Assert.Equal(original, await store.LoadAsync("cell:0:0"));
        Assert.False(await store.ExistsAsync("quarantine:cell:0:0"));
        int before = host.Attempts;
        persistence.Update(0.1f);
        Assert.Equal(before + 1, host.Attempts);
        Assert.True(persistence.IsBusy(Coord));
        host.Outcome = CellAdmissionOutcome.Accepted;
        persistence.Update(0.1f);
        Assert.False(persistence.IsBusy(Coord));
        Assert.Equal(1, applied);
        Assert.Equal(saved, host.Restored);
        Assert.Equal(100, host.NextNetId);
        persistence.Update(0.1f);
        Assert.Equal(1, applied);
    }

    sealed class Host : ICellPersistenceHost
    {
        public byte[] Snapshot = [];
        public byte[]? Restored;
        public CellAdmissionOutcome Outcome = CellAdmissionOutcome.Accepted;
        public int Attempts;
        public long NextNetId { get; private set; } = 1;
        public IReadOnlyCollection<CellCoord> LiveCellCoords => new[] { Coord };
        public event Action<CellCoord>? CellCreated;
        public byte[] SnapshotCell(CellCoord coord) => Snapshot;
        public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot) => TryRestoreCell(coord, snapshot).NetIds;
        public CellRestoreResult TryRestoreCell(CellCoord coord, byte[] snapshot)
        {
            Attempts++;
            if (Outcome != CellAdmissionOutcome.Accepted) return CellRestoreResult.AwaitingAdmission(Outcome, "fixture not ready");
            Restored = snapshot;
            Snapshot = snapshot;
            return new(true, new long[] { 99 }, 0, null);
        }
        public void EnsureCell(CellCoord coord) => CellCreated?.Invoke(coord);
        public void EnsureNextNetIdAtLeast(long value) => NextNetId = Math.Max(value, NextNetId);
    }
}
