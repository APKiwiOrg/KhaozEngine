using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using KhaozEngine.Replication;
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

    /// <summary>Blob schema version. Bump on a STRUCTURAL component-layout change and register a
    /// <see cref="RegisterMigration"/> from the previous version so old saves are brought forward, not skipped or
    /// misread. Defaults to the engine's current version
    /// (<see cref="WireGenerationBlobMigration.StampedSchemaVersion"/> = 4, the version whose header also records the
    /// writing build's <see cref="MoveProtocol.WireProtocolVersion"/>). Version 3 put the island-frame stamp on
    /// <see cref="ReplicatedPosition"/>, version 2 was the 10.0.0 64-bit
    /// <see cref="KhaozEngine.Replication.NetId"/> layout, and the pre-10.0.0 32-bit layout was version 1. A plain
    /// wire-generation bump needs NO schema bump from v4 on: the stamped generation drives the bring-forward walk
    /// (<see cref="BuiltinBlobLayout"/>).</summary>
    public int SchemaVersion { get; init; } = WireGenerationBlobMigration.StampedSchemaVersion;

    /// <summary>
    /// Whether to fold the engine's own built-in cell-blob migrations into this config's chain (default true). There
    /// are three: the 10.0.0 <see cref="NetIdBlobMigration.WidenV1ToV2(byte[])"/> netId widening (v1 -&gt; v2), the
    /// <see cref="PositionFrameBlobMigration.FrameV2ToV3(byte[])"/> position framing (v2 -&gt; v3), and the
    /// <see cref="WireGenerationBlobMigration.NormalizeV3ToV4(byte[])"/> wire-generation stamp (v3 -&gt; v4). Each is included
    /// automatically for any <see cref="SchemaVersion"/> above it, so a server on the default config
    /// migrates an old save forward without the consumer wiring anything. A consumer migration registered from
    /// the same from-version OVERRIDES the engine step. Set false to test / drive the raw migration machinery in
    /// isolation (only the explicitly <see cref="RegisterMigration"/>-ed steps run), e.g. to pin an old schema version.
    /// </summary>
    public bool IncludeEngineMigrations { get; init; } = true;

    /// <summary>
    /// The live replication registry this server restores cells with, or null (the default) to take the host's own
    /// (<see cref="ICellPersistenceHost.Registry"/>, which <c>ShardedWorldServer</c> exposes), and null from both is
    /// what skips registry-aware validation. It is only read by the two engine migrations that have to INFER a pre-v4
    /// blob's wire generation: a candidate parse that recovers an extension component id this registry has never
    /// heard of is discarded, which is what usually leaves exactly one candidate standing and turns a would-be
    /// <see cref="CellPersistenceIssueKind.QuarantinedAmbiguous"/> into a clean migration. It only ever removes
    /// candidates, and it cannot cost a blob an unsupplied registry would have migrated: a body whose only surviving
    /// readings were retired by this rule is decided again without it, since a retained unknown extension frame is
    /// something a real build wrote. Set this to override the host's registry with a different one.
    /// </summary>
    public ReplicationRegistry? Registry { get; init; }

    /// <summary>
    /// The wire generation the pre-v4 blobs in this save were written at, or null (the default) to infer it. Set it
    /// when the save's provenance is known (the engine version that wrote it maps to a generation, see the table in
    /// <c>docs/USING-KHAOZENGINE.md</c>): the engine migrations then walk each body at exactly that generation
    /// instead of trying candidates, so a blob that would otherwise be quarantined as ambiguous comes forward. Blobs
    /// that carry the stamp (v4 and up) ignore this - their header is the truth. A body that does not walk at the
    /// stated generation is quarantined, never re-guessed.
    /// </summary>
    public int? AssumedWireGeneration { get; init; }

    /// <summary>
    /// Whether a blob that is NEWER than this build (a rolled-back binary reading a save it cannot understand, by
    /// schema version or by wire generation) throws out of the load drain instead of being quarantined and the cell
    /// started fresh (default false, the quarantine). A rollback that keeps running is the one case where the
    /// quarantine is not harmless: the cell starts empty, the next <see cref="CellPersistence.SaveDirtyPass"/> writes that empty
    /// state over the main key, and only the quarantine copy still holds the world. Set this on a server whose
    /// operator would rather see the boot stop than have the save silently hollowed out.
    /// <para>
    /// The throw leaves that coordinate marked as a load in flight, deliberately: a caller that catches it and keeps
    /// ticking still never writes the empty cell over the stored blob, because the dirty pass skips a coordinate
    /// whose load has not been applied.
    /// </para>
    /// </summary>
    public bool FailFastOnTooNew { get; init; }

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
///
/// <para>Tracked store tasks (cell saves, the meta write, quarantine writes) are pruned every <see cref="Update"/>:
/// a faulted or canceled task is observed and its exception surfaced through <see cref="OnStoreError"/>, so a store
/// outage can't grow the pending list unbounded or make the boot sequence / shutdown <see cref="FlushAsync"/> throw
/// when its <c>Task.WhenAll</c> hits the fault. A faulted cell save leaves that cell dirty so the next pass retries
/// it; the meta write is monotonic and re-attempted whenever the high-water advances; a faulted quarantine write is
/// surfaced and dropped (the cell already started fresh, so the load path is unaffected).</para>
/// </summary>
public sealed class CellPersistence
{
    // Header: [int32 magic][int32 schemaVersion], then from WireGenerationBlobMigration.StampedSchemaVersion on
    // [int32 wireGeneration], then the raw Replication snapshot. The schema version itself says which of the two
    // header widths is on disk, so an older blob needs no probing.
    private const int Magic = 0x3150434B; // "KCP1"
    private const int BaseHeaderBytes = 8;
    private const int StampedHeaderBytes = 12;

