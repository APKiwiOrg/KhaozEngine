using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>
/// A cell-blob schema migration: takes the raw snapshot BODY (the bytes after the 8-byte header) at one schema
/// version and returns the body rewritten to the next version. Author it with
/// <see cref="KhaozEngine.Replication.SnapshotBlobReader"/> / <see cref="KhaozEngine.Replication.SnapshotBlobWriter"/>
/// to map / drop / transform per-component payloads without hand-parsing. It must do ONLY the data change; the driver
/// stamps the schema version. Engine-owned built-in layout changes ship engine-provided migrations; consumer
/// extension changes ship consumer migrations. May throw on a genuinely undecodable body: the driver quarantines that
/// blob rather than crashing.
/// </summary>
public delegate byte[] CellSnapshotMigration(byte[] snapshotBody);

/// <summary>Tunables for <see cref="CellPersistence"/>.</summary>
public sealed class CellPersistenceConfig
{
    private readonly SortedDictionary<int, CellSnapshotMigration> migrations = new();

    /// <summary>How often the periodic snapshot saves dirty cells, seconds. A crash loses at most this much.</summary>
    public float SaveIntervalSeconds { get; init; } = 30f;

    /// <summary>Key namespace for cell records. Stored key is <c>{CellKeyPrefix}{x}:{y}</c>.</summary>
    public string CellKeyPrefix { get; init; } = "cell:";

    /// <summary>Key of the world-scope meta record (the NetId high-water mark).</summary>
    public string MetaKey { get; init; } = "world:meta";

    /// <summary>Key namespace for quarantined (undecodable) cell blobs. A poisoned blob's original bytes are copied
    /// here (<c>{QuarantineKeyPrefix}{CellKeyPrefix}{x}:{y}</c>) before the cell starts fresh, so nothing is destroyed
    /// and an operator can recover them out of band.</summary>
    public string QuarantineKeyPrefix { get; init; } = "quarantine:";

    /// <summary>Blob schema version. Bump on a component-layout change and register a <see cref="RegisterMigration"/>
    /// from the previous version so old saves are brought forward, not skipped or misread.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Registers the migration that takes a stored blob from <paramref name="fromVersion"/> to
    /// <paramref name="fromVersion"/> + 1. Registrations across a chain must be contiguous with no gaps and none may
    /// target at or beyond <see cref="SchemaVersion"/>; the chain is validated when the <see cref="CellPersistence"/>
    /// is constructed (mirroring <c>MigrationChain</c>). Returns this config for chaining. A duplicate
    /// <paramref name="fromVersion"/> throws immediately.
    /// </summary>
    public CellPersistenceConfig RegisterMigration(int fromVersion, CellSnapshotMigration migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        if (migrations.ContainsKey(fromVersion))
            throw new ArgumentException($"A cell-blob migration from version {fromVersion} is already registered.", nameof(fromVersion));
        migrations.Add(fromVersion, migrate);
        return this;
    }

    /// <summary>The registered migrations keyed by from-version, ascending. Consumed by <see cref="CellPersistence"/>.</summary>
    internal IReadOnlyDictionary<int, CellSnapshotMigration> Migrations => migrations;
}

/// <summary>
/// Wires an <see cref="IWorldStore"/> into a <see cref="ShardHost"/>-based server (via
/// <see cref="ICellPersistenceHost"/>) so a cell's authoritative non-player entities survive a restart. Mirrors
/// <see cref="WorldPersistence"/> but keyed by cell coordinate: lazy load-on-cell-create, periodic dirty snapshot
/// of changed cells, and a NetId high-water record so restored entities never collide with fresh spawns. Async
/// loads are applied on the server thread inside <see cref="Update"/> (never from a background continuation).
/// </summary>
public sealed class CellPersistence
{
    // Header: [int32 magic][int32 schemaVersion] then the raw Replication snapshot.
    private const int Magic = 0x3150434B; // "KCP1"

    private readonly ICellPersistenceHost host;
    private readonly IWorldStore store;
    private readonly CellPersistenceConfig config;

    private readonly ConcurrentQueue<(CellCoord coord, byte[] snapshot)> restoreQueue = new();
    private readonly ConcurrentDictionary<CellCoord, byte[]> lastSaved = new();   // raw (unwrapped) snapshot per cell
    private readonly HashSet<CellCoord> loadRequested = new();                    // server-thread-only idempotency
    private readonly ConcurrentDictionary<CellCoord, byte> loadsInFlight = new(); // coords with an outstanding load; SaveDirtyPass skips them so a periodic save can't clobber the stored blob with pre-restore state
    private readonly object pendingLock = new();
    private readonly List<Task> pending = new();
    private int lastSavedNextNetId;
    private float sinceSave;

    public CellPersistence(ICellPersistenceHost host, IWorldStore store, CellPersistenceConfig? config = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.config = config ?? new CellPersistenceConfig();
        host.CellCreated += OnCellCreated;
    }

    private string CellKey(CellCoord c) => $"{config.CellKeyPrefix}{c.X}:{c.Y}";

    private void Track(Task task) { lock (pendingLock) pending.Add(task); }

    private void OnCellCreated(CellCoord coord)
    {
        if (!loadRequested.Add(coord)) return;          // load a given cell at most once
        loadsInFlight[coord] = 0;                        // guard against a dirty-save clobbering the stored blob before the restore applies
        Track(LoadCellAsync(coord));
    }

