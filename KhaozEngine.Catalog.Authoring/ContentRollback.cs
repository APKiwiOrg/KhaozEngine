using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One row that BLOCKS a rollback: it was live at the target version and has been retired since, and the
/// rule that retired it is named beside it so an operator sees which publish did it.
/// </summary>
/// <param name="Type">The content type.</param>
/// <param name="DefinitionId">The retired definition id.</param>
/// <param name="Key">The row's key.</param>
/// <param name="RuleSequence">The retiring rule's sequence, or 0 when no rule names the row.</param>
/// <param name="IntroducedIn">The version the retiring rule was published in, or 0 when no rule names it.</param>
public sealed record ContentRollbackBlocker(
    ContentTypeId Type,
    int DefinitionId,
    ContentKey Key,
    int RuleSequence,
    int IntroducedIn);

/// <summary>
/// What a rollback WOULD do: the edits that restore the target version's field values, or the rows that
/// block it. It is a plan rather than a publish, because an operator reviews the diff and publishes it,
/// which is what makes a rollback reviewable rather than a second uncontrolled change.
/// </summary>
public sealed class ContentRollbackPlan
{
    internal ContentRollbackPlan(
        int targetVersion,
        int fromVersion,
        IReadOnlyList<ContentEdit> edits,
        IReadOnlyList<ContentRollbackBlocker> blockers,
        IReadOnlyList<ContentFinding> findings)
    {
        TargetVersion = targetVersion;
        FromVersion = fromVersion;
        Edits = edits;
        Blockers = blockers;
        Findings = findings;
    }

    /// <summary>The version whose field values would be restored.</summary>
    public int TargetVersion { get; }

    /// <summary>The version the rollback is measured from, which is the active one.</summary>
    public int FromVersion { get; }

    /// <summary>The edits that restore the target's values, in type then id order. Empty when blocked.</summary>
    public IReadOnlyList<ContentEdit> Edits { get; }

    /// <summary>Every row that blocks the rollback, empty on one that may proceed.</summary>
    public IReadOnlyList<ContentRollbackBlocker> Blockers { get; }

    /// <summary>One <c>KEC0039</c> finding per blocker, which is what the action returns to the console.</summary>
    public IReadOnlyList<ContentFinding> Findings { get; }

    /// <summary>True when a retire stands between the target version and now.</summary>
    public bool IsBlocked => Blockers.Count > 0;
}

/// <summary>
/// The rollback of spec 6.13, which is a PUBLISH and not a special case: it builds a draft of ordinary
/// updates restoring an earlier version's field values, and the operator reviews the diff and publishes it.
/// <para>
/// <b>The version number keeps climbing throughout.</b> A rollback is never a return to an old number, which
/// is the same property that lets a durable page's version stamp be an ordering comparison.
/// </para>
/// <para>
/// <b>A row introduced AFTER the target keeps its id and its values.</b> That is the difference between a
/// rollback and a restore, and it is why a rollback of a price pass does not delete the definitions a later
/// publish added.
/// </para>
/// <para>
/// <b>A row live at the target and RETIRED since is a flat refusal</b>, <c>KEC0039</c>. There is no
/// un-retire branch and there never was a reachable one: a retire appends exactly one kind 2 rule, so every
/// retired row is named by a rule and a branch conditioned on "no rule names that id" could not run. The way
/// out is <see cref="Remedy"/>, an ordinary add under a NEW key carrying the old values, plus a replacement
/// rule when existing references should move onto it.
/// </para>
/// </summary>
public static class ContentRollback
{
    /// <summary>The finding code a blocked rollback carries, which is its own code and never a second meaning for another.</summary>
    public const string BlockedCode = "KEC0039";

    /// <summary>The way out of a blocked rollback, which the refusal names so an operator is not left guessing.</summary>
    public const string Remedy =
        "mint a new definition under a new key carrying the old values, and add a ReplacedBy rule when existing references should move onto it";

    /// <summary>
    /// Works out what a rollback to <paramref name="targetVersion"/> would do.
    /// </summary>
    /// <param name="targetVersion">The version whose field values are to be restored.</param>
    /// <param name="target">The rows live AT the target version.</param>
    /// <param name="fromVersion">The version the rollback is measured from.</param>
    /// <param name="current">The rows live at <paramref name="fromVersion"/>.</param>
    /// <param name="rules">The full ordered rule list, which names the retire that blocks a row.</param>
    /// <param name="registry">The registry the rows' types are declared in, which supplies the field names.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ContentRollbackPlan Prepare(
        int targetVersion,
        IReadOnlyList<ContentRowRevision> target,
        int fromVersion,
        IReadOnlyList<ContentRowRevision> current,
        IReadOnlyList<RemapRule> rules,
        ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(registry);

        Dictionary<(ushort Type, int Id), ContentRow> live = Index(current);
        var ordered = new List<ContentRow>(target.Count);
        for (int i = 0; i < target.Count; i++)
        {
            ordered.Add(target[i].Row);
        }

        ordered.Sort(static (left, right) => left.Type.Value == right.Type.Value
            ? left.Id.CompareTo(right.Id)
            : left.Type.Value.CompareTo(right.Type.Value));

        var edits = new List<ContentEdit>();
        var blockers = new List<ContentRollbackBlocker>();
        var findings = new List<ContentFinding>();

