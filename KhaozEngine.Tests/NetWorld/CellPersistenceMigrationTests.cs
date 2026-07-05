using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class CellPersistenceMigrationTests
{
    private static readonly CellCoord C00 = new(0, 0);

    // A dictionary-backed host. TryRestoreCell models the real CellSim contract: a rolled-back failure applies
    // nothing (does not record), a success records the body it was handed and reports the retained-frame count.
    private sealed class FakeHost : ICellPersistenceHost
    {
        public readonly Dictionary<CellCoord, byte[]> Snapshots = new();
        public readonly Dictionary<CellCoord, byte[]> Restored = new();
        public readonly Dictionary<CellCoord, List<long>> RestoreIds = new();
        public bool FailRestore;
        public int RetainedCount;
        public long NextNetId { get; private set; } = 1;
        public event Action<CellCoord>? CellCreated;

        public IReadOnlyCollection<CellCoord> LiveCellCoords => new List<CellCoord>(Snapshots.Keys);
        public byte[]? SnapshotCell(CellCoord coord) => Snapshots.TryGetValue(coord, out byte[]? b) ? b : null;

        public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot)
        {
            Restored[coord] = snapshot;
            return RestoreIds.TryGetValue(coord, out List<long>? ids) ? ids : new List<long>();
        }

        public CellRestoreResult TryRestoreCell(CellCoord coord, byte[] snapshot)
        {
            if (FailRestore) return CellRestoreResult.Failed("decode failed");
            Restored[coord] = snapshot;
            List<long> ids = RestoreIds.TryGetValue(coord, out List<long>? l) ? l : new List<long>();
            return new CellRestoreResult(true, ids, RetainedCount, null);
        }

        public void EnsureCell(CellCoord coord) => CellCreated?.Invoke(coord);
        public void EnsureNextNetIdAtLeast(long atLeast) { if (atLeast > NextNetId) NextNetId = atLeast; }
        public void RaiseCellCreated(CellCoord coord) => CellCreated?.Invoke(coord);
    }

    // Writes a wrapped [magic][schemaVersion][body] blob for a cell using the persistence's own save path.
    private static async Task SeedBlob(InMemoryWorldStore store, CellCoord coord, int schemaVersion, byte[] body)
    {
        var seedHost = new FakeHost();
        seedHost.Snapshots[coord] = body;
        // Opt out of engine migrations here: this only WRITES a blob stamped at schemaVersion, and folding in the
        // engine widening would (e.g. at v5) leave a gap in the chain and throw at construction.
        var seeder = new CellPersistence(seedHost, store,
            new CellPersistenceConfig { SchemaVersion = schemaVersion, IncludeEngineMigrations = false });
        seeder.SaveDirtyPass();
        await seeder.FlushAsync();
    }

    private static byte[] Append(byte[] b, byte marker)
    {
        var o = new byte[b.Length + 1];
        b.CopyTo(o, 0);
        o[^1] = marker;
        return o;
    }

    [Fact]
    public async Task Load_MigratesPreviousVersionBlob_AndRestoresMigratedBody()
    {
        var store = new InMemoryWorldStore();
        byte[] v1 = { 0, 0, 0, 0 };
        await SeedBlob(store, C00, 1, v1);

        var cfg = new CellPersistenceConfig { SchemaVersion = 2 };
        cfg.RegisterMigration(1, b => Append(b, 0xAB));
        var host = new FakeHost();
        var cp = new CellPersistence(host, store, cfg);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.Equal(Append(v1, 0xAB), host.Restored[C00]);
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.Migrated && i.FromVersion == 1 && i.ToVersion == 2);
    }

    [Fact]
    public async Task Load_ComposesTwoStepMigrationChain()
    {
        var store = new InMemoryWorldStore();
        byte[] v1 = { 1 };
        await SeedBlob(store, C00, 1, v1);

        var cfg = new CellPersistenceConfig { SchemaVersion = 3 };
        cfg.RegisterMigration(1, b => Append(b, 2)).RegisterMigration(2, b => Append(b, 3));
        var host = new FakeHost();
        var cp = new CellPersistence(host, store, cfg);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.Equal(Append(Append(v1, 2), 3), host.Restored[C00]);
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.Migrated && i.FromVersion == 1 && i.ToVersion == 3);
    }

    [Fact]
    public void Ctor_MigrationChainGap_Throws()
    {
        var cfg = new CellPersistenceConfig { SchemaVersion = 3 };
        cfg.RegisterMigration(1, b => b);   // 2 -> 3 missing
        Assert.Throws<ArgumentException>(() => new CellPersistence(new FakeHost(), new InMemoryWorldStore(), cfg));
    }

    [Fact]
    public void Ctor_MigrationStepAtOrBeyondSchemaVersion_Throws()
    {
        var cfg = new CellPersistenceConfig { SchemaVersion = 2 };
        cfg.RegisterMigration(2, b => b);   // targets v3, at/beyond schema 2
        Assert.Throws<ArgumentException>(() => new CellPersistence(new FakeHost(), new InMemoryWorldStore(), cfg));
    }

    [Fact]
    public async Task CorruptHeaderBlob_Quarantines_PreservesBytes_KeepsTicking()
    {
        var store = new InMemoryWorldStore();
        byte[] garbage = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };   // bad magic
        await store.SaveAsync("cell:0:0", garbage);
        var host = new FakeHost();
        var cp = new CellPersistence(host, store);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.False(host.Restored.ContainsKey(C00));
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);
        Assert.Equal(garbage, await store.LoadAsync("quarantine:cell:0:0"));
        for (int i = 0; i < 3; i++) cp.Update(100f);   // poisoned key does not crash-loop
    }

    [Fact]
    public async Task MigrationThrows_Quarantines_PreservesOriginalBytes()
    {
        var store = new InMemoryWorldStore();
        await SeedBlob(store, C00, 1, new byte[] { 9, 9, 9, 9 });
        byte[]? orig = await store.LoadAsync("cell:0:0");

        var cfg = new CellPersistenceConfig { SchemaVersion = 2 };
        cfg.RegisterMigration(1, _ => throw new InvalidOperationException("boom"));
        var host = new FakeHost();
        var cp = new CellPersistence(host, store, cfg);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.False(host.Restored.ContainsKey(C00));
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);
        Assert.Equal(orig, await store.LoadAsync("quarantine:cell:0:0"));
    }

    [Fact]
    public async Task Load_BlobOlderThanEarliestMigration_SkippedTooOld_AndPreserved()
    {
        var store = new InMemoryWorldStore();
        await SeedBlob(store, C00, 1, new byte[] { 0, 0, 0, 0 });
        byte[]? orig = await store.LoadAsync("cell:0:0");

        // Opt out of engine migrations so v2 is genuinely the earliest step (else the engine widening from v1 would
        // make a v1 blob upgradable, not "too old").
        var cfg = new CellPersistenceConfig { SchemaVersion = 3, IncludeEngineMigrations = false };
        cfg.RegisterMigration(2, b => b);   // earliest migration from v2; a v1 blob predates it
        var host = new FakeHost();
        var cp = new CellPersistence(host, store, cfg);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.False(host.Restored.ContainsKey(C00));
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.SkippedTooOld && i.FromVersion == 1);
        Assert.Equal(orig, await store.LoadAsync("quarantine:cell:0:0"));
    }

    [Fact]
    public async Task Load_NewerThanSchema_SkippedTooNew_AndPreserved()
    {
        var store = new InMemoryWorldStore();
        await SeedBlob(store, C00, 5, new byte[] { 0, 0, 0, 0 });   // stored at v5
        byte[]? orig = await store.LoadAsync("cell:0:0");

        var host = new FakeHost();
        var cp = new CellPersistence(host, store, new CellPersistenceConfig { SchemaVersion = 2 });   // build only knows v2
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.False(host.Restored.ContainsKey(C00));
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.SkippedTooNew && i.FromVersion == 5);
        Assert.Equal(orig, await store.LoadAsync("quarantine:cell:0:0"));
    }

    [Fact]
    public async Task Load_CurrentVersionBlob_RestoresByteIdentically_NoMigration()
    {
        var store = new InMemoryWorldStore();
        byte[] body = { 0, 0, 0, 0 };
        await SeedBlob(store, C00, NetIdBlobMigration.NetId64SchemaVersion, body);   // stored at the current version (2)
        var host = new FakeHost();
        var cp = new CellPersistence(host, store);   // default SchemaVersion == the current version (2) == stored
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.Equal(body, host.Restored[C00]);   // byte-identical: no reader/writer round trip
        Assert.DoesNotContain(issues, i => i.Kind == CellPersistenceIssueKind.Migrated);
        Assert.DoesNotContain(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);
    }

    [Fact]
    public async Task Load_RetainedUnknownExtensions_SurfacesEvent()
    {
        var store = new InMemoryWorldStore();
        await SeedBlob(store, C00, 1, new byte[] { 0, 0, 0, 0 });
        var host = new FakeHost { RetainedCount = 2 };
        var cp = new CellPersistence(host, store);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.RetainedUnknownExtensions && i.RetainedFrameCount == 2);
    }

    [Fact]
    public async Task Load_RestoreDecodeFailure_Quarantines_NotRestored()
    {
        var store = new InMemoryWorldStore();
        await SeedBlob(store, C00, 1, new byte[] { 0, 0, 0, 0 });
        byte[]? orig = await store.LoadAsync("cell:0:0");
        var host = new FakeHost { FailRestore = true };
        var cp = new CellPersistence(host, store);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();

        Assert.False(host.Restored.ContainsKey(C00));
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);
        Assert.Equal(orig, await store.LoadAsync("quarantine:cell:0:0"));
    }
}
