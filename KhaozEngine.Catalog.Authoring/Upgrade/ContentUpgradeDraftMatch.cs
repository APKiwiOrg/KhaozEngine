using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The two proofs an upgrade run has about an open draft: <see cref="IsPlan"/>, which is EXACTLY one
/// definition's change set and is what a run needs before it PUBLISHES a draft it did not write in this
/// attempt, and <see cref="IsKnownWork"/>, which is every held edit belonging to some plan the run computed
/// and is what it needs before it DISCARDS one. They are public because they are the comparisons every
/// data-loss path turns on.
/// <para>
/// <b>An actor and a note are not proof.</b> Both stores KEEP the standing note when a writer passes an
/// empty one, and no store rewrites the identity that opened a draft, so an operator who adds edits to an
/// interrupted run's draft without a note leaves it carrying the runner's actor and the runner's note. A
/// run that trusted those two would discard work nobody published.
/// </para>
/// <para>
/// <b>The comparison is order independent.</b> A draft is rebuilt from a stored edit table, and while both
/// providers do hand the rows back in the order they were written, the proof does not depend on that: two
/// edits of different targets in either order are the same change set.
/// </para>
/// </summary>
public static class ContentUpgradeDraftMatch
{
    /// <summary>
    /// Whether the draft's expanded edits ARE the planned edits: the same count, one edit per planned target
    /// under the same type, operation, definition id and key, and the same payload on each.
    /// </summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    /// <param name="planned">The edits a fresh plan of the same definition produced against the current baseline.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool IsPlan(ContentDraft draft, IReadOnlyList<ContentEdit> planned)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(planned);

        IReadOnlyList<ContentEdit> held = draft.Changes.Edits;
        if (held.Count != planned.Count || planned.Count == 0)
        {
            return false;
        }

        var byTarget = new Dictionary<ContentUpgradeEditIdentity, ContentEdit>(held.Count);
        for (int i = 0; i < held.Count; i++)
        {
            if (!byTarget.TryAdd(IdentityOf(held[i]), held[i]))
            {
                // One target twice is a change set no draft can hold, so a store that produced one is not
                // reproducing a plan whatever the payloads say.
                return false;
            }
        }

