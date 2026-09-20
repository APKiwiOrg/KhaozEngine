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
/// </summary>
public sealed class ContentUpgradePlanBuilder
{
    readonly ContentUpgradeContext _context;
    readonly ContentBundle _target;
    readonly List<ContentBundleRow> _additions = [];
    readonly List<ContentEdit> _edits = [];
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
                _additions.Add(targetRow);
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
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="fieldName">The schema field, compared ordinally.</param>
    /// <param name="oldDefault">The value this build shipped before, which is the only value that is patched.</param>
    /// <param name="replacement">The value this build ships now.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is null.</exception>
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

        _edits.Add(ContentEdit.Update(type, id, key, [new ContentFieldEdit(fieldName, replacement)]));
        _lines.Add(FormattableString.Invariant(
            $"patch {TypeName(type)} {id} '{key}' field '{fieldName}' from the old shipped default"));
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

        _edits.Add(ContentEdit.Retire(type, id, key, policy, replacementId));
        _lines.Add(FormattableString.Invariant($"retire {TypeName(type)} {id} '{key}'"));
        _pending++;
        return this;
    }

    /// <summary>
    /// Resolves the staged work into one plan: the adds first, then the patches and retires in the order they
    /// were staged. The adds go first because they read as the additive half of the upgrade, and the ids they
    /// allocate are unaffected either way.
    /// </summary>
    public ContentUpgradePlan Build()
    {
        if (_refusal is not null)
        {
            return ContentUpgradePlan.Refused(_refusal);
        }

        var edits = new List<ContentEdit>(_additions.Count + _edits.Count);
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

        edits.AddRange(_edits);
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
}
