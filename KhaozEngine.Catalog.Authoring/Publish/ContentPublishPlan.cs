using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// A chunk's full address (spec 6.7). The SIDE is part of it and not a flag on it: the client chunk and the
/// server chunk of one id range are two different runs of bytes with two different hashes, so they are two
/// addresses.
/// </summary>
/// <param name="Type">The content type.</param>
/// <param name="ChunkIndex">The chunk's index within its type, <c>definitionId / chunkSlots</c>.</param>
/// <param name="Side">Which side the bytes were encoded for.</param>
public readonly record struct ContentChunkAddress(ContentTypeId Type, int ChunkIndex, ContentVisibility Side);

/// <summary>
/// One <c>catalog_chunk</c> row as a publish computes it: the address, the content hash, the two sizes, the
/// row count, and the stored file for a chunk this publish actually encoded.
/// <para>
/// <b>A REUSED chunk carries no bytes</b> (spec 6.6). Its hash is read from the previous version's row and
/// nothing is encoded, compressed, hashed or written for it, which is the mechanism behind the
/// download-size-after-a-one-item-edit budget.
/// </para>
/// </summary>
public sealed class ContentChunkRecord
{
    /// <summary>Builds one chunk row.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="chunkIndex">The chunk's index within its type.</param>
    /// <param name="side">The side these bytes were encoded for.</param>
    /// <param name="hash">The content address, lower hex, over the UNCOMPRESSED canonical bytes.</param>
    /// <param name="uncompressedBytes">The body's length before decompression.</param>
    /// <param name="storedBytes">The whole stored file's length, header included.</param>
    /// <param name="rowCount">The rows in the table, which is not the slot count.</param>
    /// <param name="storedFile">The file as it goes to the pack store, empty on a reused chunk.</param>
    /// <param name="isReused">Whether this row was carried forward rather than encoded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hash"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or a size is negative.</exception>
    public ContentChunkRecord(
        ContentTypeId type,
        int chunkIndex,
        ContentVisibility side,
        string hash,
        int uncompressedBytes,
        int storedBytes,
        int rowCount,
        ReadOnlyMemory<byte> storedFile,
        bool isReused)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(uncompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(storedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        Type = type;
        ChunkIndex = chunkIndex;
        Side = side;
        Hash = hash;
        UncompressedBytes = uncompressedBytes;
        StoredBytes = storedBytes;
        RowCount = rowCount;
        StoredFile = storedFile;
        IsReused = isReused;
    }

    /// <summary>The content type.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The chunk's index within its type.</summary>
    public int ChunkIndex { get; }

    /// <summary>The side these bytes were encoded for.</summary>
    public ContentVisibility Side { get; }

    /// <summary>The content address, lower hex, over the uncompressed canonical bytes.</summary>
    public string Hash { get; }

    /// <summary>The body's length before decompression, which is the manifest's per-chunk figure.</summary>
    public int UncompressedBytes { get; }

    /// <summary>The whole stored file's length, header included.</summary>
    public int StoredBytes { get; }

    /// <summary>The rows in the table.</summary>
    public int RowCount { get; }

    /// <summary>The file step 9 writes, and EMPTY on a reused chunk, which step 9 skips.</summary>
    public ReadOnlyMemory<byte> StoredFile { get; }

    /// <summary>Whether this row was carried forward from the previous version rather than encoded.</summary>
    public bool IsReused { get; }

    /// <summary>The address, which is what the carry forward of spec 6.6 matches on.</summary>
    public ContentChunkAddress Address => new(Type, ChunkIndex, Side);

    /// <summary>
    /// The same row as the NEXT version would carry it forward: the hash and the sizes without the bytes,
    /// because a version that reuses a chunk writes nothing for it.
    /// </summary>
    public ContentChunkRecord AsReused() => IsReused
        ? this
        : new ContentChunkRecord(Type, ChunkIndex, Side, Hash, UncompressedBytes, StoredBytes, RowCount, default, true);
}

/// <summary>Where one new row's definition id came from, spec 6.3's one path with two sources.</summary>
public enum ContentIdSource
{
    /// <summary>The edit CARRIED the id, which a bulk import and a content upgrade write and nothing else.</summary>
    Carried = 0,