        for (int i = 0; i < planned.Count; i++)
        {
            ContentEdit wanted = planned[i];
            if (!byTarget.TryGetValue(IdentityOf(wanted), out ContentEdit? standing)
                || !SamePayload(standing, wanted))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether EVERY edit the draft holds is one of the supplied known edits: the same target under the same
    /// type, operation, definition id and key, and the same payload. It is a SUBSET question rather than an
    /// equality one, which is what tells a draft two runs' change sets merged into apart from a draft an
    /// operator added to.
    /// <para>
    /// <b>This is a discard proof and never a publish one.</b> A draft that is a subset of known work holds
    /// nothing nobody planned, so losing it costs a replan and no content. It says nothing about the draft
    /// being a complete change set, so nothing may be published out of it: that still takes
    /// <see cref="IsPlan"/>.
    /// </para>
    /// <para>
    /// <b>An EMPTY draft passes.</b> Nothing held is nothing to lose, and a run that refused to clear one
    /// would stand off against a draft that cannot move until an operator resolves it by hand.
    /// </para>
    /// <para>
    /// <b>Duplicates are handled from both sides.</b> One target twice in the DRAFT fails, because no plan
    /// produces a change set like that and a store that returned one is not reproducing planned work. The
    /// same target several times in the KNOWN set is ordinary, because two definitions may touch one row
    /// differently, and a held edit matching any one of them is planned work.
    /// </para>
    /// </summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    /// <param name="known">Every edit of every plan the run computed, in any order and with repeats allowed.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool IsKnownWork(ContentDraft draft, IReadOnlyList<ContentEdit> known)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(known);

        IReadOnlyList<ContentEdit> held = draft.Changes.Edits;
        if (held.Count == 0)
        {
            return true;
        }

        Dictionary<ContentUpgradeEditIdentity, List<ContentEdit>> byTarget = Index(known);
        var seen = new HashSet<ContentUpgradeEditIdentity>(held.Count);
        for (int i = 0; i < held.Count; i++)
        {
            if (!seen.Add(IdentityOf(held[i])) || !IsKnown(byTarget, held[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether ANY edit the draft holds came from the known set. It is the other side of
    /// <see cref="IsKnownWork"/> and it proves nothing on its own: it tells a draft a run's own write merged
    /// into apart from one that is another writer's whole, which decides who is asked to resolve it, and it
    /// never decides whether anything may be destroyed.
    /// </summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    /// <param name="known">Every edit of every plan the run computed, in any order and with repeats allowed.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool HoldsKnownWork(ContentDraft draft, IReadOnlyList<ContentEdit> known)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(known);

        IReadOnlyList<ContentEdit> held = draft.Changes.Edits;
        Dictionary<ContentUpgradeEditIdentity, List<ContentEdit>> byTarget = Index(known);
        for (int i = 0; i < held.Count; i++)
        {
            if (IsKnown(byTarget, held[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The known edits by the row they act on, with several per row allowed.</summary>
    static Dictionary<ContentUpgradeEditIdentity, List<ContentEdit>> Index(IReadOnlyList<ContentEdit> known)
    {
        var byTarget = new Dictionary<ContentUpgradeEditIdentity, List<ContentEdit>>(known.Count);
        for (int i = 0; i < known.Count; i++)
        {
            ContentUpgradeEditIdentity identity = IdentityOf(known[i]);
            if (!byTarget.TryGetValue(identity, out List<ContentEdit>? candidates))
            {
                candidates = [];
                byTarget[identity] = candidates;
            }

            candidates.Add(known[i]);
        }

        return byTarget;
    }

    /// <summary>Whether any known edit of the held edit's row carries the held edit's payload too.</summary>
    static bool IsKnown(
        Dictionary<ContentUpgradeEditIdentity, List<ContentEdit>> byTarget,
        ContentEdit held)
    {
        if (!byTarget.TryGetValue(IdentityOf(held), out List<ContentEdit>? planned))
        {
            return false;
        }

        for (int i = 0; i < planned.Count; i++)
        {
            if (SamePayload(planned[i], held))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The four halves that name the ROW an edit acts on, which is what the two lists are matched up by.
    /// The operation is part of the identity rather than of the payload, because an update and a retire of
    /// one row are two different edits rather than one changed edit.
    /// </summary>
    readonly record struct ContentUpgradeEditIdentity(
        ushort Type,
        ContentEditOperation Operation,
        int DefinitionId,
        ContentKey Key);

    static ContentUpgradeEditIdentity IdentityOf(ContentEdit edit)
        => new(edit.Type.Value, edit.Operation, edit.DefinitionId, edit.Key);

    /// <summary>
    /// Everything an edit carries beyond its target: the changed fields, the retire policy and its
    /// replacement, the fork's key and flag field, the family, and whether an imported row is already
    /// retired. All of it, because an edit that differs in any of them publishes different content.
    /// </summary>
    static bool SamePayload(ContentEdit left, ContentEdit right)
        => left.RetirePolicy == right.RetirePolicy
            && left.ReplacementId == right.ReplacementId
            && left.ForkKey.Equals(right.ForkKey)
            && string.Equals(left.ForkFlagField, right.ForkFlagField, StringComparison.Ordinal)
            && left.FamilyId == right.FamilyId
            && left.ImportedAsRetired == right.ImportedAsRetired
            && SameFields(left.Fields, right.Fields);

    /// <summary>
    /// The changed fields, matched BY NAME rather than by position. A field set is a map in every sense that
    /// matters here: two edits that set the same fields to the same values are the same edit whichever order
    /// a provider stored them in.
    /// <para>
    /// One name TWICE is a set no row can hold, on either side. The check is symmetric because the counts
    /// alone would otherwise let two distinct fields match one field named twice, which is two edits away
    /// from the same row.
    /// </para>
    /// </summary>
    static bool SameFields(IReadOnlyList<ContentFieldEdit> left, IReadOnlyList<ContentFieldEdit> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var byName = new Dictionary<string, ContentFieldValue>(left.Count, StringComparer.Ordinal);
        for (int i = 0; i < left.Count; i++)
        {
            if (!byName.TryAdd(left[i].Name, left[i].Value))
            {
                return false;
            }
        }

        var seen = new HashSet<string>(right.Count, StringComparer.Ordinal);
        for (int i = 0; i < right.Count; i++)
        {
            if (!seen.Add(right[i].Name)
                || !byName.TryGetValue(right[i].Name, out ContentFieldValue held)
                || !held.Equals(right[i].Value))
            {
                return false;
            }
        }

        return true;
    }
}
