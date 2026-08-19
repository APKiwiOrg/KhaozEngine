using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>What happened to a cell's saved blob on load, surfaced through <see cref="CellPersistence.Issue"/> so
/// ops can SEE schema evolution, downgrades, corruption, and registry regressions instead of them being silent.</summary>
public enum CellPersistenceIssueKind
{
    /// <summary>The stored blob is older than the earliest registered migration, so it cannot be brought to the
    /// current schema. It is skipped (never misread) and its bytes are preserved under the quarantine key; the cell
    /// starts fresh.</summary>
    SkippedTooOld,

    /// <summary>The stored blob's schema version is NEWER than this build understands (a downgrade / rollback). It is
    /// skipped and its bytes are preserved under the quarantine key; the cell starts fresh.</summary>
    SkippedTooNew,

    /// <summary>The stored blob was migrated from an older schema version up to the current one before restore
    /// (<see cref="CellPersistenceIssue.FromVersion"/> -&gt; <see cref="CellPersistenceIssue.ToVersion"/>).</summary>
    Migrated,

    /// <summary>The stored blob failed to decode (bad header, corrupt frame, a migration threw, or restore rejected
    /// it). The original bytes are preserved under the quarantine key and the cell starts fresh, so the server never
    /// crash-loops on a poisoned key.</summary>
    QuarantinedCorrupt,

    /// <summary>The restored blob carried extension component ids this build's registry does not know. They were
    /// retained and will be re-persisted verbatim (retain-and-rewrite), so a registry regression did not strip data
    /// at rest. <see cref="CellPersistenceIssue.RetainedFrameCount"/> reports how many.</summary>
    RetainedUnknownExtensions,
}

/// <summary>
/// A diagnostics event raised by <see cref="CellPersistence"/> when a cell's saved blob needed migration, was skipped,
/// was quarantined, or carried unknown extension frames. Raised on the server thread (inside the load-apply drain),
/// so a handler may touch server state directly. Purely observational: the persistence driver has already handled the
/// situation by the time the event fires.
/// </summary>
public readonly struct CellPersistenceIssue
{
    public CellPersistenceIssue(CellPersistenceIssueKind kind, CellCoord coord, int fromVersion, int toVersion,
        int retainedFrameCount, string? message)
    {
        Kind = kind;
        Coord = coord;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        RetainedFrameCount = retainedFrameCount;
        Message = message;
    }

    /// <summary>What happened.</summary>
    public CellPersistenceIssueKind Kind { get; }

    /// <summary>The affected cell coordinate.</summary>
    public CellCoord Coord { get; }

    /// <summary>The blob's stored schema version (for <see cref="CellPersistenceIssueKind.Migrated"/>,
    /// <see cref="CellPersistenceIssueKind.SkippedTooOld"/>, <see cref="CellPersistenceIssueKind.SkippedTooNew"/>); 0 otherwise.</summary>
    public int FromVersion { get; }

    /// <summary>The current schema version the blob was (or would be) brought to; 0 when not applicable.</summary>
    public int ToVersion { get; }

    /// <summary>How many unknown extension frames were retained (for <see cref="CellPersistenceIssueKind.RetainedUnknownExtensions"/>); 0 otherwise.</summary>
    public int RetainedFrameCount { get; }

    /// <summary>A human-readable detail (the decode error for <see cref="CellPersistenceIssueKind.QuarantinedCorrupt"/>,
    /// the wire-generation hop for a <see cref="CellPersistenceIssueKind.Migrated"/> or
    /// <see cref="CellPersistenceIssueKind.SkippedTooNew"/> that was about the generation rather than the schema
    /// version); null otherwise.</summary>
    public string? Message { get; }

    public static CellPersistenceIssue Migrated(CellCoord coord, int fromVersion, int toVersion)
        => new(CellPersistenceIssueKind.Migrated, coord, fromVersion, toVersion, 0, null);

    /// <summary>As <see cref="Migrated(CellCoord,int,int)"/>, with a detail line for a bring-forward the schema
    /// versions alone do not describe - a wire-generation walk leaves both versions equal.</summary>
    public static CellPersistenceIssue Migrated(CellCoord coord, int fromVersion, int toVersion, string? message)
        => new(CellPersistenceIssueKind.Migrated, coord, fromVersion, toVersion, 0, message);

    public static CellPersistenceIssue SkippedTooOld(CellCoord coord, int storedVersion, int schemaVersion)
        => new(CellPersistenceIssueKind.SkippedTooOld, coord, storedVersion, schemaVersion, 0, null);

    public static CellPersistenceIssue SkippedTooNew(CellCoord coord, int storedVersion, int schemaVersion)
        => new(CellPersistenceIssueKind.SkippedTooNew, coord, storedVersion, schemaVersion, 0, null);

    /// <summary>As <see cref="SkippedTooNew(CellCoord,int,int)"/>, with a detail line for a skip the schema versions
    /// alone do not describe - a blob whose stored WIRE GENERATION is newer than this build's.</summary>
    public static CellPersistenceIssue SkippedTooNew(CellCoord coord, int storedVersion, int schemaVersion, string? message)
        => new(CellPersistenceIssueKind.SkippedTooNew, coord, storedVersion, schemaVersion, 0, message);

    public static CellPersistenceIssue QuarantinedCorrupt(CellCoord coord, string message)
        => new(CellPersistenceIssueKind.QuarantinedCorrupt, coord, 0, 0, 0, message);

    public static CellPersistenceIssue RetainedUnknownExtensions(CellCoord coord, int count)
        => new(CellPersistenceIssueKind.RetainedUnknownExtensions, coord, 0, 0, count, null);

    public override string ToString() => Kind switch
    {
        CellPersistenceIssueKind.Migrated => Message is null
            ? $"cell {Coord.X}:{Coord.Y} migrated v{FromVersion} -> v{ToVersion}"
            : $"cell {Coord.X}:{Coord.Y} migrated v{FromVersion} -> v{ToVersion} ({Message})",
        CellPersistenceIssueKind.SkippedTooOld => $"cell {Coord.X}:{Coord.Y} skipped: stored v{FromVersion} predates the oldest migration (schema v{ToVersion}), bytes quarantined",
        CellPersistenceIssueKind.SkippedTooNew => Message is null
            ? $"cell {Coord.X}:{Coord.Y} skipped: stored v{FromVersion} is newer than schema v{ToVersion} (downgrade), bytes quarantined"
            : $"cell {Coord.X}:{Coord.Y} skipped: {Message} (downgrade), bytes quarantined",
        CellPersistenceIssueKind.QuarantinedCorrupt => $"cell {Coord.X}:{Coord.Y} quarantined (corrupt): {Message}",
        CellPersistenceIssueKind.RetainedUnknownExtensions => $"cell {Coord.X}:{Coord.Y} retained {RetainedFrameCount} unknown extension frame(s)",
        _ => $"cell {Coord.X}:{Coord.Y} {Kind}",
    };
}