    // The "no wire generation on disk" marker: every schema below StampedSchemaVersion predates the stamp.
    private const int UnstampedWireGeneration = 0;

    // One step of the effective chain as the DRIVER runs it: the consumer-facing CellSnapshotMigration plus the one
    // thing a step needs to tell the next one, which wire generation the body it produced is at. That is what stops
    // the v3 -> v4 step re-inferring over a body the v2 -> v3 step has already normalized (#353 fix round).
    private delegate byte[] ChainStep(byte[] body, CellBlobMigrationContext context);

    // Resolved once per type, ambient: it follows Log.Configure rather than pinning whatever manager happened to be
    // configured when this type was first touched.
    private static readonly ILogger Log = Diagnostics.Log.Get("CellPersistence");

    private readonly ICellPersistenceHost host;
    private readonly IWorldStore store;
    private readonly CellPersistenceConfig config;
    private readonly IReadOnlyDictionary<int, ChainStep> migrations;
    private readonly int migrationStart;   // lowest from-version in the chain, or SchemaVersion when empty

    // The RAW stored blob per pending cell load (header + body). Unwrap, migrate, quarantine and restore all run on
    // the server thread in DrainRestores, so every Issue event is raised there (never a background continuation).
    private readonly ConcurrentQueue<(CellCoord coord, byte[] rawBlob)> restoreQueue = new();
    private readonly ConcurrentDictionary<CellCoord, byte[]> lastSaved = new();   // raw (unwrapped) snapshot per cell
    private readonly HashSet<CellCoord> loadRequested = new();                    // server-thread-only idempotency
    private readonly ConcurrentDictionary<CellCoord, byte> loadsInFlight = new(); // coords with an outstanding load; SaveDirtyPass skips them so a periodic save can't clobber the stored blob with pre-restore state
    private readonly ConcurrentDictionary<CellCoord, byte> savesInFlight = new(); // coords with an outstanding store write, so IsBusy can gate an eviction on it
    private readonly object pendingLock = new();
    private readonly List<Task> pending = new();
    private long lastSavedNextNetId;   // interlocked: advanced from a save continuation (threadpool) after the meta write lands, read on the server thread
    private float sinceSave;
    // Per-outcome load tallies (server thread only, like the drain that writes them), so ops gets ONE line saying how
    // the save actually came up rather than having to add up an Issue stream that nobody keeps.
    private int migratedCells;
    private int skippedTooOldCells;
    private int skippedTooNewCells;
    private int quarantinedCorruptCells;
    private int quarantinedAmbiguousCells;
    private int loggedOutcomes;

    /// <summary>Raised on the server thread (from <see cref="Update"/> or <see cref="FlushAsync"/>) when a tracked
    /// store task (a cell save, the meta write, or a quarantine write) faulted or was canceled - typically a store
    /// outage. The driver drops the finished task so the pending list can't grow unbounded, and this hook lets the
    /// game log or alert. A faulted cell save's state stays dirty and is retried on the next pass; a faulted
    /// quarantine write is dropped (the cell already started fresh).</summary>
    public event Action<Exception>? OnStoreError;

    /// <summary>
    /// Raised on the server thread (inside the load-apply drain) when a cell's saved blob was migrated, skipped
    /// (too old / too new), quarantined (corrupt / undecodable), or carried unknown extension frames. Observational:
    /// the driver has already handled the situation. Consumers (ops tooling) subscribe to SEE what would otherwise be
    /// silent. See <see cref="CellPersistenceIssue"/>.
    /// </summary>
    public event Action<CellPersistenceIssue>? Issue;

