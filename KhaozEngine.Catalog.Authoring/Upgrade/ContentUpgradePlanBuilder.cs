using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>One committed identity: the content type and the key, which is how a planner names a row.</summary>
/// <param name="Type">The content type.</param>
/// <param name="Key">The row's key, ordinal and immutable once published.</param>
public readonly record struct ContentUpgradeIdentity(ContentTypeId Type, ContentKey Key);

/// <summary>
/// Builds a <see cref="ContentUpgradePlan"/> out of a baseline catalog and a COMMITTED TARGET BUNDLE, running
/// the checks of <see cref="ContentUpgradeChecks"/> on the way. The types and the keys are parameters, so the
/// engine carries no game noun and no game content.
/// <para>
/// <b>A partial state is REFUSED rather than completed.</b> Every staged operation resolves to satisfied or
/// pending, and a mixture of the two means an earlier run, an operator, or an older explicit command did some
/// of this upgrade and not the rest. Completing the remainder would guess which rows an operator owns, and
/// that guess is unrecoverable once it publishes.
/// </para>
/// <para>
/// <b>A value an operator has tuned is neither.</b> <see cref="PatchField"/> acts only on a field still
/// holding the old shipped default, and a field holding anything else is left alone and counts toward
/// neither side of the partial check, because operator tuning is not evidence that the upgrade ran.
/// </para>
/// <para>
/// <b>A LIST is the one place that rule inverts.</b> <see cref="AppendTag"/> detects by ELEMENT rather than
/// by the whole value, because a tag list an operator has extended equals no default a build ever shipped
/// and a patch would skip that row forever. It appends at the end, keeps every element already there in the
/// order it was written, and counts satisfied against pending like every other verb.
/// </para>
/// <para>
/// <b>ONE row is ONE edit, whatever a planner stages.</b> A draft holds one pending intent per row
/// (<see cref="ContentChangeSet"/>), so two patches of one row are merged into a single
/// <see cref="ContentEditOperation.Update"/> carrying both fields, in the order they were staged. Any
/// staging a change set could not hold verbatim is thrown rather than emitted: the same field twice, a
/// patch and a retire of one row, and one row staged twice under either verb. Emitting it would drop a
/// change silently at the store and leave a draft no later run could ever prove is its own.
/// </para>
/// </summary>
public sealed class ContentUpgradePlanBuilder
{
    readonly ContentUpgradeContext _context;
    readonly ContentBundle _target;
    readonly List<ContentBundleRow> _additions = [];
    readonly List<StagedRow> _staged = [];
    readonly Dictionary<ContentEditTarget, int> _byTarget = [];
    readonly HashSet<ContentEditTarget> _addedTargets = [];
    readonly List<string> _lines = [];
    string? _refusal;
    int _satisfied;
    int _pending;

    /// <summary>
    /// Opens a builder and runs the two whole-bundle checks straight away: the target agrees with this
    /// build's registry, and every baseline type is schema compatible with the target. A failure of either
    /// makes every later call a no-op and <see cref="Build"/> a refusal.
    /// </summary>
    /// <param name="context">The context the runner handed the planner.</param>
    /// <param name="target">The committed target bundle the build ships.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ContentUpgradePlanBuilder(ContentUpgradeContext context, ContentBundle target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);