    /// <summary>The plain per-type counter issued it.</summary>
    Plain = 1,

    /// <summary>A family's aligned block issued it.</summary>
    Family = 2,
}

/// <summary>One new row's id and where it came from.</summary>
/// <param name="EditOrdinal">The edit's position in the frozen change set, which is the allocation order.</param>
/// <param name="Type">The content type.</param>
/// <param name="Key">The row's key.</param>
/// <param name="DefinitionId">The id the row will carry.</param>
/// <param name="Source">Which of the three sources issued it.</param>
/// <param name="FamilyId">The family the row belongs to, or null.</param>
public sealed record ContentIdAllocationEntry(
    int EditOrdinal,
    ContentTypeId Type,
    ContentKey Key,
    int DefinitionId,
    ContentIdSource Source,
    long? FamilyId);

/// <summary>
/// One type's high-water mark moved up to a CARRIED id, spec 6.3's seeding step. Without it the first
/// ordinary add after an import allocates id 1 straight onto an imported row.
/// </summary>
/// <param name="Type">The content type.</param>
/// <param name="SeededThrough">The largest carried id of that type, which both marks moved to at least.</param>
public sealed record ContentIdSeed(ContentTypeId Type, int SeededThrough);

/// <summary>
/// What step 3 did: every new row's id in edit ordinal order, and every high-water mark it seeded.
/// <para>
/// <see cref="Seeds"/> is EMPTY for an ordinary publish, because nothing carries an id there, and that
/// emptiness is the assertion a test makes rather than a thing the step skips silently.
/// </para>
/// </summary>
/// <param name="Entries">One entry per row that needed an id, in edit ordinal order.</param>
/// <param name="Seeds">One entry per type whose marks moved to a carried id.</param>
public sealed record ContentIdAllocationRecord(
    IReadOnlyList<ContentIdAllocationEntry> Entries,
    IReadOnlyList<ContentIdSeed> Seeds)
{
    /// <summary>An allocation that issued nothing, which is a publish of updates and retires alone.</summary>
    public static ContentIdAllocationRecord Empty { get; } = new([], []);
}

/// <summary>One row revision CLOSED at the new version, spec 6.5.</summary>
/// <param name="Type">The content type.</param>
/// <param name="DefinitionId">The definition id, which the successor keeps.</param>
/// <param name="ValidFromVersion">The closed revision's own valid-from, which identifies it.</param>
/// <param name="ReplacedInVersion">The new version, which is what the close writes.</param>
public sealed record ContentRowClose(
    ContentTypeId Type,
    int DefinitionId,
    int ValidFromVersion,
    int ReplacedInVersion);

/// <summary>One row revision ENTERING at the new version, spec 6.5.</summary>
/// <param name="Row">The row as it will stand, ids and fields final.</param>
/// <param name="ValidFromVersion">The new version.</param>
/// <param name="FamilyId">The family it was allocated from, or null.</param>
public sealed record ContentRowInsert(ContentRow Row, int ValidFromVersion, long? FamilyId);

/// <summary>
/// The BASE version as steps 2 to 8 need it: its number, its live rows, its rules, its chunk rows, its
/// languages and its two minimum builds.
/// <para>
/// <b>It is handed IN rather than read here, and that is the crash-safety shape.</b> The commit of step 10
/// reads the base under the row lock it took at step 1 and hands it down, so the version the candidate was
/// built against and the version the transaction commits against cannot differ. A publisher that read it
/// itself would be reading it outside the lock.
/// </para>
/// </summary>
public sealed class ContentPublishBaseline
{
    static readonly ContentRowRevision[] NoRows = [];
    static readonly RemapRule[] NoRules = [];
    static readonly ContentChunkRecord[] NoChunks = [];
    static readonly ManifestLanguageEntry[] NoLanguages = [];