    public CellPersistence(ICellPersistenceHost host, IWorldStore store, CellPersistenceConfig? config = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.config = config ?? new CellPersistenceConfig();
        migrations = BuildEffectiveMigrations(this.config, this.host.Registry);
        migrationStart = ValidateMigrationChain(migrations, this.config.SchemaVersion);
        host.CellCreated += OnCellCreated;
    }

    // Builds the effective migration chain: the engine's own built-in steps (those strictly below the target schema
    // version) with the consumer's registrations layered on top - a consumer step OVERRIDES an engine step of the
    // same from-version. There are three engine steps: the 10.0.0 netId widening (v1 -> v2), the position framing
    // that followed it (v2 -> v3), and the wire-generation stamp (v3 -> v4), so a pre-10.0.0 save boots forward
    // through all three. Each is bound here to what this config knows about the save (the registry and any assumed
    // wire generation, defaulting to the host's own registry), and to the per-blob context they use to hand each
    // other the generation they produced. A
    // config that opts out (IncludeEngineMigrations = false) uses only its own registrations, so the raw driver can
    // be exercised in isolation.
    private static IReadOnlyDictionary<int, ChainStep> BuildEffectiveMigrations(CellPersistenceConfig cfg,
        ReplicationRegistry? hostRegistry)
    {
        var merged = new SortedDictionary<int, ChainStep>();
        if (cfg.IncludeEngineMigrations)
        {
            var options = new CellBlobMigrationOptions
            {
                // The host restores cells with a registry already, so taking it as the default is what stops a
                // server getting the un-filtered inference purely because nobody wired the same object in twice.
                Registry = cfg.Registry ?? hostRegistry,
                AssumedWireGeneration = cfg.AssumedWireGeneration,
            };
            options.Validate();   // a typo'd generation fails here, not on every cell at boot
            if (NetIdBlobMigration.NetId32SchemaVersion < cfg.SchemaVersion)
                merged[NetIdBlobMigration.NetId32SchemaVersion] = NetIdBlobMigration.WidenV1ToV2;
            if (PositionFrameBlobMigration.AbsolutePositionSchemaVersion < cfg.SchemaVersion)
                merged[PositionFrameBlobMigration.AbsolutePositionSchemaVersion] =
                    (body, ctx) => PositionFrameBlobMigration.FrameV2ToV3(body, options, ctx);
            if (WireGenerationBlobMigration.UnstampedSchemaVersion < cfg.SchemaVersion)
                merged[WireGenerationBlobMigration.UnstampedSchemaVersion] =
                    (body, ctx) => WireGenerationBlobMigration.NormalizeV3ToV4(body, options, ctx);
        }
        foreach (KeyValuePair<int, CellSnapshotMigration> kv in cfg.Migrations)
        {
            CellSnapshotMigration consumerStep = kv.Value;
            // Only the consumer step knows what it produced, so it clears the recorded generation rather than letting
            // a later engine step trust one it did not establish.
            merged[kv.Key] = (body, ctx) =>
            {
                byte[] rewritten = consumerStep(body);
                ctx.KnownWireGeneration = null;
                return rewritten;
            };
        }
        return merged;
    }

    // Validates the migration chain (contiguous, no gaps, no step at/beyond the schema version), mirroring
    // MigrationChainBuilder.Build, and returns the lowest from-version (or the schema version when there are no
    // migrations, so any older blob is "too old" to bring forward). Throws on a bad chain at construction time.
    private static int ValidateMigrationChain(IReadOnlyDictionary<int, ChainStep> steps, int schemaVersion)
    {
        if (steps.Count == 0) return schemaVersion;
        int start = int.MaxValue;
        foreach (int from in steps.Keys)
        {
            if (from >= schemaVersion)
                throw new ArgumentException(
                    $"Cell-blob migration from version {from} targets version {from + 1}, at or beyond the schema version {schemaVersion}.");
            if (from < start) start = from;
        }
        for (int v = start; v < schemaVersion; v++)
            if (!steps.ContainsKey(v))
                throw new ArgumentException(
                    $"Cell-blob migration chain has a gap: no step registered from version {v} (steps must be contiguous from {start} to {schemaVersion - 1}).");
        return start;
    }

    /// <summary>How many stored cells were brought forward (a schema migration, a wire-generation walk, or both)
    /// since this driver was constructed.</summary>
    public int MigratedCellCount => migratedCells;

