using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Which of the three shapes a plan is, which is the whole of a planner's vocabulary.</summary>
public enum ContentUpgradePlanKind
{
    /// <summary>Edits to publish, with one human-readable line per change.</summary>
    Changes,

    /// <summary>The catalog already carries the content, so nothing is published and the id is adopted.</summary>
    AlreadySatisfied,

    /// <summary>The planner will not act on this catalog, and the reason says why and what to do next.</summary>
    Refused,
}

/// <summary>
/// What a planner answers with: edits, already satisfied, or refused. There are exactly three shapes and no
/// fourth, because a planner that could answer "partly" would be asking the runner to guess which half of a
/// half-applied upgrade an operator owns.
/// <para>
/// <b>Detect by identity, never by value.</b> A row present under the committed id AND the committed key is
/// satisfied whatever its field values are, because those values may be operator tuning that an upgrade has
/// no business reverting.
/// </para>
/// </summary>
public sealed class ContentUpgradePlan
{
    static readonly string[] NoLines = [];
    static readonly ContentEdit[] NoEdits = [];
    static readonly ContentTextEdit[] NoTextEdits = [];

    ContentUpgradePlan(
        ContentUpgradePlanKind kind,
        IReadOnlyList<ContentEdit> edits,
        IReadOnlyList<string> changeLines,
        string reason)
        : this(kind, edits, NoTextEdits, changeLines, reason, null)
    {
    }

    ContentUpgradePlan(
        ContentUpgradePlanKind kind,
        IReadOnlyList<ContentEdit> edits,
        IReadOnlyList<ContentTextEdit> textEdits,
        IReadOnlyList<string> changeLines,
        string reason,
        ContentBundleTextState? baselineText)
    {
        Kind = kind;
        Edits = edits;
        TextEdits = textEdits;
        ChangeLines = changeLines;
        Reason = reason;
        BaselineText = baselineText;
    }

    /// <summary>Which shape this is.</summary>
    public ContentUpgradePlanKind Kind { get; }

    /// <summary>The row edits to publish, and empty for the other two shapes and for a text-only change set.</summary>
    public IReadOnlyList<ContentEdit> Edits { get; }

    /// <summary>
    /// The text intents to publish, one per canonical target, and empty for a row-only change set and the other
    /// two shapes. A plan carrying any is applied through the text authoring companion, and its proofs compare
    /// the draft's text intents and language introductions as well as its rows.
    /// </summary>
    public IReadOnlyList<ContentTextEdit> TextEdits { get; }

    /// <summary>Whether this change set carries text, which only a store with the text companion can apply.</summary>
    internal bool CarriesText => TextEdits.Count > 0;

    /// <summary>
    /// The text the baseline bundle carried when the run planned this change set, which decides the languages
    /// its Sets introduce. Null until the run binds the plan to the baseline it was computed against.
    /// </summary>
    internal ContentBundleTextState? BaselineText { get; }

    /// <summary>One line per change, which is what a preview prints and what the report carries.</summary>
    public IReadOnlyList<string> ChangeLines { get; }

    /// <summary>Why the catalog is satisfied or why the planner refused, and empty under <see cref="ContentUpgradePlanKind.Changes"/>.</summary>
    public string Reason { get; }

    /// <summary>
    /// Edits to publish. <b>An empty edit list is an ArgumentException rather than an empty plan</b>, because
    /// a planner with nothing to do is saying <see cref="AlreadySatisfied"/> or <see cref="Refused"/>, and
    /// publishing a version that changes no row would move the version number for nothing.
    /// </summary>
    /// <param name="edits">The edits, in the order they are applied to the draft.</param>
    /// <param name="changeLines">One readable line per change, which an operator reviews before an apply.</param>
    /// <exception cref="ArgumentNullException">An argument is null, or an entry in one is.</exception>
    /// <exception cref="ArgumentException"><paramref name="edits"/> is empty.</exception>
    public static ContentUpgradePlan Changes(
        IReadOnlyList<ContentEdit> edits,
        IReadOnlyList<string> changeLines)
    {
        ContentEdit[] copiedEdits = Copy(edits, nameof(edits));
        if (copiedEdits.Length == 0)
        {
            throw new ArgumentException(
                "A plan that changes nothing is AlreadySatisfied or Refused, never an empty change list.",
                nameof(edits));
        }

        return new ContentUpgradePlan(
            ContentUpgradePlanKind.Changes,
            copiedEdits,
            Copy(changeLines, nameof(changeLines)),
            string.Empty);
    }

    /// <summary>
    /// A COMPLETE change set to publish: row edits and text intents together, applied in one batch and published
    /// as one version. A text-only change set is real authored work and publishes like any other. A change set
    /// with neither rows nor text is an ArgumentException, for the same reason an empty row list is.
    /// <para>
    /// The planner submits intents only. A Set introduces its language when the baseline does not declare it,
    /// and there is no language-only command, so a change set cannot declare a language without a value.
    /// </para>
    /// </summary>
    /// <param name="changes">The row edits and text intents, in the order they are applied to the draft.</param>
    /// <param name="changeLines">One readable line per change, which an operator reviews before an apply.</param>
    /// <exception cref="ArgumentNullException">An argument is null, or an entry in one is.</exception>
    /// <exception cref="ArgumentException"><paramref name="changes"/> carries neither a row edit nor a text intent.</exception>
    public static ContentUpgradePlan Changes(
        ContentAuthoringChanges changes,
        IReadOnlyList<string> changeLines)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.RowEdits.Count == 0 && changes.TextEdits.Count == 0)
        {
            throw new ArgumentException(
                "A plan that changes nothing is AlreadySatisfied or Refused, never an empty change set.",
                nameof(changes));
        }

        return new ContentUpgradePlan(
            ContentUpgradePlanKind.Changes,
            Copy(changes.RowEdits, nameof(changes)),
            Copy(changes.TextEdits, nameof(changes)),
            Copy(changeLines, nameof(changeLines)),
            string.Empty,
            null);
    }

    /// <summary>The same change set bound to the text its baseline carried, which its proofs compare against.</summary>
    /// <param name="baselineText">The baseline bundle's complete text.</param>
    internal ContentUpgradePlan Against(ContentBundleTextState baselineText)
        => new(Kind, Edits, TextEdits, ChangeLines, Reason, baselineText);

    /// <summary>The change set as the companion applies it.</summary>
    internal ContentAuthoringChanges ToChanges() => new(Edits, TextEdits);

    /// <summary>
    /// The catalog already carries the content. The runner records the id as
    /// <see cref="ContentUpgradeDisposition.Adopted"/> and publishes nothing, which is what a catalog repaired
    /// by an older explicit command reads as.
    /// </summary>
    /// <param name="reason">What was found, which the report renders beside the id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    public static ContentUpgradePlan AlreadySatisfied(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new ContentUpgradePlan(ContentUpgradePlanKind.AlreadySatisfied, NoEdits, NoLines, reason);
    }

    /// <summary>
    /// The planner will not act. Nothing is written: no draft, no version, no pin move and no ledger row, and
    /// the run stops at this definition with the earlier ones left applied.
    /// </summary>
    /// <param name="reason">Why, naming what is wrong and the action an operator takes next.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    public static ContentUpgradePlan Refused(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new ContentUpgradePlan(ContentUpgradePlanKind.Refused, NoEdits, NoLines, reason);
    }

    static T[] Copy<T>(IReadOnlyList<T> source, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        var copy = new T[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            copy[i] = source[i] ?? throw new ArgumentNullException(
                parameterName, FormattableString.Invariant($"Entry {i} is null."));
        }

        return copy;
    }
}
