using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The TEXT half of an upgrade run's draft proofs: whether a complete draft holds exactly a plan's text intents
/// and the language introductions those intents cause, and whether every text intent and introduction it
/// holds came from a plan the run computed.
/// <para>
/// <b>Introductions are sticky and they are compared.</b> A Set introduces its language when the baseline does
/// not declare it, and replacing that Set with a Remove keeps the introduction. So the introductions a plan
/// causes are exactly the canonical languages of its Sets that the baseline does not declare, and a draft
/// carrying any other introduction holds a change nobody planned: a rival's translation, kept or removed. Such
/// a draft is neither published as the plan nor discarded as known work.
/// </para>
/// </summary>
internal static class ContentUpgradeTextMatch
{
    /// <summary>
    /// The exact match a run's publish takes for one bound plan: the row-only proof for a row-only plan, and
    /// the complete proof against the plan's own baseline text for a plan carrying text.
    /// </summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    /// <param name="plan">The bound plan.</param>
    public static bool IsPlan(ContentDraft draft, ContentUpgradePlan plan)
        => plan.CarriesText
            ? IsPlan(draft, plan.Edits, plan.TextEdits, plan.BaselineText ?? ContentBundleTextCompatibility.Empty)
            : ContentUpgradeDraftMatch.IsPlan(draft, plan.Edits);

    /// <summary>The complete exact match, which <see cref="ContentUpgradeDraftMatch.IsPlan(ContentDraft, ContentAuthoringChanges, ContentBundleTextState)"/> exposes.</summary>
    public static bool IsPlan(
        ContentDraft draft,
        IReadOnlyList<ContentEdit> rows,
        IReadOnlyList<ContentTextEdit> text,
        ContentBundleTextState baselineText)
    {
        if (draft.TextState is not ContentDraftTextState held || (rows.Count == 0 && text.Count == 0))
        {
            return false;
        }

        return ContentUpgradeDraftMatch.RowsArePlan(draft.Changes.Edits, rows)
            && SameTextEdits(held.Edits, text)
            && SameLanguages(held.Introductions, Introduced(text, baselineText));
    }

    /// <summary>The complete known-work proof, which <see cref="ContentUpgradeDraftMatch"/> exposes.</summary>
    public static bool IsKnownWork(
        ContentDraft draft,
        IReadOnlyList<ContentEdit> knownRows,
        IReadOnlyList<ContentTextEdit> knownText)
    {
        if (draft.TextState is not ContentDraftTextState held
            || !ContentUpgradeDraftMatch.RowsAreKnown(draft.Changes.Edits, knownRows))
        {
            return false;
        }

        foreach (ContentTextEdit edit in held.Edits)
        {
            if (!Contains(knownText, edit))
            {
                return false;
            }
        }

        var setLanguages = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextEdit edit in knownText)
        {
            if (edit.Operation == ContentTextEditOperation.Set)
            {
                setLanguages.Add(edit.Target.Language);
            }
        }

        foreach (ContentTextLanguageDeclaration introduction in held.Introductions)
        {
            // A known Set introduces its language under its canonical spelling and nothing else.
            if (!setLanguages.Contains(introduction.Language)
                || !string.Equals(introduction.WireTag, introduction.Language, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether any text intent the draft holds is one of the known ones.</summary>
    public static bool HoldsKnownText(ContentDraft draft, IReadOnlyList<ContentTextEdit> knownText)
    {
        if (draft.TextState is not ContentDraftTextState held)
        {
            return false;
        }

        foreach (ContentTextEdit edit in held.Edits)
        {
            if (Contains(knownText, edit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The canonical languages a plan's Sets introduce against the baseline's declarations.</summary>
    static HashSet<string> Introduced(IReadOnlyList<ContentTextEdit> text, ContentBundleTextState baselineText)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguageDeclaration language in baselineText.Languages)
        {
            declared.Add(language.Language);
        }

        var introduced = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextEdit edit in text)
        {
            if (edit.Operation == ContentTextEditOperation.Set && !declared.Contains(edit.Target.Language))
            {
                introduced.Add(edit.Target.Language);
            }
        }

        return introduced;
    }

    /// <summary>Whether the held introductions are exactly the expected languages, each under its canonical spelling.</summary>
    static bool SameLanguages(IReadOnlyList<ContentTextLanguageDeclaration> held, HashSet<string> expected)
    {
        if (held.Count != expected.Count)
        {
            return false;
        }

        foreach (ContentTextLanguageDeclaration introduction in held)
        {
            if (!expected.Contains(introduction.Language)
                || !string.Equals(introduction.WireTag, introduction.Language, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether two text intent lists are the same set, order independent, operation and value included.</summary>
    static bool SameTextEdits(IReadOnlyList<ContentTextEdit> held, IReadOnlyList<ContentTextEdit> planned)
    {
        if (held.Count != planned.Count)
        {
            return false;
        }

        var byTarget = new Dictionary<ContentTextTarget, ContentTextEdit>(held.Count);
        foreach (ContentTextEdit edit in held)
        {
            if (!byTarget.TryAdd(edit.Target, edit))
            {
                return false;
            }
        }

        foreach (ContentTextEdit wanted in planned)
        {
            if (!byTarget.TryGetValue(wanted.Target, out ContentTextEdit? standing) || !standing.Equals(wanted))
            {
                return false;
            }
        }

        return true;
    }

    static bool Contains(IReadOnlyList<ContentTextEdit> list, ContentTextEdit edit)
    {
        foreach (ContentTextEdit candidate in list)
        {
            if (candidate.Equals(edit))
            {
                return true;
            }
        }

        return false;
    }
}