    /// <summary>How many stored cells were skipped as older than the earliest registered migration.</summary>
    public int SkippedTooOldCellCount => skippedTooOldCells;

    /// <summary>How many stored cells were skipped as newer than this build understands (a rollback), by schema
    /// version or by wire generation. Every one of them is a cell that started EMPTY and will be overwritten with
    /// that empty state by the next save pass - see <see cref="CellPersistenceConfig.FailFastOnTooNew"/>.</summary>
    public int SkippedTooNewCellCount => skippedTooNewCells;

    /// <summary>How many stored cells failed to decode and were quarantined.</summary>
    public int QuarantinedCorruptCellCount => quarantinedCorruptCells;

    /// <summary>How many stored cells were quarantined because their wire generation could not be inferred (several
    /// candidates walk the body and disagree). <see cref="CellPersistenceConfig.AssumedWireGeneration"/> is what
    /// brings these in.</summary>
    public int QuarantinedAmbiguousCellCount => quarantinedAmbiguousCells;

    // Every Issue goes through here so the tallies cannot drift from the events, and so the aggregate line below has
    // something to report. Server thread only (the load drain).
    private void RaiseIssue(CellPersistenceIssue issue)
    {
        switch (issue.Kind)
        {
            case CellPersistenceIssueKind.Migrated: migratedCells++; break;
            case CellPersistenceIssueKind.SkippedTooOld: skippedTooOldCells++; break;
            case CellPersistenceIssueKind.SkippedTooNew: skippedTooNewCells++; break;
            case CellPersistenceIssueKind.QuarantinedCorrupt: quarantinedCorruptCells++; break;
            case CellPersistenceIssueKind.QuarantinedAmbiguous: quarantinedAmbiguousCells++; break;
            default: break;   // RetainedUnknownExtensions is a property of a cell that loaded fine
        }
        Issue?.Invoke(issue);
    }

    // One line per flush that changed something, so the boot flush prints what the save came up as. Silent when
    // nothing but clean loads happened, which is the normal case.
    private void LogLoadOutcomes()
    {
        int total = migratedCells + skippedTooOldCells + skippedTooNewCells + quarantinedCorruptCells
            + quarantinedAmbiguousCells;
        if (total == loggedOutcomes) return;
        loggedOutcomes = total;
        Log.Info($"cell blobs: {migratedCells} migrated, {skippedTooOldCells} skipped as too old, " +
                 $"{skippedTooNewCells} skipped as too new, {quarantinedCorruptCells} quarantined as corrupt, " +
                 $"{quarantinedAmbiguousCells} quarantined as ambiguous");
    }

    private string CellKey(CellCoord c) => $"{config.CellKeyPrefix}{c.X}:{c.Y}";

    private void Track(Task task) { lock (pendingLock) pending.Add(task); }

    private void OnCellCreated(CellCoord coord) => RequestLoad(coord);

    /// <summary>
    /// Starts this cell's store load if one has not been requested already, the work
    /// <see cref="ICellPersistenceHost.CellCreated"/> normally triggers. Idempotent per coordinate for the life of
    /// the driver, so it is safe to call spuriously. A coordinate whose cell was unloaded and whose bookkeeping was
    /// cleared by <see cref="ForgetCell"/> loads again. Returns false when a load was already requested.
    /// </summary>
    public bool RequestLoad(CellCoord coord)
    {
        if (!loadRequested.Add(coord)) return false;    // load a given cell at most once
        loadsInFlight[coord] = 0;                        // guard against a dirty-save clobbering the stored blob before the restore applies
        Track(LoadCellAsync(coord));
        return true;
    }

    /// <summary>
    /// Whether a store operation for this cell is outstanding: a load whose restore has not been applied yet, or a
    /// write that has not landed. The gate a cell-eviction driver checks before snapshotting, since a cell caught
    /// mid-restore would be persisted (and then unloaded) in its pre-restore state.
    /// </summary>
    public bool IsBusy(CellCoord coord) => loadsInFlight.ContainsKey(coord) || savesInFlight.ContainsKey(coord);

    /// <summary>
    /// The bytes last durably written for this cell (the dirty-tracking baseline), unwrapped. Exposed so an
    /// eviction driver can confirm what it persisted. False when the cell has never been saved or restored.
    /// </summary>
    public bool TryGetLastSaved(CellCoord coord, out byte[] snapshot) => lastSaved.TryGetValue(coord, out snapshot!);

