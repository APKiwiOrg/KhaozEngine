using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// A TEST AND TOOLING implementation of <see cref="IContentAuthoringStore"/> holding the whole catalog in
/// memory. <b>A production host uses a provider</b>, because nothing here survives the process.
/// <para>
/// It lives in this package rather than in a test project for the same reason
/// <c>KhaozEngine.Commerce.InMemoryWalletStore</c> does: the draft, the allocator, the publish and the
/// admin-action suites all need one, and those suites sit in different assemblies, so a copy inside any one
/// of them could not be reached from the others.
/// </para>
/// <para>
/// It carries the constraints its provider siblings get from a <c>CHECK</c>, so a defect surfaces here
/// rather than at the first SQL run: a high-water mark never moves backwards, an issued mark never passes a
/// reserved one, and a family block is aligned to its own size.
/// </para>
/// <para>
/// <b>Not thread safe across its own awaits.</b> Every member completes synchronously under one gate, which
/// is what a provider's connection lease does with a real connection behind it.
/// </para>
/// </summary>
public sealed class InMemoryContentAuthoringStore : IContentAuthoringStore, IContentIdPersistence
{
    /// <summary>The schema version this store reports, matching the providers' first migration.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The cap a page read is clamped to, which the seam leaves to the implementation.</summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// The active pointer of a database that has published nothing: a version number with no version behind
    /// it, which every read of the live set resolves against.
    /// </summary>
    public const int NoActiveVersion = 0;

    readonly object _gate = new();
    readonly ContentTypeRegistry _registry;
    readonly ContentIdAllocator _allocator;
    readonly Func<DateTimeOffset> _clock;
    readonly string _storeEpoch = Guid.NewGuid().ToString("N");
    readonly Dictionary<ushort, ContentIdHighWater> _highWater = [];
    readonly Dictionary<long, FamilyRecord> _families = [];
    readonly List<ContentVersionRecord> _versions = [];
    readonly List<ContentRowRevision> _rows = [];
    readonly InMemoryContentAuditLog _audit;
    long _nextFamilyId = 1;

    // The published half. A publish is the only thing that moves the pointer, appends a version row or
    // writes a temporal row, so on a store whose publish pipeline is not wired in these stay as they start.
    int _activeVersion = NoActiveVersion;
    int? _pinnedVersion;
    ContentDraft? _draft;