        for (int i = 0; i < ordered.Count; i++)
        {
            ContentRow was = ordered[i];
            if (!live.TryGetValue((was.Type.Value, was.Id), out ContentRow? now))
            {
                // A definition is never deleted, so a row live at the target is live now. A store that
                // answers otherwise has lost a row, which is not something a rollback can repair.
                continue;
            }

            if (now.IsRetired && !was.IsRetired)
            {
                RemapRule? retiring = Retiring(rules, was, targetVersion);
                blockers.Add(new ContentRollbackBlocker(
                    was.Type,
                    was.Id,
                    was.Key,
                    retiring?.Sequence ?? 0,
                    retiring?.IntroducedIn ?? 0));
                findings.Add(new ContentFinding(
                    was.Type,
                    was.Id,
                    BlockedCode,
                    Message(was, targetVersion, retiring)));
                continue;
            }

            IReadOnlyList<ContentFieldEdit> restore = Restore(registry, was, now);
            if (restore.Count > 0)
            {
                edits.Add(ContentEdit.Update(was.Type, was.Id, was.Key, restore));
            }
        }

        return new ContentRollbackPlan(
            targetVersion,
            fromVersion,
            blockers.Count > 0 ? [] : edits,
            blockers,
            findings);
    }

    /// <summary>
    /// The refusal a blocked plan throws, carrying every finding, so the API boundary renders a 409 listing
    /// every blocking row and rule with the way out named.
    /// </summary>
    /// <param name="plan">A plan whose blockers are not empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="plan"/> is not blocked, so there is nothing to refuse.</exception>
    public static ContentAuthoringException Refusal(ContentRollbackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsBlocked)
        {
            throw new ArgumentException("A plan with no blockers is not a refusal.", nameof(plan));
        }

        ContentRollbackBlocker first = plan.Blockers[0];
        return new ContentAuthoringException(
            FormattableString.Invariant(
                $"A rollback to version {plan.TargetVersion} would restore {plan.Blockers.Count} row(s) that have been retired since, starting with row {first.DefinitionId} ('{first.Key}') of content type {first.Type.Value}. A retire is irreversible for pages already migrated past it, so the way out is to {Remedy}."),
            first.Type,
            first.DefinitionId,
            ContentAuthoringException.RetireIrreversibleReason,
            plan.Findings);
    }

    /// <summary>
    /// The fields whose values differ, carrying the TARGET's value. A field that was absent at the target and
    /// carries a value now is restored to absent, because a rollback restores the row as it stood rather than
    /// as much of it as an edit finds convenient.
    /// </summary>
    static IReadOnlyList<ContentFieldEdit> Restore(
        ContentTypeRegistry registry,
        ContentRow was,
        ContentRow now)
    {
        if (!registry.TryGet(was.Type, out ContentTypeRegistration? registration))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The rollback target carries a row of content type {was.Type.Value}, which this registry does not declare."),
                was.Type,
                was.Id,
                ContentAuthoringException.UnknownTypeReason);
        }

        IReadOnlyList<ContentFieldEntry> schema = registration.Schema.Fields;
        var restore = new List<ContentFieldEdit>();
        for (int i = 0; i < schema.Count && i < was.Fields.Count; i++)
        {
            if (schema[i].IsDerivedMarker)
            {
                // A derived key is a function of the row it sits on, so it has no value to restore.
                continue;
            }

            if (i >= now.Fields.Count || !was.Fields[i].Equals(now.Fields[i]))
            {
                restore.Add(new ContentFieldEdit(schema[i].Name, was.Fields[i]));
            }
        }

        return restore;
    }

    /// <summary>
    /// The kind 2 rule that retired a row after the target version. Every retire appends exactly one, so this
    /// finds one in the ordinary case, and it answers null rather than throwing on a store whose rule list
    /// somehow does not name it, because the refusal is about the ROW and the rule is the detail.
    /// </summary>
    static RemapRule? Retiring(IReadOnlyList<RemapRule> rules, ContentRow row, int targetVersion)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            RemapRule rule = rules[i];
            if (rule.Kind == RemapRuleKind.Retired
                && rule.Type == row.Type
                && rule.FromId == row.Id
                && rule.IntroducedIn > targetVersion)
            {
                return rule;
            }
        }

        return null;
    }

    static string Message(ContentRow row, int targetVersion, RemapRule? retiring)
        => retiring is null
            ? FormattableString.Invariant(
                $"Row {row.Id} ('{row.Key}') was live at version {targetVersion} and is retired now, so a rollback would restore a retired definition. A retire is irreversible: {Remedy}.")
            : FormattableString.Invariant(
                $"Row {row.Id} ('{row.Key}') was live at version {targetVersion} and rule {retiring.Sequence}, introduced in version {retiring.IntroducedIn}, retired it. A retire is irreversible: {Remedy}.");

    static Dictionary<(ushort Type, int Id), ContentRow> Index(IReadOnlyList<ContentRowRevision> revisions)
    {
        var index = new Dictionary<(ushort, int), ContentRow>(revisions.Count);
        for (int i = 0; i < revisions.Count; i++)
        {
            ContentRow row = revisions[i].Row;
            index[(row.Type.Value, row.Id)] = row;
        }

        return index;
    }
}