    /// <summary>
    /// Drops this driver's per-cell bookkeeping (the load-once marker and the dirty baseline) for a coordinate whose
    /// cell has been unloaded, so the next <see cref="ICellPersistenceHost.CellCreated"/> for it loads from the
    /// store again rather than being treated as already loaded. The stored blob itself is untouched.
    /// </summary>
    /// <remarks>
    /// An eviction driver that keeps the evicted snapshot in memory and restores it synchronously on recreation
    /// must NOT call this: leaving the marker in place is exactly what stops this driver from restoring the same
    /// cell a second time from the store. Call it only when handing the coordinate back to the store-backed path.
    /// </remarks>
    public void ForgetCell(CellCoord coord)
    {
        loadRequested.Remove(coord);
        lastSaved.TryRemove(coord, out _);
    }

    /// <summary>
    /// Writes one cell's snapshot to the store immediately, outside the periodic dirty pass, and reports whether it
    /// landed. On success the dirty baseline advances, so the cell reads clean until it changes again. The task is
    /// tracked like any other store work (a <see cref="FlushAsync"/> awaits it, a fault surfaces through
    /// <see cref="OnStoreError"/>), and faults rather than returning false, so a caller checks
    /// <c>IsCompletedSuccessfully</c> before its result. This is the persist half of an eviction: the cell may only
    /// be unloaded once it completes true.
    /// </summary>
    public Task<bool> SaveCellAsync(CellCoord coord, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        savesInFlight[coord] = 0;
        Task<bool> task = SaveOneCellAsync(coord, snapshot);
        Track(task);
        return task;
    }

    private async Task<bool> SaveOneCellAsync(CellCoord coord, byte[] snapshot)
    {
        try
        {
            await store.SaveAsync(CellKey(coord), Wrap(snapshot)).ConfigureAwait(false);
        }
        finally
        {
            savesInFlight.TryRemove(coord, out _);
        }
        lastSaved[coord] = snapshot;
        return true;
    }

    private async Task LoadCellAsync(CellCoord coord)
    {
        byte[]? blob = await store.LoadAsync(CellKey(coord)).ConfigureAwait(false);
        if (blob is null) { loadsInFlight.TryRemove(coord, out _); return; }   // no save -> cell stays as spawned
        restoreQueue.Enqueue((coord, blob));   // raw blob; header/migrate/quarantine/restore happen on the server thread
    }

    /// <summary>Call once per server frame. Applies completed loads (this thread) + runs the periodic dirty pass.</summary>
    public void Update(float dt)
    {
        DrainRestores();
        PrunePending();
        sinceSave += dt;
        if (sinceSave >= config.SaveIntervalSeconds) { sinceSave = 0f; SaveDirtyPass(); }
    }

    // Drops every finished task from the pending list. Previously only RanToCompletion was pruned, so on a store
    // outage the faulted/canceled tasks (a save, the meta write, or a quarantine write) accumulated until FlushAsync's
    // Task.WhenAll surfaced them - the list grew unbounded and the boot/shutdown flush threw. Faults are observed
    // (reading Task.Exception) and surfaced via OnStoreError; a faulted cell save's state stays dirty and retries on
    // the next pass. Collect the exceptions inside the lock but raise the event outside it (never run a game callback
    // while holding pendingLock). Mirrors WorldPersistence.PrunePending.
    private void PrunePending()
    {
        List<Exception>? failures = null;
        lock (pendingLock)
            pending.RemoveAll(t =>
            {
                if (!t.IsCompleted) return false;
                if (t.IsFaulted || t.IsCanceled)
                    (failures ??= new List<Exception>()).Add(t.Exception?.GetBaseException() ?? new TaskCanceledException());
                return true;
            });
        if (failures is not null)
            foreach (Exception ex in failures) OnStoreError?.Invoke(ex);
    }

    private void DrainRestores()
    {
        while (restoreQueue.TryDequeue(out (CellCoord coord, byte[] rawBlob) r))
        {
            ProcessLoadedBlob(r.coord, r.rawBlob);
            loadsInFlight.TryRemove(r.coord, out _);     // load handled (restored/migrated/skipped/quarantined): safe to dirty-save
        }
    }