    /// <summary>Builds one baseline. Every list is COPIED, so the baseline is immutable once it exists.</summary>
    /// <param name="versionNumber">The base version's number, or 0 on a database that has published none.</param>
    /// <param name="rows">Every row LIVE at the base version.</param>
    /// <param name="rules">The full ordered remap rule list as it stands.</param>
    /// <param name="chunks">Every <c>catalog_chunk</c> row the base version holds, at every side.</param>
    /// <param name="languages">The base version's text chunks, which a publish carries forward.</param>
    /// <param name="minimumServerBuild">The base version's minimum server build.</param>
    /// <param name="minimumClientBuild">The base version's minimum client build.</param>
    /// <exception cref="ArgumentNullException">A list, or an entry in one, is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A number is negative.</exception>
    public ContentPublishBaseline(
        int versionNumber,
        IReadOnlyList<ContentRowRevision> rows,
        IReadOnlyList<RemapRule> rules,
        IReadOnlyList<ContentChunkRecord> chunks,
        IReadOnlyList<ManifestLanguageEntry> languages,
        int minimumServerBuild,
        int minimumClientBuild)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(versionNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumServerBuild);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumClientBuild);

        VersionNumber = versionNumber;
        Rows = Copy(rows, nameof(rows));
        Rules = Copy(rules, nameof(rules));
        Chunks = Copy(chunks, nameof(chunks));
        Languages = Copy(languages, nameof(languages));
        MinimumServerBuild = minimumServerBuild;
        MinimumClientBuild = minimumClientBuild;
    }

    /// <summary>The EMPTY database: version 0, no rows, no rules, no chunks and both builds at 0.</summary>
    public static ContentPublishBaseline Empty { get; } =
        new(0, NoRows, NoRules, NoChunks, NoLanguages, 0, 0);

    /// <summary>The base version's number, 0 on a database that has published none.</summary>
    public int VersionNumber { get; }

    /// <summary>Every row live at the base version, which step 2 applies the draft's edits to.</summary>
    public IReadOnlyList<ContentRowRevision> Rows { get; }

    /// <summary>The full ordered rule list, which step 4 validates this publish's appends on top of.</summary>
    public IReadOnlyList<RemapRule> Rules { get; }

    /// <summary>Every chunk row the base version holds, which step 6 carries forward per SIDE.</summary>
    public IReadOnlyList<ContentChunkRecord> Chunks { get; }

    /// <summary>The base version's text chunks, which step 8 names in both manifests.</summary>
    public IReadOnlyList<ManifestLanguageEntry> Languages { get; }

    /// <summary>The base version's minimum server build, which a request that omits one carries forward.</summary>
    public int MinimumServerBuild { get; }

    /// <summary>The base version's minimum client build, which a request that omits one carries forward.</summary>
    public int MinimumClientBuild { get; }

    /// <summary>True when the base is an empty database, which is the one case step 4 passes a null previous.</summary>
    public bool IsEmpty => VersionNumber == 0 && Rows.Count == 0;

    /// <summary>
    /// The baseline the NEXT publish would see if this plan committed, which is what makes a chain of
    /// publishes testable before the commit of task 16 exists and what a store projects after committing.
    /// </summary>
    /// <param name="plan">A plan that validated.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="plan"/> did not validate, so no version follows it.</exception>
    public static ContentPublishBaseline After(ContentPublishPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsValid)
        {
            throw new ArgumentException(
                "A plan that did not validate publishes no version, so nothing follows it.", nameof(plan));
        }

        var chunks = new ContentChunkRecord[plan.Chunks.Count];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = plan.Chunks[i].AsReused();
        }

        return new ContentPublishBaseline(
            plan.VersionNumber,
            plan.LiveRows,
            plan.Rules,
            chunks,
            plan.Languages,
            plan.MinimumServerBuild,
            plan.MinimumClientBuild);
    }

    static T[] Copy<T>(IReadOnlyList<T> source, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        var copy = new T[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            copy[i] = source[i] ?? throw new ArgumentNullException(
                parameterName, FormattableString.Invariant($"Entry {i} is null."));
        }

        return copy;
    }
}

