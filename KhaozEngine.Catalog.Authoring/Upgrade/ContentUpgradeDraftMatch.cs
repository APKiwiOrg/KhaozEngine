using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Whether an open draft holds EXACTLY the edits a plan would put in it. This is the proof an upgrade run
/// needs before it treats a draft as its own, and it is a public check because it is the one comparison a
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

        for (int i = 0; i < right.Count; i++)
        {
            if (!byName.TryGetValue(right[i].Name, out ContentFieldValue held)
                || !held.Equals(right[i].Value))
            {
                return false;
            }
        }

        return true;
    }
}