    // Server-thread handling of one loaded blob: read the header, bring it forward (through the wire-generation walk
    // when the header records an older generation, then through the migration chain when the schema is older), and
    // restore it - quarantining (preserving the bytes, cell starts fresh) any undecodable case instead of throwing,
    // so a poisoned key can never crash-loop the server. Every outcome that ops should see is surfaced through the
    // Issue event.
    private void ProcessLoadedBlob(CellCoord coord, byte[] rawBlob)
    {
        if (!TryReadHeader(rawBlob, out int storedVersion, out int storedGeneration, out byte[] body))
        {
            Quarantine(coord, rawBlob, CellPersistenceIssue.QuarantinedCorrupt(coord, "missing or invalid blob header"));
            return;
        }
        if (storedVersion > config.SchemaVersion)
        {
            SkipTooNew(coord, rawBlob, CellPersistenceIssue.SkippedTooNew(coord, storedVersion, config.SchemaVersion));
            return;
        }
        int current = BuiltinBlobLayout.CurrentWireGeneration;
        if (storedGeneration > current)
        {
            // The schema fits but the BODY was written by a newer wire generation, so its built-in payloads are
            // shapes this build has no reader for. A downgrade, and the same call as a too-new schema: skip, keep
            // the bytes. Before the generation was stamped this was a silent misparse (#322).
            SkipTooNew(coord, rawBlob, CellPersistenceIssue.SkippedTooNew(coord, storedVersion, config.SchemaVersion,
                $"wire generation {storedGeneration} is newer than this build's {current}"));
            return;
        }
        if (storedVersion < config.SchemaVersion && storedVersion < migrationStart)
        {
            Quarantine(coord, rawBlob, CellPersistenceIssue.SkippedTooOld(coord, storedVersion, config.SchemaVersion));
            return;
        }

        byte[] forwardBody = body;
        bool broughtForward = false;
        string? detail = null;
        // What the chain's steps know about this body's layout. Seeded from the header when it carries the stamp, so
        // a consumer step registered above v4 is handed payloads at this build's generation and no engine step below
        // it ever infers what the header already recorded.
        var context = new CellBlobMigrationContext();
        if (storedGeneration != UnstampedWireGeneration) context.KnownWireGeneration = storedGeneration;

        // A recorded generation older than this build's moves the body first, so a consumer step registered above v4
        // sees payloads at this build's layout rather than at whatever the writer's generation was.
        if (storedGeneration != UnstampedWireGeneration && storedGeneration != current)
        {
            try { forwardBody = BuiltinBlobLayout.NormalizeToCurrent(forwardBody, storedGeneration); }
            catch (Exception ex)
            {
                Quarantine(coord, rawBlob, CellPersistenceIssue.QuarantinedCorrupt(coord,
                    $"wire generation {storedGeneration} -> {current} failed: {ex.Message}"));
                return;
            }
            context.KnownWireGeneration = current;
            broughtForward = true;
            detail = $"wire generation {storedGeneration} -> {current}";
        }

        if (storedVersion < config.SchemaVersion)
        {
            try
            {
                for (int v = storedVersion; v < config.SchemaVersion; v++)
                    forwardBody = migrations[v](forwardBody, context)
                        ?? throw new InvalidOperationException($"cell-blob migration from version {v} returned null");
            }
            catch (AmbiguousCellBlobGenerationException ex)
            {
                // The body walks at several wire generations and they disagree, so there is no honest reading of it.
                // Its own outcome, not a corrupt one: the bytes are fine, it is the layout nobody recorded.
                Quarantine(coord, rawBlob,
                    CellPersistenceIssue.QuarantinedAmbiguous(coord, storedVersion, ex.CandidateGenerations));
                return;
            }
            catch (Exception ex)
            {
                Quarantine(coord, rawBlob, CellPersistenceIssue.QuarantinedCorrupt(coord, $"migration from v{storedVersion} failed: {ex.Message}"));
                return;
            }
            broughtForward = true;
            detail = null;   // the schema hop is the headline; the chain's steps carry the generation walk inside them
        }

        TryRestoreAndBaseline(coord, forwardBody, rawBlob, broughtForward, storedVersion, detail);
    }

    // A blob this build is too old to read. Quarantining it keeps the bytes but leaves the cell empty, and the next
    // dirty pass then writes that empty cell over the main key - so a server whose operator would rather stop than
    // hollow out the save sets CellPersistenceConfig.FailFastOnTooNew and gets the throw instead. It propagates out
    // of the load drain (Update / FlushAsync) on the server thread, deliberately.
    private void SkipTooNew(CellCoord coord, byte[] rawBlob, CellPersistenceIssue issue)
    {
        Quarantine(coord, rawBlob, issue);
        if (config.FailFastOnTooNew) throw new InvalidOperationException(issue.ToString());
    }