/// <summary>
/// What steps 1 to 8 produced: everything the commit of step 10 needs and nothing durable (spec 6.1).
/// <para>
/// <b>A plan is not a publish.</b> Nothing here has been written, so a caller that drops it leaves the store
/// exactly as it found it, which is what makes the whole of steps 1 to 8 retryable and what the crash tests
/// of spec 6.11 rely on.
/// </para>
/// <para>
/// <b>An INVALID plan stops where it failed.</b> A candidate that does not validate carries its findings and
/// no chunks and no manifests, because encoding bytes for a version nobody will publish is work for nothing.
/// A plan refused at step 7 by <c>KEC0014</c> carries the chunks it had already encoded and still no
/// manifests. Either way <see cref="IsValid"/> is false and both manifests are null.
/// </para>
/// </summary>
public sealed class ContentPublishPlan
{
    readonly Dictionary<ContentChunkAddress, ContentChunkRecord> _byAddress;

    /// <summary>Builds the plan the publisher hands back. Internal because the publisher is the only maker.</summary>
    internal ContentPublishPlan(
        int versionNumber,
        int baseVersion,
        ContentSnapshot candidate,
        ContentValidationReport validation,
        ContentIdAllocationRecord allocation,
        IReadOnlyList<ContentRowClose> closes,
        IReadOnlyList<ContentRowInsert> inserts,
        IReadOnlyList<ContentRowRevision> liveRows,
        IReadOnlyList<RemapRule> appendedRules,
        IReadOnlyList<RemapRule> rules,
        IReadOnlyList<ContentChunkRecord> chunks,
        IReadOnlyList<ManifestLanguageEntry> languages,
        string remapRuleChunkHash,
        ContentManifest? serverManifest,
        ContentManifest? clientManifest,
        string serverManifestHash,
        string clientManifestHash,
        int minimumServerBuild,
        int minimumClientBuild,
        IReadOnlyList<ContentEdit> frozenEdits)
    {
        FrozenEdits = frozenEdits;
        VersionNumber = versionNumber;
        BaseVersion = baseVersion;
        Candidate = candidate;
        Validation = validation;
        Allocation = allocation;
        Closes = closes;
        Inserts = inserts;
        LiveRows = liveRows;
        AppendedRules = appendedRules;
        Rules = rules;
        Chunks = chunks;
        Languages = languages;
        RemapRuleChunkHash = remapRuleChunkHash;
        ServerManifest = serverManifest;
        ClientManifest = clientManifest;
        ServerManifestHash = serverManifestHash;
        ClientManifestHash = clientManifestHash;
        MinimumServerBuild = minimumServerBuild;
        MinimumClientBuild = minimumClientBuild;

        _byAddress = new Dictionary<ContentChunkAddress, ContentChunkRecord>(chunks.Count);
        int written = 0;
        long bytes = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            ContentChunkRecord chunk = chunks[i];
            _byAddress[chunk.Address] = chunk;
            if (chunk.IsReused)
            {
                continue;
            }

            written++;
            bytes += chunk.StoredBytes;
        }