    private async Task LoadCellAsync(CellCoord coord)
    {
        byte[]? blob = await store.LoadAsync(CellKey(coord)).ConfigureAwait(false);
        if (blob is null) { loadsInFlight.TryRemove(coord, out _); return; }   // no save -> cell stays as spawned
        if (!TryUnwrap(blob, out byte[] snapshot)) { loadsInFlight.TryRemove(coord, out _); return; } // header/schema mismatch -> skip
        lastSaved[coord] = snapshot;                    // loaded == clean baseline
        restoreQueue.Enqueue((coord, snapshot));
    }

    /// <summary>Call once per server frame. Applies completed loads (this thread) + runs the periodic dirty pass.</summary>
    public void Update(float dt)
    {
        DrainRestores();
        lock (pendingLock) pending.RemoveAll(t => t.Status == TaskStatus.RanToCompletion);
        sinceSave += dt;
        if (sinceSave >= config.SaveIntervalSeconds) { sinceSave = 0f; SaveDirtyPass(); }
    }

    private void DrainRestores()
    {
        while (restoreQueue.TryDequeue(out (CellCoord coord, byte[] snapshot) r))
        {
            IReadOnlyList<int> ids = host.RestoreCell(r.coord, r.snapshot);
            int max = 0;
            foreach (int id in ids) if (id > max) max = id;
            if (max > 0) host.EnsureNextNetIdAtLeast(max + 1);
            loadsInFlight.TryRemove(r.coord, out _);     // restore applied; cell is now safe to dirty-save
        }
    }

    /// <summary>Saves every live cell whose persistable snapshot changed since its last save, plus the meta record.</summary>
    public void SaveDirtyPass()
    {
        foreach (CellCoord coord in new List<CellCoord>(host.LiveCellCoords))
        {
            if (loadsInFlight.ContainsKey(coord)) continue;   // load outstanding: skip so a periodic save can't overwrite the stored blob with pre-restore state
            byte[]? snap = host.SnapshotCell(coord);
            if (snap is null) continue;
            if (lastSaved.TryGetValue(coord, out byte[]? prev) && prev.AsSpan().SequenceEqual(snap)) continue;
            lastSaved[coord] = snap;
            Track(store.SaveAsync(CellKey(coord), Wrap(snap)));
        }
        SaveMetaIfAdvanced();
    }

    private void SaveMetaIfAdvanced()
    {
        int next = host.NextNetId;
        if (next <= lastSavedNextNetId) return;
        lastSavedNextNetId = next;
        Track(store.SaveAsync(config.MetaKey, new WorldMetaRecord { NextNetId = next }.Encode()));
    }

    /// <summary>Loads the NetId high-water record and resumes the allocator above it. Call at boot (server thread).</summary>
    public async Task LoadMetaAsync()
    {
        byte[]? data = await store.LoadAsync(config.MetaKey).ConfigureAwait(false);
        if (data is null) return;
        WorldMetaRecord meta = WorldMetaRecord.Decode(data);
        lastSavedNextNetId = meta.NextNetId;
        host.EnsureNextNetIdAtLeast(meta.NextNetId);
    }

    /// <summary>
    /// Instantiates every saved cell (enumerating <c>{CellKeyPrefix}*</c> keys) so its normal load path runs. No-op
    /// on a store that cannot enumerate. Call at boot on the server thread; follow with <see cref="FlushAsync"/> to
    /// apply the restores before the first tick.
    /// </summary>
    public async Task PreloadAsync()
    {
        if (store is not IEnumerableWorldStore es) return;
        var coords = new List<CellCoord>();
        await foreach (WorldStoreEntry entry in es.EnumerateAsync(config.CellKeyPrefix).ConfigureAwait(false))
            if (TryParseCoord(entry.Key, out CellCoord c)) coords.Add(c);
        foreach (CellCoord c in coords) host.EnsureCell(c);
    }

    /// <summary>Awaits all in-flight loads/saves, applies pending restores, then a final dirty + meta save.</summary>
    public async Task FlushAsync()
    {
        DrainRestores();
        await AwaitPendingAsync().ConfigureAwait(false);
        DrainRestores();
        SaveDirtyPass();
        await AwaitPendingAsync().ConfigureAwait(false);
    }

    private async Task AwaitPendingAsync()
    {
        Task[] tasks;
        lock (pendingLock) { tasks = pending.ToArray(); pending.Clear(); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private byte[] Wrap(byte[] snapshot)
    {
        var buf = new byte[8 + snapshot.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), config.SchemaVersion);
        snapshot.CopyTo(buf.AsSpan(8));
        return buf;
    }

    private bool TryUnwrap(byte[] blob, out byte[] snapshot)
    {
        snapshot = Array.Empty<byte>();
        if (blob.Length < 8) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0, 4)) != Magic) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4, 4)) != config.SchemaVersion) return false;
        snapshot = blob[8..];
        return true;
    }

    private bool TryParseCoord(string key, out CellCoord coord)
    {
        coord = default;
        if (!key.StartsWith(config.CellKeyPrefix, StringComparison.Ordinal)) return false;
        string rest = key.Substring(config.CellKeyPrefix.Length);
        int sep = rest.IndexOf(':');
        if (sep <= 0) return false;
        if (int.TryParse(rest.AsSpan(0, sep), out int x) && int.TryParse(rest.AsSpan(sep + 1), out int y))
        { coord = new CellCoord(x, y); return true; }
        return false;
    }
}
