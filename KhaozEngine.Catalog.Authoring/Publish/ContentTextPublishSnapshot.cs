using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What <see cref="IContentTextAuthoringStore.FreezeChangesAsync"/> reads in one consistent step: the store
/// epoch, the row baseline a publish is prepared against, the COMPLETE text of that same baseline version,
/// and the complete frozen draft, text state and language introductions included.
/// <para>
/// The draft is an owned copy, so a caller mutating the snapshot's change set cannot reach the store's draft,
/// and the commit compares the store's actual state against this copy rather than trusting it.
/// </para>
/// </summary>
public sealed class ContentTextPublishSnapshot
{
    /// <summary>Builds one snapshot.</summary>
    /// <param name="storeEpoch">The identity of the store frozen.</param>
    /// <param name="baseline">The row baseline, read at the frozen base version.</param>
    /// <param name="baselineText">The complete text of the same base version.</param>
    /// <param name="draft">The complete draft, frozen for the base version.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The parts disagree about the epoch or the base version, the draft is not frozen for it, or its text is unrepresented.</exception>
    public ContentTextPublishSnapshot(
        string storeEpoch,
        ContentPublishBaseline baseline,
        ContentVersionTextSnapshot baselineText,
        ContentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(storeEpoch);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baselineText);
        ArgumentNullException.ThrowIfNull(draft);

        if (!string.Equals(storeEpoch, baselineText.StoreEpoch, StringComparison.Ordinal)
            || baselineText.VersionNumber != baseline.VersionNumber)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The baseline text is version {baselineText.VersionNumber} of another read than row baseline {baseline.VersionNumber}."),
                nameof(baselineText));
        }

        if (draft.TextState is not ContentDraftTextState text || draft.FrozenForBaseVersion != baseline.VersionNumber)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A text publish snapshot holds a complete draft frozen for base version {baseline.VersionNumber}."),
                nameof(draft));
        }

        StoreEpoch = storeEpoch;
        Baseline = baseline;
        BaselineText = baselineText;
        Draft = new ContentDraft(
            text, draft.BaseVersion, draft.OpenedBy, draft.OpenedAtUtc, draft.Note, draft.Changes, draft.FrozenForBaseVersion);
    }

    /// <summary>The identity of the store frozen.</summary>
    public string StoreEpoch { get; }

    /// <summary>The row baseline the publish is prepared against.</summary>
    public ContentPublishBaseline Baseline { get; }

    /// <summary>The complete text of the base version.</summary>
    public ContentVersionTextSnapshot BaselineText { get; }

    /// <summary>The complete frozen draft, owned.</summary>
    public ContentDraft Draft { get; }

    /// <summary>The base version the draft is frozen for.</summary>
    public int BaseVersion => Baseline.VersionNumber;

    /// <summary>The frozen draft's complete text state.</summary>
    public ContentDraftTextState TextState => Draft.TextState!;
}