    // Restores a decoded body via the non-throwing host path. On decode failure the blob is quarantined (its bytes
    // preserved, cell left fresh). On success: raises the NetId high-water, surfaces Migrated / RetainedUnknownExtensions
    // events, and seeds the dirty-baseline - except for a migrated blob, which is left unset so the upgraded form
    // (current header + migrated body) is rewritten once, advancing the on-disk schema version.
    private void TryRestoreAndBaseline(CellCoord coord, byte[] body, byte[] rawBlob, bool migrated, int fromVersion,
        string? migrationDetail = null)
    {
        CellRestoreResult r = host.TryRestoreCell(coord, body);
        if (!r.Ok)
        {
            Quarantine(coord, rawBlob, CellPersistenceIssue.QuarantinedCorrupt(coord, r.Error ?? "cell snapshot failed to decode"));
            return;
        }

        // Single high-water over ALL restored ids. Correct while everything is node 0 (ids are then numerically
        // ordered by counter). When multi-node lands this needs a PER-NODE max: NetId packs the node in the high 16
        // bits, so a foreign-node id sorts numerically ABOVE a local one yet EnsureNextAtLeast ignores it (its node
        // bits are not ours), leaving this node's counter unadvanced - so restore the per-node high-water for each
        // node present, not one max across all of them.
        long max = 0;
        foreach (long id in r.NetIds) if (id > max) max = id;
        if (max > 0) host.EnsureNextNetIdAtLeast(max + 1);

        if (migrated) RaiseIssue(CellPersistenceIssue.Migrated(coord, fromVersion, config.SchemaVersion, migrationDetail));
        if (r.RetainedFrameCount > 0) RaiseIssue(CellPersistenceIssue.RetainedUnknownExtensions(coord, r.RetainedFrameCount));

        if (!migrated) lastSaved[coord] = host.SnapshotCell(coord) ?? body;
    }

    // Copies the original bytes to the quarantine key (so nothing is destroyed), then surfaces the issue. The cell is
    // left fresh; a later save may reuse the main key (the original is safe under quarantine). The quarantine write is
    // fire-and-forget (tracked only so a flush can await/observe it, not dirty-tracked): if it faults on a store
    // outage it is surfaced via OnStoreError and DROPPED, not retried - the cell has already started fresh, so the
    // load path is unaffected and retrying a one-shot forensic copy isn't worth the bookkeeping.
    private void Quarantine(CellCoord coord, byte[] rawBlob, CellPersistenceIssue issue)
    {
        Track(store.SaveAsync(QuarantineKey(coord), rawBlob));
        RaiseIssue(issue);
    }

    private string QuarantineKey(CellCoord c) => $"{config.QuarantineKeyPrefix}{CellKey(c)}";

    /// <summary>Saves every live cell whose persistable snapshot changed since its last save, plus the meta record.</summary>
    public void SaveDirtyPass()
    {
        List<(CellCoord coord, byte[] snap)>? dirty = null;
        foreach (CellCoord coord in new List<CellCoord>(host.LiveCellCoords))
        {
            if (loadsInFlight.ContainsKey(coord)) continue;   // load outstanding: skip so a periodic save can't overwrite the stored blob with pre-restore state
            if (savesInFlight.ContainsKey(coord)) continue;   // save outstanding (an eviction): skip so this pass can't race that write with an unordered one of its own
            byte[]? snap = host.SnapshotCell(coord);
            if (snap is null) continue;
            if (lastSaved.TryGetValue(coord, out byte[]? prev) && prev.AsSpan().SequenceEqual(snap)) continue;
            (dirty ??= new List<(CellCoord, byte[])>()).Add((coord, snap));
        }
        if (dirty is not null)
        {
            // Mark before dispatching, on this thread, so an eviction requested between here and the first await
            // still sees the write as outstanding.
            foreach ((CellCoord coord, byte[] _) in dirty) savesInFlight[coord] = 0;
            Track(SaveManyCellsAsync(dirty));
        }
        SaveMetaIfAdvanced();
    }

    // Batches every dirty cell's wrapped snapshot into one store round trip (one SaveManyAsync call instead of N
    // SaveAsync calls). lastSaved is advanced per cell only AFTER the whole batch lands, so a faulted/canceled batch
    // leaves every cell in it dirty for the next pass - the same "never mark a cell clean before it is actually
    // saved" guarantee the old per-cell SaveCellAsync gave, just at batch grain: one failed round trip means the
    // whole pass retries next interval, not only the one cell that actually caused the fault.
    private async Task SaveManyCellsAsync(List<(CellCoord coord, byte[] snap)> dirty)
    {
        var items = new List<(string Key, byte[] Data)>(dirty.Count);
        foreach ((CellCoord coord, byte[] snap) in dirty) items.Add((CellKey(coord), Wrap(snap)));
        try
        {
            await store.SaveManyAsync(items).ConfigureAwait(false);
        }
        finally
        {
            foreach ((CellCoord coord, byte[] _) in dirty) savesInFlight.TryRemove(coord, out _);
        }
        foreach ((CellCoord coord, byte[] snap) in dirty) lastSaved[coord] = snap;
    }

