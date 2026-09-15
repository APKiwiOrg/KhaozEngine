using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The BUNDLE half of the in-memory store: the lossless export of one version, and the import that seeds an
/// empty store from one.
/// <para>
/// <b>An import runs through the ordinary publish and there is no second mechanism.</b> It turns the bundle
/// into a draft of <c>Add</c> edits, some carrying their own id and some not, and publishes it as version 1.
/// That is what keeps the id rule a single sentence: a row that names an id is imported with it, a row that
/// names none is allocated one in edit ordinal order, and a bundle may mix the two.
/// </para>
/// <para>
/// <b>Into an EMPTY store only.</b> Empty means the version table holds no rows, and a non-empty one is
/// refused with nothing written. That single rule is the answer to a whole class of seeding defect, where a
/// seed that runs repeatedly against live data either reverts an operator's value on the next deploy or is
/// beaten forever by a stored row.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// The families, the id marks and the restamped rules are written BEFORE the publish, because the edits
    /// and the baseline both need them. Any refusal after that point resets this store to the empty state it
    /// was required to be in, so a caller that catches one is holding a store it may import into again. Files
    /// a failed attempt already wrote to the pack store are ordinary orphans and the next sweep takes them.
    /// </remarks>
    public async Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        IReadOnlyList<ContentEdit> edits;
        lock (_gate)
        {
            if (_versions.Count > 0)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"This store already stands at version {_activeVersion}, and a bundle is imported into an EMPTY store only. A deployed catalog's values change through an edit and a publish and through nothing else."),
                    default,
                    0,
                    ContentAuthoringException.CatalogNotEmptyReason);
            }

            if (PackStore is null)
            {
                throw NoPackStore(nameof(ImportBundleAsync));
            }

            RequireTypesAgree(bundle);
            RestoreFamilies(bundle);
            SeedMarks(bundle);
            Restamp(bundle);
            edits = Edits(bundle);
        }

        try
        {
            await ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken).ConfigureAwait(false);
            ContentPublishResult published = await PublishAsync(
                new ContentPublishRequest(actor, operatorId, note, 0), cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _audit.Append(
                    ContentAuditActions.BulkImport,
                    actor,
                    operatorId,
                    default,
                    0,
                    default,
                    string.Empty,
                    null,
                    InMemoryContentAuditLog.Render(bundle.Rows.Count),
                    published.VersionNumber,
                    note);
            }

            return published;
        }
        catch (ContentAuthoringException)
        {
            ResetToEmpty();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FindVersion(versionNumber) is null)
            {
                throw UnknownVersion(versionNumber);
            }

            var types = new List<ContentBundleType>();
            IReadOnlyList<ContentTypeRegistration> registrations = _registry.ByTypeId;
            for (int i = 0; i < registrations.Count; i++)
            {
                ContentTypeRegistration registration = registrations[i];
                types.Add(new ContentBundleType(
                    registration.Type,
                    registration.TypeKey,
                    registration.DefaultVisibility,
                    registration.ChunkSlots,
                    registration.MaxDefinitionId,
                    registration.Schema));
            }

            var families = new List<ContentFamily>();
            var familyKeys = new Dictionary<long, string>();
            foreach (FamilyRecord held in _families.Values)
            {
                ContentFamily family = held.ToFamily();
                families.Add(family);
                familyKeys[family.FamilyId] = family.FamilyKey;
            }

            families.Sort(static (left, right) => left.FamilyId.CompareTo(right.FamilyId));

            List<ContentRowRevision> live = LiveAt(versionNumber);
            live.Sort(static (left, right) => left.Row.Type.Value == right.Row.Type.Value
                ? left.Row.Id.CompareTo(right.Row.Id)
                : left.Row.Type.Value.CompareTo(right.Row.Type.Value));

            var rows = new List<ContentBundleRow>(live.Count);
            for (int i = 0; i < live.Count; i++)
            {
                rows.Add(Export(live[i], familyKeys));
            }

            var rules = new List<RemapRule>();
            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].IntroducedIn <= versionNumber)
                {
                    rules.Add(_rules[i]);
                }
            }

            return Task.FromResult(new ContentBundle(
                ContentBundle.CurrentFormatVersion, _storeEpoch, versionNumber, types, rows, families, rules));
        }
    }

    /// <summary>
    /// One live row as a bundle carries it. The id is always NAMED on an export, because that is what makes
    /// an import reproduce the same ids and therefore makes an adoption a no-op for stored player data.
    /// <para>
    /// A DERIVED marker field is skipped: its key is a function of the row it sits on, so an edit cannot
    /// author a value for it and an import carrying one would be refused at the boundary.
    /// </para>
    /// </summary>
    ContentBundleRow Export(ContentRowRevision revision, Dictionary<long, string> familyKeys)
    {
        ContentRow row = revision.Row;
        ContentTypeRegistration registration = RequireType(row.Type);
        IReadOnlyList<ContentFieldEntry> schema = registration.Schema.Fields;
        var fields = new List<ContentFieldEdit>(schema.Count);
        for (int i = 0; i < schema.Count && i < row.Fields.Count; i++)
        {
            if (!schema[i].IsDerivedMarker)
            {
                fields.Add(new ContentFieldEdit(schema[i].Name, row.Fields[i]));
            }
        }

        string? familyKey = revision.FamilyId is long familyId && familyKeys.TryGetValue(familyId, out string? key)
            ? key
            : null;

        return new ContentBundleRow(row.Type, row.Id, row.Key, row.IsRetired, familyKey, fields);
    }

    /// <summary>
    /// Every bundle type must be registered HERE, under the same key and the same chunk slot count. A codec
    /// is code and no document can carry one, so an import registers nothing: it checks that the declaration
    /// the bundle was exported under and the one this process holds are the same declaration.
    /// </summary>
    void RequireTypesAgree(ContentBundle bundle)
    {
        for (int i = 0; i < bundle.Types.Count; i++)
        {
            ContentBundleType declared = bundle.Types[i];
            if (!_registry.TryGet(declared.Type, out ContentTypeRegistration? registration))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"The bundle carries content type {declared.Type.Value} '{declared.TypeKey}', which this process has not registered. A codec is code, so an import needs the type already declared against the codec that decodes it."),
                    declared.Type,
                    0,
                    ContentAuthoringException.UnknownTypeReason);
            }

            if (!string.Equals(registration.TypeKey, declared.TypeKey, StringComparison.Ordinal)
                || registration.ChunkSlots != declared.ChunkSlots)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"The bundle declares content type {declared.Type.Value} as '{declared.TypeKey}' with {declared.ChunkSlots} chunk slots and this process registers it as '{registration.TypeKey}' with {registration.ChunkSlots}. The two declarations disagree about where an id falls, so the import is refused whole."),
                    declared.Type,
                    0,
                    ContentAuthoringException.BundleFormatReason);
            }
        }
    }

    /// <summary>
    /// The families and their blocks VERBATIM, ids included, because a family is what makes a row's id
    /// membership answerable and an import that reallocated blocks would move every id in one.
    /// </summary>
    void RestoreFamilies(ContentBundle bundle)
    {
        for (int i = 0; i < bundle.Families.Count; i++)
        {
            ContentFamily family = bundle.Families[i];
            _ = RequireType(family.Type);

            var record = new FamilyRecord(
                family.FamilyId, family.Type, family.FamilyKey, family.BlockSize, family.CreatedInVersion);
            for (int b = 0; b < family.Blocks.Count; b++)
            {
                record.Blocks.Add(family.Blocks[b]);
            }

            _families[family.FamilyId] = record;
            _nextFamilyId = Math.Max(_nextFamilyId, family.FamilyId + 1);
        }
    }

    /// <summary>
    /// Both marks move to the top of every restored block and every carried id, so the plain counter cannot
    /// walk into a family's block or onto a row the bundle already named. The publish seeds from the carried
    /// ids again afterwards, which is a no-op once these are in.
    /// <para>
    /// <b>It runs BEFORE the allocation rather than after it</b>, which is the one place this diverges from
    /// spec 6.3's written order, and it has to. A bundle may MIX named and unnamed ids, so an unnamed row
    /// allocated from a counter that has not yet seen the carried ids lands straight on one of them and
    /// <c>KEC0036</c> refuses the whole import. Nothing spec 6.3 claims is lost: its reproducibility sentence
    /// is scoped to an ID-FREE bundle, which raises no mark here and still allocates 1 upward in row order.
    /// </para>
    /// </summary>
    void SeedMarks(ContentBundle bundle)
    {
        var highest = new Dictionary<ushort, int>();
        for (int i = 0; i < bundle.Families.Count; i++)
        {
            ContentFamily family = bundle.Families[i];
            for (int b = 0; b < family.Blocks.Count; b++)
            {
                Raise(highest, family.Type.Value, family.Blocks[b].TopExclusive - 1);
            }
        }

        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ContentBundleRow row = bundle.Rows[i];
            if (row.Id is int id)
            {
                Raise(highest, row.Type.Value, id);
            }
        }

        foreach (KeyValuePair<ushort, int> mark in highest)
        {
            var type = new ContentTypeId(mark.Key);
            ContentIdHighWater held = Mark(type);
            _highWater[mark.Key] = new ContentIdHighWater(
                Math.Max(held.ReservedThrough, mark.Value), Math.Max(held.IssuedThrough, mark.Value));
        }
    }

    /// <summary>
    /// The bundle's rules as the NEW line's, contiguous from 1 and introduced in version 1, which is what
    /// makes an import a republish rather than a restore. A lossless export is not a backup: the version
    /// LINE restarts, so a durable page stamped against the old line is newer than every rule there is.
    /// </summary>
    void Restamp(ContentBundle bundle)
    {
        for (int i = 0; i < bundle.Rules.Count; i++)
        {
            RemapRule rule = bundle.Rules[i];
            _rules.Add(new RemapRule(
                i + 1, 1, rule.Type, rule.Kind, rule.FromId, rule.ToId, rule.Payload.ToArray()));
        }
    }

    /// <summary>One <c>Add</c> per bundle row, in the bundle's own order, which is the allocation order.</summary>
    IReadOnlyList<ContentEdit> Edits(ContentBundle bundle)
    {
        var byKey = new Dictionary<(ushort Type, string Key), long>();
        foreach (KeyValuePair<long, FamilyRecord> held in _families)
        {
            byKey[(held.Value.Type.Value, held.Value.FamilyKey)] = held.Key;
        }

        var edits = new List<ContentEdit>(bundle.Rows.Count);
        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ContentBundleRow row = bundle.Rows[i];
            long? familyId = null;
            if (row.FamilyKey is string familyKey)
            {
                if (!byKey.TryGetValue((row.Type.Value, familyKey), out long held))
                {
                    throw new ContentAuthoringException(
                        FormattableString.Invariant(
                            $"Bundle row '{row.Key}' of content type {row.Type.Value} names family '{familyKey}', which the bundle's own family list does not carry."),
                        row.Type,
                        row.Id ?? 0,
                        ContentAuthoringException.UnknownFamilyReason);
                }

                familyId = held;
            }

            edits.Add(ContentEdit.Import(
                row.Type, row.Id ?? 0, row.Key, row.Fields, familyId, row.IsRetired));
        }

        return edits;
    }

    /// <summary>
    /// The empty state the import was required to start from. A refused import leaves a store a caller may
    /// import into again rather than one carrying half a bundle.
    /// </summary>
    void ResetToEmpty()
    {
        lock (_gate)
        {
            _rules.Clear();
            _published.Clear();
            _rows.Clear();
            _versions.Clear();
            _families.Clear();
            _highWater.Clear();
            _nextFamilyId = 1;
            _draft = null;
            _activeVersion = NoActiveVersion;
        }
    }

    static void Raise(Dictionary<ushort, int> highest, ushort typeId, int candidate)
        => highest[typeId] = highest.TryGetValue(typeId, out int held) ? Math.Max(held, candidate) : candidate;
}