        ChunksWritten = written;
        ChunksReused = chunks.Count - written;
        BytesWritten = bytes;
    }

    /// <summary>The number this candidate was computed at, which is the base version plus one.</summary>
    public int VersionNumber { get; }

    /// <summary>The version the draft was based on, 0 on an empty database.</summary>
    public int BaseVersion { get; }

    /// <summary>
    /// The draft edits step 1 FROZE, in change-set order, which is exactly what step 10 deletes from the
    /// draft and nothing more.
    /// <para>
    /// The freeze marker is what makes that set stable, so scoping the delete to it can only matter when the
    /// marker failed to hold. That is the point: a commit that deleted the draft wholesale would take an edit
    /// it never published down with it, and an edit silently lost is worse than an edit that survives into
    /// the next draft.
    /// </para>
    /// </summary>
    public IReadOnlyList<ContentEdit> FrozenEdits { get; }

    /// <summary>The complete candidate, at <see cref="VersionNumber"/>, which step 4 swept.</summary>
    public ContentSnapshot Candidate { get; }

    /// <summary>Every finding, from step 4's sweep and step 7's client-encode check together.</summary>
    public ContentValidationReport Validation { get; }

    /// <summary>True when the candidate may be published. False leaves both manifests null.</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>What step 3 issued, and the high-water marks it seeded.</summary>
    public ContentIdAllocationRecord Allocation { get; }

    /// <summary>Every row revision this version CLOSES.</summary>
    public IReadOnlyList<ContentRowClose> Closes { get; }

    /// <summary>Every row revision this version INSERTS.</summary>
    public IReadOnlyList<ContentRowInsert> Inserts { get; }

    /// <summary>Every row LIVE at this version, which is the next publish's baseline rows.</summary>
    public IReadOnlyList<ContentRowRevision> LiveRows { get; }

    /// <summary>The rules this publish appends, in sequence order.</summary>
    public IReadOnlyList<RemapRule> AppendedRules { get; }

    /// <summary>The full ordered rule list as it will stand, which is what step 4 validated.</summary>
    public IReadOnlyList<RemapRule> Rules { get; }

    /// <summary>Every chunk row this version holds, written and reused together.</summary>
    public IReadOnlyList<ContentChunkRecord> Chunks { get; }

    /// <summary>Every language this version ships text for, carried forward from the base version.</summary>
    public IReadOnlyList<ManifestLanguageEntry> Languages { get; }

    /// <summary>The <c>KECR</c> rule chunk's content address, identical in both manifests.</summary>
    public string RemapRuleChunkHash { get; }

    /// <summary>The server manifest, or null when the plan did not validate.</summary>
    public ContentManifest? ServerManifest { get; }

    /// <summary>The client manifest, or null when the plan did not validate.</summary>
    public ContentManifest? ClientManifest { get; }

    /// <summary>The server manifest digest, lower hex, or empty when the plan did not validate.</summary>
    public string ServerManifestHash { get; }

    /// <summary>The client manifest digest, lower hex, or empty when the plan did not validate.</summary>
    public string ClientManifestHash { get; }

    /// <summary>The minimum server build this version declares, an INPUT to the manifest hash.</summary>
    public int MinimumServerBuild { get; }

    /// <summary>The minimum client build this version declares, an INPUT to the manifest hash.</summary>
    public int MinimumClientBuild { get; }

    /// <summary>The engine's own pack format generation, never supplied by a caller.</summary>
    public int FormatGeneration => ContentPackFormat.Generation;

    /// <summary>Chunks whose bytes were encoded, which is the download an adopting client pays.</summary>
    public int ChunksWritten { get; }

    /// <summary>Chunks carried forward unchanged, which a client already holding them refetches never.</summary>
    public int ChunksReused { get; }

    /// <summary>Stored bytes across every chunk this publish encoded.</summary>
    public long BytesWritten { get; }

    /// <summary>One chunk row by its full address.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="chunkIndex">The chunk's index within its type.</param>
    /// <param name="side">The side.</param>
    /// <param name="record">The row, when this version holds one.</param>
    public bool TryGetChunk(
        ContentTypeId type,
        int chunkIndex,
        ContentVisibility side,
        [MaybeNullWhen(false)] out ContentChunkRecord record)
        => _byAddress.TryGetValue(new ContentChunkAddress(type, chunkIndex, side), out record);
}