    private void SaveMetaIfAdvanced()
    {
        long next = host.NextNetId;
        if (next <= Interlocked.Read(ref lastSavedNextNetId)) return;
        Track(SaveMetaAsync(next));
    }

    // Writes the meta high-water, advancing the persisted baseline only AFTER the write lands (interlocked - the
    // continuation runs off the server thread). A faulted meta write leaves the baseline low, so a later pass retries
    // it; the high-water is monotonic, so the advance is a max, never a lower-write.
    private async Task SaveMetaAsync(long next)
    {
        await store.SaveAsync(config.MetaKey, new WorldMetaRecord { NextNetId = next }.Encode()).ConfigureAwait(false);
        long cur = Interlocked.Read(ref lastSavedNextNetId);
        while (next > cur)
        {
            long prev = Interlocked.CompareExchange(ref lastSavedNextNetId, next, cur);
            if (prev == cur) break;
            cur = prev;
        }
    }

    /// <summary>Loads the NetId high-water record and resumes the allocator above it. Call at boot (server thread).</summary>
    public async Task LoadMetaAsync()
    {
        byte[]? data = await store.LoadAsync(config.MetaKey).ConfigureAwait(false);
        if (data is null) return;
        WorldMetaRecord meta = WorldMetaRecord.Decode(data);
        Interlocked.Exchange(ref lastSavedNextNetId, meta.NextNetId);   // boot is quiescent; interlocked to pair with SaveMetaAsync's off-thread advance
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
        LogLoadOutcomes();
        SaveDirtyPass();
        await AwaitPendingAsync().ConfigureAwait(false);
    }

    // Awaits every tracked task to completion, then observes it. Unlike a bare Task.WhenAll (which rethrows the first
    // fault and, having cleared pending, would leave the rest unobserved) this surfaces EVERY faulted/canceled task
    // through OnStoreError and never throws - so the boot sequence (LoadMeta -> Preload -> Flush) and the shutdown
    // flush complete cleanly through a store outage, leaving faulted cell saves dirty to retry on the next pass.
    private async Task AwaitPendingAsync()
    {
        Task[] tasks;
        lock (pendingLock) { tasks = pending.ToArray(); pending.Clear(); }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* individual faults are observed + surfaced per-task below, not rethrown */ }
        List<Exception>? failures = null;
        foreach (Task t in tasks)
            if (t.IsFaulted || t.IsCanceled)
                (failures ??= new List<Exception>()).Add(t.Exception?.GetBaseException() ?? new TaskCanceledException());
        if (failures is not null)
            foreach (Exception ex in failures) OnStoreError?.Invoke(ex);
    }

    private byte[] Wrap(byte[] snapshot)
    {
        bool stamped = config.SchemaVersion >= WireGenerationBlobMigration.StampedSchemaVersion;
        int headerBytes = stamped ? StampedHeaderBytes : BaseHeaderBytes;
        var buf = new byte[headerBytes + snapshot.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), config.SchemaVersion);
        // The body is whatever the live registry writes, so it is at THIS build's wire generation by construction.
        if (stamped) BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), BuiltinBlobLayout.CurrentWireGeneration);
        snapshot.CopyTo(buf.AsSpan(headerBytes));
        return buf;
    }

    // Reads the [magic][version] header (plus the [wireGeneration] word from StampedSchemaVersion on) and returns the
    // body. Version-agnostic (the caller decides migrate / skip / restore): only a bad magic or a too-short blob
    // fails here, which the caller treats as corrupt. A pre-stamp blob reports UnstampedWireGeneration, which means
    // "not recorded" rather than a generation number - the migration chain infers it from the body instead.
    private static bool TryReadHeader(byte[] blob, out int version, out int wireGeneration, out byte[] body)
    {
        version = 0;
        wireGeneration = UnstampedWireGeneration;
        body = Array.Empty<byte>();
        if (blob.Length < BaseHeaderBytes) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0, 4)) != Magic) return false;
        version = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4, 4));
        if (version < WireGenerationBlobMigration.StampedSchemaVersion)
        {
            body = blob[BaseHeaderBytes..];
            return true;
        }
        if (blob.Length < StampedHeaderBytes) return false;
        wireGeneration = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8, 4));
        if (wireGeneration < BuiltinBlobLayout.OldestKnownWireGeneration) return false;   // 0 / negative: not a stamp
        body = blob[StampedHeaderBytes..];
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
