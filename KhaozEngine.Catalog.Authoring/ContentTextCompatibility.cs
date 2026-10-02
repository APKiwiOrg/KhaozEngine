using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The fail-closed gates every ROW-ONLY route runs before it can drop text, and the complete comparisons the
/// text routes confirm against. A provider that stores no text still runs the DTO gates, because a plan or a
/// bundle built elsewhere can carry text its row-only commit or import would silently lose.
/// <para>
/// A caller's DTO is never proof that text is absent. These gates refuse what a DTO SAYS it carries, and the
/// backend gates refuse what the backend actually HOLDS. Both answer with
/// <see cref="ContentAuthoringException.TextUnrepresentedReason"/>.
/// </para>
/// </summary>
internal static class ContentTextCompatibility
{
    /// <summary>The bundle format a row-only route reads, which is text free by contract.</summary>
    const int RowOnlyBundleFormat = 1;

    /// <summary>Whether a draft's text state holds any intent or introduction.</summary>
    public static bool HoldsText(ContentDraft? draft) => draft?.TextState is { IsEmpty: false };

    /// <summary>
    /// Refuses a plan that is the row half of a text plan, which a row-only commit would publish without its
    /// text, its declarations and the copies its version owes.
    /// </summary>
    /// <param name="plan">The plan handed to a row-only commit.</param>
    /// <param name="member">The member refusing, which the message names.</param>
    public static void RequireRowOnlyPlan(ContentPublishPlan plan, string member)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.FrozenTextState is not null)
        {
            throw Unrepresented(member, FormattableString.Invariant(
                $"the plan for version {plan.VersionNumber} is the row half of a text plan"));
        }
    }

    /// <summary>
    /// Refuses a bundle a row-only import cannot represent: one carrying a text section, or one whose format
    /// implies a section it no longer carries.
    /// </summary>
    /// <param name="bundle">The bundle handed to a row-only import.</param>
    /// <param name="member">The member refusing, which the message names.</param>
    public static void RequireRowOnlyBundle(ContentBundle bundle, string member)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.TextState is not null)
        {
            throw Unrepresented(member, "the bundle carries a text section");
        }

        if (bundle.FormatVersion != RowOnlyBundleFormat)
        {
            throw Unrepresented(member, FormattableString.Invariant(
                $"the bundle is format {bundle.FormatVersion}, which carries a text section this one has lost"));
        }
    }

    /// <summary>Refuses a draft whose actual text a row-only route would drop.</summary>
    /// <param name="draft">The draft the backend actually holds.</param>
    /// <param name="member">The member refusing, which the message names.</param>
    public static void RequireNoHeldText(ContentDraft? draft, string member)
    {
        if (HoldsText(draft))
        {
            throw Unrepresented(member, FormattableString.Invariant(
                $"the open draft holds {draft!.TextEditCount} text intent(s) and {draft.LanguageIntroductionCount} language introduction(s)"));
        }
    }

    /// <summary>
    /// Whether two drafts are the same COMPLETE draft: both carry a text state, and their base, freeze marker,
    /// opener, stamp, note, row edits and text state are equal in order. A row-only draft is never the same.
    /// </summary>
    public static bool SameDraft(ContentDraft expected, ContentDraft actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        return expected.TextState is ContentDraftTextState text
            && text.IsSameAs(actual.TextState)
            && expected.BaseVersion == actual.BaseVersion
            && expected.FrozenForBaseVersion == actual.FrozenForBaseVersion
            && string.Equals(expected.OpenedBy, actual.OpenedBy, StringComparison.Ordinal)
            && expected.OpenedAtUtc == actual.OpenedAtUtc
            && string.Equals(expected.Note, actual.Note, StringComparison.Ordinal)
            && SameEdits(expected.Changes.Edits, actual.Changes.Edits);
    }

    /// <summary>Whether two row edit lists are equal in order, every target and payload half included.</summary>
    public static bool SameEdits(IReadOnlyList<ContentEdit> left, IReadOnlyList<ContentEdit> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!SameEdit(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The refusal every gate here throws.</summary>
    /// <param name="member">The member refusing.</param>
    /// <param name="detail">What it could not represent.</param>
    public static ContentAuthoringException Unrepresented(string member, string detail)
        => new(
            FormattableString.Invariant(
                $"{member} is refused because {detail}, and this row-only route cannot carry text. Use the text authoring companion, which publishes, discards and moves rows and text together."),
            default,
            0,
            ContentAuthoringException.TextUnrepresentedReason);

    static bool SameEdit(ContentEdit left, ContentEdit right)
    {
        if (left.Type != right.Type
            || left.DefinitionId != right.DefinitionId
            || !left.Key.Equals(right.Key)
            || left.Operation != right.Operation
            || left.RetirePolicy != right.RetirePolicy
            || left.ReplacementId != right.ReplacementId
            || !left.ForkKey.Equals(right.ForkKey)
            || !string.Equals(left.ForkFlagField, right.ForkFlagField, StringComparison.Ordinal)
            || left.FamilyId != right.FamilyId
            || left.ImportedAsRetired != right.ImportedAsRetired
            || left.Fields.Count != right.Fields.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Fields.Count; i++)
        {
            if (!string.Equals(left.Fields[i].Name, right.Fields[i].Name, StringComparison.Ordinal)
                || !left.Fields[i].Value.Equals(right.Fields[i].Value))
            {
                return false;
            }
        }

        return true;
    }
}