    /// <summary>Builds an empty store over one registry.</summary>
    /// <param name="registry">The registry this store's types are declared in, which is where a type's id ceiling comes from. Per instance, never ambient.</param>
    /// <param name="clock">The clock every stamp is read from, or null for the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public InMemoryContentAuthoringStore(ContentTypeRegistry registry, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _audit = new InMemoryContentAuditLog(_clock);
        _allocator = new ContentIdAllocator(this);
    }

    /// <summary>The allocator this store hands its two allocation members to.</summary>
    public ContentIdAllocator Allocator => _allocator;

    /// <inheritdoc />
    /// <remarks>An in-memory store is created WITH its schema, so both modes succeed. The empty-database
    /// refusal of <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> is a provider behaviour, and it
    /// needs a database that can be empty.</remarks>
    public Task InitializeAsync(
        ContentAuthoringSchemaMode mode,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(SchemaVersion);

    /// <inheritdoc />
    public Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_storeEpoch);

    /// <inheritdoc />
    public Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_activeVersion);
        }
    }

    /// <inheritdoc />
    public Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_pinnedVersion);
        }
    }

    /// <inheritdoc />
    public Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        lock (_gate)
        {
            if (version is int held && FindVersion(held) is null)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Version {held} does not exist, so it cannot be pinned."),
                    default,
                    0,
                    ContentAuthoringException.UnknownTypeReason);
            }

            string? before = InMemoryContentAuditLog.Render(_pinnedVersion);
            _pinnedVersion = version;
            _audit.Append(
                ContentAuditActions.Pin,
                actor,
                operatorId,
                default,
                0,
                default,
                string.Empty,
                before,
                InMemoryContentAuditLog.Render(version),
                0,
                string.Empty);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var newestFirst = new List<ContentVersionRecord>(_versions);
            newestFirst.Sort(static (left, right) => right.VersionNumber.CompareTo(left.VersionNumber));
            return Task.FromResult<IReadOnlyList<ContentVersionRecord>>(newestFirst);
        }
    }

    /// <inheritdoc />
    public Task<ContentVersionRecord?> GetVersionAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(FindVersion(versionNumber));
        }
    }

    /// <inheritdoc />
    /// <remarks>The snapshot is built by the publish pipeline, which owns the candidate and the codecs, so
    /// this store answers it only once that pipeline is wired into it.</remarks>
    public Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
        => throw NotYetPublishing(nameof(LoadSnapshotAsync));

    /// <inheritdoc />
    public Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_draft);
        }
    }

    /// <inheritdoc />
    public Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        lock (_gate)
        {
            // Every edit is checked against the schema BEFORE any of them is applied, and the change set is
            // built on a COPY, so one refusal leaves the open draft exactly as it was. A batch save from a
            // grid lands whole or not at all.
            for (int i = 0; i < edits.Count; i++)
            {
                CheckAgainstSchema(edits[i]);
            }

            ContentDraft open = _draft
                ?? new ContentDraft(_activeVersion, actor, _clock(), note, new ContentChangeSet());
            var working = new ContentChangeSet(open.Changes.Edits);
            for (int i = 0; i < edits.Count; i++)
            {
                working.Apply(edits[i]);
            }

            _draft = new ContentDraft(
                open.BaseVersion,
                open.OpenedBy,
                open.OpenedAtUtc,
                note.Length == 0 ? open.Note : note,
                working);

            for (int i = 0; i < edits.Count; i++)
            {
                _audit.AppendEdit(edits[i], actor, operatorId, note);
            }

            return Task.FromResult(_draft);
        }
    }

    /// <inheritdoc />
    public Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        lock (_gate)
        {
            int discarded = _draft?.EditCount ?? 0;
            _draft = null;
            _audit.Append(
                ContentAuditActions.DraftDiscard,
                actor,
                operatorId,
                default,
                0,
                default,
                string.Empty,
                InMemoryContentAuditLog.Render(discarded),
                null,
                0,
                string.Empty);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    /// <remarks>Publishing is the ordered pipeline of spec 6, which owns the candidate, the chunk bytes and
    /// both manifests. This store answers it only once that pipeline is wired into it.</remarks>
    public Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => throw NotYetPublishing(nameof(PublishAsync));

    /// <inheritdoc />
    /// <remarks>A rollback is a DRAFT built from a published version's field values, so it needs the same
    /// pipeline the publish does.</remarks>
    public Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => throw NotYetPublishing(nameof(RollbackToAsync));

    /// <inheritdoc />
    /// <remarks>Version 0 reads the ACTIVE version's live set. The draft-applied overlay the seam describes
    /// is the publish pipeline's candidate, so it arrives with that pipeline rather than being approximated
    /// here.</remarks>
    public Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);

        lock (_gate)
        {
            int at = versionNumber == 0 ? _activeVersion : versionNumber;
            var matched = new List<ContentRow>();
            for (int i = 0; i < _rows.Count; i++)
            {
                ContentRowRevision revision = _rows[i];
                if (revision.Row.Type != type || !IsLiveAt(revision, at))
                {
                    continue;
                }

                if (!includeRetired && revision.Row.IsRetired)
                {
                    continue;
                }

                if (keyPrefix is not null
                    && !revision.Row.Key.ToString().StartsWith(keyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                matched.Add(revision.Row);
            }

            matched.Sort(static (left, right) => left.Id.CompareTo(right.Id));
            return Task.FromResult(new ContentRowPage(at, matched.Count, Page(matched, skip, take)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var history = new List<ContentRowRevision>();
            for (int i = 0; i < _rows.Count; i++)
            {
                ContentRowRevision revision = _rows[i];
                if (revision.Row.Type == type && revision.Row.Id == definitionId)
                {
                    history.Add(revision);
                }
            }

            history.Sort(static (left, right) => left.ValidFromVersion.CompareTo(right.ValidFromVersion));
            return Task.FromResult<IReadOnlyList<ContentRowRevision>>(history);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);

        lock (_gate)
        {
            return Task.FromResult(_audit.List(type, definitionId, skip, take, MaxPageSize));
        }
    }

    /// <inheritdoc />
    public Task<int> AllocateAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default)
    {
        RequireType(type);
        return _allocator.AllocateAsync(type, count, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
        => _allocator.AllocateInFamilyAsync(familyId, cancellationToken);

    /// <inheritdoc />
    public async Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(familyKey);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        RequireType(type);

        long familyId;
        lock (_gate)
        {
            if (!ContentFamily.IsLegalBlockSize(blockSize))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Family '{familyKey}' declares a block size of {blockSize}, and a block size is a power of two between {ContentFamily.MinBlockSize} and {ContentFamily.MaxBlockSize}. It is fixed at creation, because changing it would move every id in the family."),
                    type,
                    0,
                    ContentAuthoringException.FamilyDeclarationReason);
            }

            foreach (FamilyRecord held in _families.Values)
            {
                if (held.Type == type && string.Equals(held.FamilyKey, familyKey, StringComparison.Ordinal))
                {
                    throw new ContentAuthoringException(
                        FormattableString.Invariant(
                            $"Content type {type.Value} already carries a family keyed '{familyKey}', and a family key is unique within its type."),
                        type,
                        0,
                        ContentAuthoringException.FamilyDeclarationReason);
                }
            }

            familyId = _nextFamilyId++;
            _families.Add(familyId, new FamilyRecord(familyId, type, familyKey, blockSize, _activeVersion));
        }

        try
        {
            // A family reserves a block at creation (contracts 5.2), through the same reserve-then-issue
            // path a full family takes for its next one.
            await _allocator.ReserveBlockAsync(familyId, cancellationToken).ConfigureAwait(false);
        }
        catch (ContentAuthoringException)
        {
            // The reservation is its own commit and it refused, so the family row goes with it and the
            // caller is left with nothing written rather than a family that can never issue an id.
            lock (_gate)
            {
                _families.Remove(familyId);
            }

            throw;
        }

        lock (_gate)
        {
            ContentFamily created = _families[familyId].ToFamily();
            _audit.Append(
                ContentAuditActions.FamilyCreate,
                actor,
                operatorId,
                type,
                0,
                new ContentKey(familyKey),
                string.Empty,
                null,
                InMemoryContentAuditLog.Render(created.Blocks[0].BaseId),
                0,
                string.Empty);
            return created;
        }
    }

    /// <inheritdoc />
    /// <remarks>An import is a publish of the whole bundle as version 1, so it needs the publish pipeline.</remarks>
    public Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => throw NotYetPublishing(nameof(ImportBundleAsync));

    /// <inheritdoc />
    /// <remarks>An export reads a published version's rows, which arrive with the publish pipeline.</remarks>
    public Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => throw NotYetPublishing(nameof(ExportBundleAsync));

    /// <inheritdoc />
    public Task<ContentIdHighWater> ReadHighWaterAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Mark(type));
        }
    }

    /// <inheritdoc />
    public Task<int?> ReadMaxDefinitionIdAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => Task.FromResult(
            _registry.TryGet(type, out ContentTypeRegistration? registration)
                ? registration.MaxDefinitionId
                : null);

    /// <inheritdoc />
    public Task CommitReservedThroughAsync(
        ContentTypeId type,
        int reservedThrough,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ContentIdHighWater mark = Mark(type);
            if (reservedThrough < mark.ReservedThrough)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reservedThrough),
                    reservedThrough,
                    FormattableString.Invariant(
                        $"Content type {type.Value} has reserved through {mark.ReservedThrough}, and a durable promise is never taken back."));
            }

            _highWater[type.Value] = mark with { ReservedThrough = reservedThrough };
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task CommitIssuedThroughAsync(
        ContentTypeId type,
        int issuedThrough,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _highWater[type.Value] = WithIssued(type, Mark(type), issuedThrough);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<ContentFamily?> ReadFamilyAsync(
        long familyId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _families.TryGetValue(familyId, out FamilyRecord? held) ? held.ToFamily() : null);
        }
    }

    /// <inheritdoc />
    public Task<ContentFamilyBlock> CommitFamilyBlockAsync(
        long familyId,
        int baseId,
        int issuedThrough,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            FamilyRecord family = RequireFamily(familyId);
            var block = new ContentFamilyBlock(
                familyId,
                family.Blocks.Count,
                baseId,
                family.BlockSize,
                baseId,
                _activeVersion);

            // The block insert and the advance of the issued mark land together, because a block row written
            // without the advance would leave the plain counter walking into the new block.
            family.Blocks.Add(block);
            _highWater[family.Type.Value] = WithIssued(family.Type, Mark(family.Type), issuedThrough);
            return Task.FromResult(block);
        }
    }

    /// <inheritdoc />
    public Task CommitFamilyNextFreeIdAsync(
        long familyId,
        int blockOrdinal,
        int nextFreeId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            FamilyRecord family = RequireFamily(familyId);
            ContentFamilyBlock block = family.Blocks[blockOrdinal];
            if (nextFreeId < block.NextFreeId || nextFreeId > block.TopExclusive)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nextFreeId),
                    nextFreeId,
                    FormattableString.Invariant(
                        $"Block {blockOrdinal} of family {familyId} spans [{block.BaseId}, {block.TopExclusive}) and stands at {block.NextFreeId}."));
            }

            family.Blocks[blockOrdinal] = block with { NextFreeId = nextFreeId };
            return Task.CompletedTask;
        }
    }

    static IReadOnlyList<T> Page<T>(List<T> matched, int skip, int take)
    {
        int from = Math.Min(skip, matched.Count);
        int count = Math.Min(Math.Min(take, MaxPageSize), matched.Count - from);
        return matched.GetRange(from, count);
    }

    static bool IsLiveAt(ContentRowRevision revision, int versionNumber)
        => revision.ValidFromVersion <= versionNumber
            && (revision.ReplacedInVersion is not int replaced || replaced > versionNumber);

    static NotSupportedException NotYetPublishing(string member)
        => new(FormattableString.Invariant(
            $"{nameof(InMemoryContentAuthoringStore)}.{member} needs the publish pipeline, which is not wired into this store yet."));

    ContentIdHighWater WithIssued(ContentTypeId type, ContentIdHighWater mark, int issuedThrough)
    {
        if (issuedThrough < mark.IssuedThrough)
        {
            throw new ArgumentOutOfRangeException(
                nameof(issuedThrough),
                issuedThrough,
                FormattableString.Invariant(
                    $"Content type {type.Value} has issued through {mark.IssuedThrough}, and an issued mark never moves backwards, because an id is never reused."));
        }

        if (issuedThrough > mark.ReservedThrough)
        {
            throw new ArgumentOutOfRangeException(
                nameof(issuedThrough),
                issuedThrough,
                FormattableString.Invariant(
                    $"Content type {type.Value} has reserved through {mark.ReservedThrough}, so issuing through {issuedThrough} would hand out an id nothing has promised to keep. Reserve before issue."));
        }

        return mark with { IssuedThrough = issuedThrough };
    }

    ContentIdHighWater Mark(ContentTypeId type)
        => _highWater.TryGetValue(type.Value, out ContentIdHighWater mark) ? mark : default;

    ContentVersionRecord? FindVersion(int versionNumber)
    {
        for (int i = 0; i < _versions.Count; i++)
        {
            if (_versions[i].VersionNumber == versionNumber)
            {
                return _versions[i];
            }
        }

        return null;
    }

    FamilyRecord RequireFamily(long familyId)
        => _families.TryGetValue(familyId, out FamilyRecord? held)
            ? held
            : throw new ContentAuthoringException(
                FormattableString.Invariant($"Family {familyId} is not in the store."),
                default,
                0,
                ContentAuthoringException.UnknownFamilyReason);

    ContentTypeRegistration RequireType(ContentTypeId type)
        => _registry.TryGet(type, out ContentTypeRegistration? registration)
            ? registration
            : throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {type.Value} is not registered, so this store carries no declaration for it."),
                type,
                0,
                ContentAuthoringException.UnknownTypeReason);

    void CheckAgainstSchema(ContentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        ContentTypeRegistration registration = RequireType(edit.Type);
        for (int i = 0; i < edit.Fields.Count; i++)
        {
            ContentFieldEdit field = edit.Fields[i];
            if (!registration.Schema.TryGet(field.Name, out ContentFieldEntry? entry))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Content type {registration.Type.Value} '{registration.TypeKey}' declares no field named '{field.Name}', so the edit is refused at the boundary rather than at publish."),
                    edit.Type,
                    edit.DefinitionId,
                    ContentAuthoringException.UnknownFieldReason);
            }

            if (entry.IsDerivedMarker)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Field '{field.Name}' of content type {registration.Type.Value} '{registration.TypeKey}' is a derived marker, whose key is a function of the row it sits on, so an edit cannot author a value for it."),
                    edit.Type,
                    edit.DefinitionId,
                    ContentAuthoringException.UnknownFieldReason);
            }
        }
    }

    /// <summary>
    /// One family as this store holds it: immutable declaration plus the MUTABLE ordered block list the
    /// allocator advances. <see cref="ContentFamily"/> itself is immutable, so it is projected on every read.
    /// </summary>
    sealed class FamilyRecord(long familyId, ContentTypeId type, string familyKey, int blockSize, int createdInVersion)
    {
        public ContentTypeId Type { get; } = type;

        public string FamilyKey { get; } = familyKey;

        public int BlockSize { get; } = blockSize;

        public List<ContentFamilyBlock> Blocks { get; } = [];

        public ContentFamily ToFamily()
            => new(familyId, Type, FamilyKey, BlockSize, false, createdInVersion, Blocks);
    }
}
