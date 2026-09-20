using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The PUBLISHED half of the in-memory store: the baseline a publish is prepared against, the one commit
/// transaction, the family read, the whole publish, the snapshot load and the rollback draft.
/// <para>
/// It is a separate file from the draft and allocator half because the two answer different questions. The
/// other half is what a console does between publishes, and this one is what a publish does, which is also
/// the split every provider will carry: a draft edit is one statement and a publish is one transaction.
/// </para>
/// <para>
/// <b>Everything here runs under the same one gate the other half takes</b>, which is what a provider's
/// transaction does with a real connection behind it. The commit is the only member that moves the active
/// pointer, and it moves it as its LAST statement while still holding the gate, so a reader that sees the
/// new number sees every row, rule, chunk and audit entry of it.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore
{
    readonly List<RemapRule> _rules = [];
    readonly Dictionary<int, PublishedVersion> _published = [];
    ContentPublishCommit? _commit;

    /// <summary>
    /// The pack store a publish writes to, or null on a store that can hold a draft and allocate ids and
    /// cannot publish. A publish writes files before it writes rows, so the target is not optional for it.
    /// </summary>
    public IPackStore? PackStore { get; }

    /// <summary>The full ordered remap rule list as it stands, which is what a published version carries.</summary>
    public IReadOnlyList<RemapRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var families = new List<ContentFamily>();
            foreach (FamilyRecord held in _families.Values)
            {
                if (type.Value == 0 || held.Type == type)
                {
                    families.Add(held.ToFamily());
                }
            }

            families.Sort(static (left, right) => left.FamilyId.CompareTo(right.FamilyId));
            return Task.FromResult<IReadOnlyList<ContentFamily>>(families);
        }
    }

    /// <inheritdoc />
    public Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ClearStaleFreeze();
            return Task.FromResult(ReadBaseline());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The order inside the gate is spec 6.10's, and the last two statements are the ones that matter: the
    /// draft goes, and then the active pointer moves. Everything before them is invisible to a reader,
    /// because nothing reads a version row that no pointer names.
    /// <para>
    /// <b>It BUILDS every change into locals and then applies them in a tail that cannot throw</b>, because a
    /// gate is not a transaction. Both providers get atomicity from one database transaction, and a version
    /// row appended, rows closed and reopened, and rules extended with the pointer NOT moved is exactly the
    /// torn state the whole of section 6 is built around not having. Everything that can fail is above the
    /// tail: the clock the version row is stamped from, the diff the audit is rendered out of, and the draft
    /// the survivors are rebuilt into. The tail is list and dictionary writes and four field assignments.
    /// </para>
    /// </remarks>
    public Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        if (!plan.IsValid)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The candidate for version {plan.VersionNumber} carries {plan.Validation.Findings.Count} finding(s), so no transaction opened. A version the validator refused is a version a boot then fails closed on."),
                default,
                0,
                ContentAuthoringException.CandidateInvalidReason,
                plan.Validation.Findings);
        }

        lock (_gate)
        {
            // 1. CONFIRM the number rather than trusting it. The plan digested its version number into both
            // manifest hashes at step 8, so a base that moved underneath it means those hashes name a
            // version this store would be writing at a different number.
            int highest = HighestVersionNumber();
            if (plan.VersionNumber != highest + 1)
            {
                throw Moved(FormattableString.Invariant(
                    $"The plan publishes version {plan.VersionNumber} and the highest published version is {highest}, so the next number is {highest + 1}. Another publish landed under this plan and its manifest hashes already carry the wrong number."));
            }

            // The same statement about the rule list: a plan appends on top of the rules it was built over,
            // so this store's rules have to be that plan's own prefix or the sequences would collide.
            ContentRulePrefix.Require(_rules, plan);

            ContentPublishBaseline before = ReadBaseline();

            // BUILD. Nothing below this point until the tail touches a field of this store, so every one of
            // these may throw and leave the store standing exactly where it was.

            // 2. The version row, stamped from the clock, which is one of the two things here that can fail.
            var record = new ContentVersionRecord(
                plan.VersionNumber,
                plan.ServerManifestHash,
                plan.ClientManifestHash,
                plan.MinimumServerBuild,
                plan.MinimumClientBuild,
                plan.FormatGeneration,
                plan.BaseVersion,
                request.Actor,
                request.Note,
                _clock());

            // 3. Every temporal row change, onto a COPY: the closes first, so no insert is mistaken for the
            // revision it replaces while the walk is half done.
            var rows = new List<ContentRowRevision>(_rows);
            Close(rows, plan.Closes);
            Insert(rows, plan.Inserts);

            // 5. Every chunk row, ONE PER SIDE, the carried-forward ones included, so the version answers
            // "which chunks do I have, on which side" without recursing back through history.
            var published = new PublishedVersion(Reused(plan.Chunks), plan.Languages);

            // 6. Every audit row, field level, against the version the rows are leaving. The diff is the
            // other thing here that can fail.
            var staged = new List<ContentAuditEntry>();
            StagePublishAudit(staged, before, plan, request);

            // 6b. The applied ledger row, when this publish carries an upgrade. A duplicate id refuses HERE,
            // in the build, so the version and its history entry land together or neither does.
            ContentUpgradeRecord? upgrade = StageAppliedUpgrade(staged, request, plan.VersionNumber);

            // 7. The draft, scoped to the edits this plan FROZE. The freeze is what makes that the whole
            // draft, so anything else here survives rather than being deleted unpublished.
            ContentDraft? draft = DraftAfterCommit(plan);

            // APPLY. List and dictionary writes and four assignments, and the rule append at 4, which is a
            // walk of a list this store owns. Nothing here can refuse.
            _versions.Add(record);
            _rows.Clear();
            _rows.AddRange(rows);
            for (int i = _rules.Count; i < plan.Rules.Count; i++)
            {
                _rules.Add(plan.Rules[i]);
            }

            _published[plan.VersionNumber] = published;
            if (upgrade is not null)
            {
                _upgrades.Add(upgrade.Id, upgrade);
            }

            _audit.Commit(staged);
            _draft = draft;

            // 8. The active pointer, LAST. It moves for the NEXT boot: a running server keeps serving the
            // version it loaded.
            _activeVersion = plan.VersionNumber;
            return Task.FromResult(record);
        }
    }

    /// <inheritdoc />
    public async Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RequireCommit().PublishAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// It reads the version's pack back through <see cref="ContentPackReader"/> rather than rebuilding the
    /// snapshot from the row table, deliberately: what a server loads is the PACK, so a store that answered
    /// from its own rows could report a version whose bytes are unreadable as healthy.
    /// </remarks>
    public async Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ContentVersionRecord record;
        IPackStore pack;
        lock (_gate)
        {
            record = FindVersion(versionNumber) ?? throw UnknownVersion(versionNumber);
            pack = PackStore ?? throw NoPackStore(nameof(LoadSnapshotAsync));
        }

        ContentManifestRead manifest = await ContentPackReader
            .ReadManifestAsync(pack, record.ServerManifestHash, ContentManifestSide.Server, registry, cancellationToken)
            .ConfigureAwait(false);
        if (!manifest.Success || manifest.Manifest is null)
        {
            throw Unreadable(versionNumber, manifest.Hash, manifest.Reason);
        }

        var reader = new ContentPackReader(pack, registry, manifest.Manifest, manifest.Hash);
        ContentPackRead read = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return read.Success && read.Snapshot is not null
            ? read.Snapshot
            : throw Unreadable(versionNumber, read.Hash, read.Reason);
    }

    /// <inheritdoc />
    /// <remarks>
    /// It BUILDS A DRAFT and publishes nothing, so an operator reviews the diff first. A row live at the
    /// target and retired since is a flat refusal, because a retire is irreversible for pages already
    /// migrated past it.
    /// </remarks>
    public async Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        ContentRollbackPlan plan;
        int from;
        lock (_gate)
        {
            if (FindVersion(targetVersion) is null)
            {
                throw UnknownVersion(targetVersion);
            }

            from = _activeVersion;
            plan = ContentRollback.Prepare(
                targetVersion, LiveAt(targetVersion), from, LiveAt(from), _rules, _registry);
        }

        if (plan.IsBlocked)
        {
            throw ContentRollback.Refusal(plan);
        }

        // STAGED before the edits land, so a rollback whose own audit row cannot be rendered leaves no draft
        // behind. The edits carry their own staged entries through ApplyEditsAsync, and this one is committed
        // after them so the ledger reads in the order the actions happened.
        var staged = new List<ContentAuditEntry>(1);
        lock (_gate)
        {
            _audit.Stage(
                staged,
                ContentAuditActions.Rollback,
                actor,
                operatorId,
                default,
                0,
                default,
                string.Empty,
                InMemoryContentAuditLog.Render(from),
                InMemoryContentAuditLog.Render(targetVersion),
                0,
                note);
        }

        ContentDraft draft = await ApplyEditsAsync(plan.Edits, actor, operatorId, note, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _audit.Commit(staged);
        }

        return draft;
    }

    /// <summary>The baseline as it stands. The caller already holds the gate.</summary>
    ContentPublishBaseline ReadBaseline()
    {
        ContentVersionRecord? record = FindVersion(_activeVersion);
        PublishedVersion? held = _published.TryGetValue(_activeVersion, out PublishedVersion? version)
            ? version
            : null;

        return new ContentPublishBaseline(
            _activeVersion,
            LiveAt(_activeVersion),
            _rules,
            held?.Chunks ?? [],
            held?.Languages ?? [],
            record?.MinimumServerBuild ?? 0,
            record?.MinimumClientBuild ?? 0);
    }

    /// <summary>Every row revision live at one version. The caller already holds the gate.</summary>
    List<ContentRowRevision> LiveAt(int versionNumber)
    {
        var rows = new List<ContentRowRevision>();
        for (int i = 0; i < _rows.Count; i++)
        {
            if (IsLiveAt(_rows[i], versionNumber))
            {
                rows.Add(_rows[i]);
            }
        }

        return rows;
    }

    /// <summary>The commit half, built once over the pack target this store was handed.</summary>
    ContentPublishCommit RequireCommit()
    {
        lock (_gate)
        {
            if (_commit is not null)
            {
                return _commit;
            }

            IPackStore pack = PackStore ?? throw NoPackStore(nameof(PublishAsync));
            _commit = new ContentPublishCommit(this, pack, new ContentPublisher(this, this, _registry));
            return _commit;
        }
    }

    int HighestVersionNumber()
    {
        int highest = 0;
        for (int i = 0; i < _versions.Count; i++)
        {
            if (_versions[i].VersionNumber > highest)
            {
                highest = _versions[i].VersionNumber;
            }
        }

        return highest;
    }

    /// <summary>Applies a plan's closes to a row list, which is the commit's own COPY and never the store's.</summary>
    /// <param name="rows">The row list being built.</param>
    /// <param name="closes">The revisions this version closes.</param>
    static void Close(List<ContentRowRevision> rows, IReadOnlyList<ContentRowClose> closes)
    {
        for (int c = 0; c < closes.Count; c++)
        {
            ContentRowClose close = closes[c];
            for (int i = 0; i < rows.Count; i++)
            {
                ContentRowRevision revision = rows[i];
                if (revision.Row.Type != close.Type
                    || revision.Row.Id != close.DefinitionId
                    || revision.ValidFromVersion != close.ValidFromVersion
                    || revision.ReplacedInVersion is not null)
                {
                    continue;
                }

                rows[i] = revision with { ReplacedInVersion = close.ReplacedInVersion };
                break;
            }
        }
    }

    /// <summary>Appends a plan's inserts to the commit's own row copy.</summary>
    /// <param name="rows">The row list being built.</param>
    /// <param name="inserts">The revisions this version inserts.</param>
    static void Insert(List<ContentRowRevision> rows, IReadOnlyList<ContentRowInsert> inserts)
    {
        for (int i = 0; i < inserts.Count; i++)
        {
            ContentRowInsert insert = inserts[i];
            rows.Add(new ContentRowRevision(insert.Row, insert.ValidFromVersion, null, insert.FamilyId));
        }
    }

    /// <summary>
    /// The version's chunk rows in the form a carry forward reads: the hash and the sizes without the bytes,
    /// because the bytes are in the pack store and holding a second copy of the whole catalog per version is
    /// what would make this store useless for the suites that publish a hundred times.
    /// </summary>
    static ContentChunkRecord[] Reused(IReadOnlyList<ContentChunkRecord> chunks)
    {
        var reused = new ContentChunkRecord[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            reused[i] = chunks[i].AsReused();
        }

        return reused;
    }

    /// <summary>
    /// The publish audit, one row per CHANGED FIELD, which is spec 4.6's unit. A change that moved no field
    /// value, a retire, writes one row-level entry naming the operation instead, so it still leaves a trace.
    /// <para>
    /// It STAGES rather than appending, so the diff and the clock reads happen while the commit can still be
    /// abandoned whole. <see cref="InMemoryContentAuditLog.Commit"/> in the commit's tail is what lands them.
    /// </para>
    /// </summary>
    /// <param name="staged">The staging list every entry is rendered into.</param>
    /// <param name="before">The baseline the rows are leaving.</param>
    /// <param name="plan">The plan being committed.</param>
    /// <param name="request">The publish request, whose actor, operator and note every entry carries.</param>
    void StagePublishAudit(
        List<ContentAuditEntry> staged,
        ContentPublishBaseline before,
        ContentPublishPlan plan,
        ContentPublishRequest request)
    {
        ContentDiff diff = ContentDiff.Between(
            before.VersionNumber, before.Rows, plan.VersionNumber, plan.LiveRows, _registry);

        for (int i = 0; i < diff.Changes.Count; i++)
        {
            ContentDiffEntry entry = diff.Changes[i];
            if (entry.Fields.Count == 0)
            {
                _audit.Stage(
                    staged,
                    ContentAuditActions.Publish,
                    request.Actor,
                    request.Operator,
                    entry.Type,
                    entry.Id,
                    entry.Key,
                    string.Empty,
                    null,
                    entry.Operation.ToString(),
                    plan.VersionNumber,
                    request.Note);
                continue;
            }

            for (int f = 0; f < entry.Fields.Count; f++)
            {
                ContentDiffField field = entry.Fields[f];
                _audit.Stage(
                    staged,
                    ContentAuditActions.Publish,
                    request.Actor,
                    request.Operator,
                    entry.Type,
                    entry.Id,
                    entry.Key,
                    field.Field,
                    field.Before,
                    field.After,
                    plan.VersionNumber,
                    request.Note);
            }
        }
    }

    static ContentAuthoringException Moved(string message)
        => new(message, default, 0, ContentAuthoringException.BaseVersionMovedReason);

    static ContentAuthoringException UnknownVersion(int versionNumber)
        => new(
            FormattableString.Invariant($"This store holds no version {versionNumber}."),
            default,
            0,
            ContentAuthoringException.UnknownVersionReason);

    static ContentAuthoringException NoPackStore(string member)
        => new(
            FormattableString.Invariant(
                $"{nameof(InMemoryContentAuthoringStore)}.{member} needs a pack store and this store was built with none. A publish writes files before it writes rows, so the pack target is not optional for it."),
            default,
            0,
            ContentAuthoringException.NoPackStoreReason);

    static ContentAuthoringException Unreadable(int versionNumber, string? hash, string? reason)
        => new(
            FormattableString.Invariant(
                $"Version {versionNumber}'s pack could not be read at object '{hash}': {reason}."),
            default,
            0,
            ContentAuthoringException.PackUnreadableReason);

    /// <summary>
    /// One published version's pack-side facts, which no other table holds: the chunk rows it carries at
    /// every side, and the text chunks it names. Both are carried FORWARD by the next publish, so the store
    /// keeps them per version rather than only for the active one.
    /// </summary>
    /// <param name="Chunks">Every chunk row of the version, in the reused form a carry forward reads.</param>
    /// <param name="Languages">The version's text chunks, one per language.</param>
    sealed record PublishedVersion(
        IReadOnlyList<ContentChunkRecord> Chunks,
        IReadOnlyList<ManifestLanguageEntry> Languages);
}