        _context = context;
        _target = target;
        _refusal = ContentUpgradeChecks.TargetMatchesRegistry(target, context.Registry)
            ?? ContentUpgradeChecks.BaselineIsSchemaCompatible(context.Baseline, target);
    }

    /// <summary>
    /// Stages one committed row to be added under the id and key the target bundle names it by. Absent from
    /// the baseline it is pending, present under BOTH the same id and the same key it is satisfied, and
    /// anything else is a conflict the plan refuses.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The committed row's key.</param>
    /// <exception cref="ArgumentException">The same row is already staged.</exception>
    public ContentUpgradePlanBuilder AddRow(ContentTypeId type, ContentKey key)
    {
        if (_refusal is not null)
        {
            return this;
        }

        ContentBundleRow? targetRow = ContentUpgradeChecks.FindByKey(_target, type, key);
        if (targetRow is null)
        {
            _refusal = FormattableString.Invariant(
                $"The committed bundle carries no {TypeName(type)} row '{key}', so this build cannot say what to add.");
            return this;
        }

        ContentUpgradeIdentityState state = ContentUpgradeChecks.Identity(
            _context.Baseline, targetRow, out string? conflict);
        switch (state)
        {
            case ContentUpgradeIdentityState.Present:
                _satisfied++;
                break;
            case ContentUpgradeIdentityState.Absent:
                RequireUnstaged(new ContentEditTarget(type, targetRow.Id ?? 0, key), type, key, "added");
                _additions.Add(targetRow);
                _addedTargets.Add(new ContentEditTarget(type, targetRow.Id ?? 0, key));
                _pending++;
                break;
            default:
                _refusal = conflict;
                break;
        }

        return this;
    }

    /// <summary>Stages every committed identity in order, which is the ordinary shape of an additive upgrade.</summary>
    /// <param name="identities">The committed identities.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identities"/> is null.</exception>
    public ContentUpgradePlanBuilder AddRows(IReadOnlyList<ContentUpgradeIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);

        for (int i = 0; i < identities.Count; i++)
        {
            AddRow(identities[i].Type, identities[i].Key);
        }

        return this;
    }

    /// <summary>
    /// Replaces one field's OLD SHIPPED DEFAULT with a new one on a row the catalog already holds. The old
    /// default is named rather than inferred, which is the whole of the value-patch rule: a field already
    /// holding <paramref name="replacement"/> is satisfied, a field holding
    /// <paramref name="oldDefault"/> is patched, and a field holding anything else belongs to an operator and
    /// is left exactly as it is.
    /// <para>
    /// Every patch of ONE row lands in one <see cref="ContentEditOperation.Update"/> carrying all of them,
    /// because that is what the draft will hold. Naming one FIELD twice, or patching a row this plan also
    /// retires, is thrown rather than merged: neither has a single answer the store could keep.
    /// </para>
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="fieldName">The schema field, compared ordinally.</param>
    /// <param name="oldDefault">The value this build shipped before, which is the only value that is patched.</param>
    /// <param name="replacement">The value this build ships now.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is null.</exception>
    /// <exception cref="ArgumentException">The field is already patched, or the row is staged otherwise.</exception>
    public ContentUpgradePlanBuilder PatchField(
        ContentTypeId type,
        ContentKey key,
        string fieldName,
        ContentFieldValue oldDefault,
        ContentFieldValue replacement)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        if (_refusal is not null)
        {
            return this;
        }

        if (!TryReadBaseline(type, key, out ContentBundleRow? row, out int id))
        {
            _refusal = FormattableString.Invariant(
                $"The catalog carries no {TypeName(type)} row '{key}' to patch '{fieldName}' on. Nothing was changed.");
            return this;
        }

        if (!TryRead(type, row, fieldName, out ContentFieldValue current))
        {
            _refusal = FormattableString.Invariant(
                $"Type {TypeName(type)} declares no field '{fieldName}', so this upgrade's patch of '{key}' cannot be applied.");
            return this;
        }

        // The field is claimed BEFORE the catalog is consulted, because naming one field twice is the
        // planner contradicting itself whatever the catalog happens to hold right now.
        var target = new ContentEditTarget(type, id, key);
        StagedRow patch = Stage(target, ContentEditOperation.Update, type, key, "patched");
        if (!patch.Claim(fieldName))
        {
            throw FieldWrittenTwice(type, id, key, fieldName);
        }

        if (current == replacement)
        {
            _satisfied++;
            return this;
        }

        if (current != oldDefault)
        {
            // Operator tuning. It is neither satisfied nor pending, because a value someone chose is not
            // evidence that this upgrade ran and is not this upgrade's to replace.
            return this;
        }

        patch.Fields.Add(new ContentFieldEdit(fieldName, replacement));
        _lines.Add(FormattableString.Invariant(
            $"patch {TypeName(type)} {id} '{key}' field '{fieldName}' from the old shipped default"));
        _pending++;
        return this;
    }

    /// <summary>
    /// Appends ONE element to a TAG LIST field of a row the catalog already holds, keeping every element
    /// already in the list and the order it was written in.
    /// <para>
    /// The list rule is not the value-patch rule. <see cref="PatchField"/> acts only while the field still
    /// equals a named old default, and a list an operator has extended equals no default this build ever
    /// shipped, so a patch would leave that row unappended forever. Identity here is the ELEMENT: a list
    /// already carrying <paramref name="tagId"/> is SATISFIED and emits nothing, and a list without it is
    /// written back with it at the END. Nothing is sorted and nothing is deduplicated.
    /// </para>
    /// <para>
    /// It composes like a patch: the append lands in the ONE <see cref="ContentEditOperation.Update"/> the
    /// row's other changes land in, and it counts toward the same satisfied and pending tallies, which is
    /// what makes several appends across several rows all-or-none under the partial-state rule. Naming one
    /// field twice under either verb is thrown rather than merged, because a row carries one value per
    /// field, and so is appending to a row this plan also adds or retires.
    /// </para>
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="fieldName">The schema field, which has to be a tag list, compared ordinally.</param>
    /// <param name="tagId">The tag id to append at the end of the list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tagId"/> is below 1.</exception>
    /// <exception cref="ArgumentException">The field is already written, or the row is staged otherwise.</exception>
    public ContentUpgradePlanBuilder AppendTag(
        ContentTypeId type,
        ContentKey key,
        string fieldName,
        int tagId)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        // A tag id below 1 is not content. The codec drops it on the way back out, so appending one would
        // stage an edit that read back as the list the row already had and never settle.
        ArgumentOutOfRangeException.ThrowIfLessThan(tagId, 1);

        if (_refusal is not null)
        {
            return this;
        }

        if (!TryReadBaseline(type, key, out ContentBundleRow? row, out int id))
        {
            _refusal = FormattableString.Invariant(
                $"The catalog carries no {TypeName(type)} row '{key}' to append to '{fieldName}' on. Nothing was changed.");
            return this;
        }

        if (!TryRead(type, row, fieldName, out ContentFieldValue current))
        {
            _refusal = FormattableString.Invariant(
                $"Type {TypeName(type)} declares no field '{fieldName}', so this upgrade's append on '{key}' cannot be applied.");
            return this;
        }

        if (current.Kind != ContentFieldKind.TagList)
        {
            _refusal = FormattableString.Invariant(
                $"Type {TypeName(type)} declares field '{fieldName}' as {current.Kind} rather than a tag list, and only a list is appended to. Patch a value instead.");
            return this;
        }

        // The field is claimed BEFORE the catalog is consulted, for the same reason a patch claims it: one
        // field written twice by one plan is the planner contradicting itself whatever the row holds.
        var target = new ContentEditTarget(type, id, key);
        StagedRow append = Stage(target, ContentEditOperation.Update, type, key, "appended to");
        if (!append.Claim(fieldName))
        {
            throw FieldWrittenTwice(type, id, key, fieldName);
        }

        if (!ContentUpgradeTagList.TryAppend(in current, tagId, out ContentFieldValue appended))
        {
            _satisfied++;
            return this;
        }

        append.Fields.Add(new ContentFieldEdit(fieldName, appended));
        _lines.Add(FormattableString.Invariant(
            $"append tag {tagId} to {TypeName(type)} {id} '{key}' field '{fieldName}'"));
        _pending++;
        return this;
    }

    /// <summary>
    /// Takes one row the catalog holds out of play. A row already retired is satisfied, which is what makes
    /// a re-run a no-op.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="policy">Placeholder or replacement, contracts 8.2.</param>
    /// <param name="replacementId">The destination id under the replacement policy, and 0 otherwise.</param>
    /// <exception cref="ArgumentException">The row is already staged by this plan.</exception>
    public ContentUpgradePlanBuilder RetireRow(
        ContentTypeId type,
        ContentKey key,
        ContentRetirePolicy policy,
        int replacementId = 0)
    {
        if (_refusal is not null)
        {
            return this;
        }

        if (!TryReadBaseline(type, key, out ContentBundleRow? row, out int id))
        {
            _refusal = FormattableString.Invariant(
                $"The catalog carries no {TypeName(type)} row '{key}' to retire. Nothing was changed.");
            return this;
        }

        if (row.IsRetired)
        {
            _satisfied++;
            return this;
        }

        StagedRow retire = Stage(
            new ContentEditTarget(type, id, key), ContentEditOperation.Retire, type, key, "retired");
        retire.Policy = policy;
        retire.ReplacementId = replacementId;
        _lines.Add(FormattableString.Invariant($"retire {TypeName(type)} {id} '{key}'"));
        _pending++;
        return this;
    }

    /// <summary>
    /// Resolves the staged work into one plan: the adds first, then the patches and retires in the order they
    /// were staged, ONE edit per row. The adds go first because they read as the additive half of the
    /// upgrade, and the ids they allocate are unaffected either way.
    /// <para>
    /// A row whose every patch turned out satisfied or operator tuned contributes NO edit, because an update
    /// carrying no field is a write that says nothing.
    /// </para>
    /// </summary>
    public ContentUpgradePlan Build()
    {
        if (_refusal is not null)
        {
            return ContentUpgradePlan.Refused(_refusal);
        }

        var edits = new List<ContentEdit>(_additions.Count + _staged.Count);
        var lines = new List<string>(_additions.Count + _lines.Count);
        if (_additions.Count > 0)
        {
            string? occupied = ContentUpgradeChecks.IdentityIsFree(
                _context.Baseline, _additions, _context.Registry);
            if (occupied is not null)
            {
                return ContentUpgradePlan.Refused(occupied);
            }

            _additions.Sort(static (left, right) => left.Type.Value != right.Type.Value
                ? left.Type.Value.CompareTo(right.Type.Value)
                : Comparer<int?>.Default.Compare(left.Id, right.Id));
            for (int i = 0; i < _additions.Count; i++)
            {
                ContentBundleRow row = _additions[i];

                // The add CARRIES the committed id rather than leaving it to the allocator. Nothing else
                // makes the published id the one the build names the row by: the allocator issues from a
                // durable mark that sits above the highest row id whenever a publish was refused after step
                // 3, and a row filed under a number a code constant does not name is a stable-id violation
                // nothing detects afterwards.
                //
                // The id is never null here: IdentityIsFree above refuses a committed row that does not carry
                // its stable id, so both the edit and the line below name a real number.
                edits.Add(ContentEdit.Import(row.Type, row.Id ?? 0, row.Key, row.Fields));
                lines.Add(FormattableString.Invariant(
                    $"add {TypeName(row.Type)} {row.Id} '{row.Key}'"));
            }
        }

        for (int i = 0; i < _staged.Count; i++)
        {
            if (_staged[i].TryBuild(out ContentEdit? edit))
            {
                edits.Add(edit);
            }
        }

        lines.AddRange(_lines);

        if (_pending == 0)
        {
            return ContentUpgradePlan.AlreadySatisfied(FormattableString.Invariant(
                $"The catalog at version {_context.BaselineVersion} already carries every identity and value this upgrade ships."));
        }

        if (_satisfied > 0)
        {
            return ContentUpgradePlan.Refused(FormattableString.Invariant(
                $"The catalog at version {_context.BaselineVersion} carries {_satisfied} of this upgrade's changes and is missing {_pending}. Refusing a partial upgrade rather than guessing which rows an operator owns."));
        }

        return ContentUpgradePlan.Changes(edits, lines);
    }

    /// <summary>
    /// The staged edit for one row under one operation, CREATING it on the first call and answering the
    /// standing one afterwards, so every patch of a row lands in the same update. It throws when the row is
    /// already staged in a way a change set could not hold beside this one.
    /// </summary>
    /// <param name="target">The row the edit acts on, which is what a draft deduplicates by.</param>
    /// <param name="operation">The operation being staged.</param>
    /// <param name="type">The content type, for the message.</param>
    /// <param name="key">The row's key, for the message.</param>
    /// <param name="verb">What this call is doing to the row, for the message.</param>
    StagedRow Stage(
        ContentEditTarget target,
        ContentEditOperation operation,
        ContentTypeId type,
        ContentKey key,
        string verb)
    {
        RequireUnadded(target, type, key, verb);
        if (!_byTarget.TryGetValue(target, out int index))
        {
            _byTarget.Add(target, _staged.Count);
            var staged = new StagedRow(target, operation);
            _staged.Add(staged);
            return staged;
        }

        StagedRow held = _staged[index];
        if (held.Operation != operation || operation == ContentEditOperation.Retire)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"This upgrade already {Describe(held.Operation)} {TypeName(type)} {target.DefinitionId} '{key}', so it cannot also be {verb} by the same plan. A draft holds one pending intent per row. Split the two into two upgrades."),
                nameof(key));
        }

        return held;
    }

    /// <summary>A row this plan ADDS is not a row it can also patch or retire in the same change set.</summary>
    /// <param name="target">The row the edit acts on.</param>
    /// <param name="type">The content type, for the message.</param>
    /// <param name="key">The row's key, for the message.</param>
    /// <param name="verb">What this call is doing to the row, for the message.</param>
    void RequireUnadded(ContentEditTarget target, ContentTypeId type, ContentKey key, string verb)
    {
        if (_addedTargets.Contains(target))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"This upgrade already adds {TypeName(type)} {target.DefinitionId} '{key}', so it cannot also have that row {verb} by the same plan. A draft holds one pending intent per row."),
                nameof(key));
        }
    }

    /// <summary>The same check for the additive half, which stages no <see cref="StagedRow"/> of its own.</summary>
    /// <param name="target">The row the add names.</param>
    /// <param name="type">The content type, for the message.</param>
    /// <param name="key">The row's key, for the message.</param>
    /// <param name="verb">What this call is doing to the row, for the message.</param>
    void RequireUnstaged(ContentEditTarget target, ContentTypeId type, ContentKey key, string verb)
    {
        RequireUnadded(target, type, key, verb);
        if (_byTarget.TryGetValue(target, out int index))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"This upgrade already {Describe(_staged[index].Operation)} {TypeName(type)} {target.DefinitionId} '{key}', so it cannot also have that row {verb} by the same plan. A draft holds one pending intent per row."),
                nameof(key));
        }
    }

    /// <summary>What a standing edit did to its row, as the present tense a refusal reads with.</summary>
    /// <param name="operation">The standing edit's operation.</param>
    static string Describe(ContentEditOperation operation)
        => operation == ContentEditOperation.Retire ? "retires" : "writes";

    /// <summary>
    /// The refusal for ONE field written twice on one row, whichever verbs named it. A row carries one value
    /// per field, so two writes of one field have no single answer the draft could keep.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="id">The row's stable id.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="fieldName">The schema field named twice.</param>
    ArgumentException FieldWrittenTwice(ContentTypeId type, int id, ContentKey key, string fieldName)
        => new(
            FormattableString.Invariant(
                $"This upgrade already writes {TypeName(type)} {id} '{key}' field '{fieldName}', and a row carries one value per field. Name each field once."),
            nameof(fieldName));

    /// <summary>
    /// One baseline row by key, with its id. A row an export produced always carries its id, so the id half
    /// failing is the same "the catalog does not hold this row" answer as the row half failing.
    /// </summary>
    bool TryReadBaseline(
        ContentTypeId type,
        ContentKey key,
        [NotNullWhen(true)] out ContentBundleRow? row,
        out int id)
    {
        row = ContentUpgradeChecks.FindByKey(_context.Baseline, type, key);
        id = row?.Id ?? 0;
        return row is not null && id > 0;
    }

    /// <summary>
    /// One field's value on a baseline row, as the SCHEMA declares the field. A row that carries no value for
    /// a declared field reads as <see cref="ContentFieldValue.Absent"/> of the declared kind rather than as a
    /// default-constructed value, so a patch may name absence as the old shipped default. A field the type
    /// does not declare at all is false, which is the caller's own typo.
    /// </summary>
    bool TryRead(ContentTypeId type, ContentBundleRow row, string fieldName, out ContentFieldValue value)
    {
        value = default;
        if (!_context.Registry.TryGet(type, out ContentTypeRegistration? registration)
            || !registration.Schema.TryGet(fieldName, out ContentFieldEntry? entry))
        {
            return false;
        }

        value = ContentFieldValue.Absent(entry.Kind);
        for (int i = 0; i < row.Fields.Count; i++)
        {
            if (string.Equals(row.Fields[i].Name, fieldName, StringComparison.Ordinal))
            {
                value = row.Fields[i].Value;
                break;
            }
        }

        return true;
    }

    string TypeName(ContentTypeId type) => ContentUpgradeChecks.TypeName(_context.Registry, type);

    /// <summary>
    /// One ROW's pending edit while the plan is still being staged: the target, the operation, and the
    /// fields the patches have named so far. It exists because the unit a draft stores is the row and the
    /// unit a planner writes is the field, and something has to hold the many while they become the one.
    /// </summary>
    /// <param name="target">The row the edit acts on.</param>
    /// <param name="operation">The operation, which is fixed once the row is staged.</param>
    sealed class StagedRow(ContentEditTarget target, ContentEditOperation operation)
    {
        readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

        /// <summary>The operation, which a second staging of the same row has to agree with.</summary>
        internal ContentEditOperation Operation { get; } = operation;

        /// <summary>The changed fields, in the order the patches were staged.</summary>
        internal List<ContentFieldEdit> Fields { get; } = [];

        /// <summary>The retire policy, on a retire only.</summary>
        internal ContentRetirePolicy Policy { get; set; }

        /// <summary>The replacement id, on a replacement-policy retire only.</summary>
        internal int ReplacementId { get; set; }

        /// <summary>
        /// Claims one field name for this row, answering false when it is already claimed. A field is
        /// claimed whether or not it ended up producing an edit, because naming it twice is the planner
        /// contradicting itself whatever the catalog holds.
        /// </summary>
        /// <param name="fieldName">The schema field.</param>
        internal bool Claim(string fieldName) => _claimed.Add(fieldName);

        /// <summary>
        /// The one edit this row contributes, or false when it contributes none: a row every patch of which
        /// was satisfied or operator tuned carries no changed field, and an update of nothing is a write
        /// that says nothing.
        /// </summary>
        /// <param name="edit">The edit, populated only when this returns true.</param>
        internal bool TryBuild([NotNullWhen(true)] out ContentEdit? edit)
        {
            if (Operation == ContentEditOperation.Retire)
            {
                edit = ContentEdit.Retire(target.Type, target.DefinitionId, target.Key, Policy, ReplacementId);
                return true;
            }

            edit = Fields.Count == 0
                ? null
                : ContentEdit.Update(target.Type, target.DefinitionId, target.Key, Fields);
            return edit is not null;
        }
    }
}
