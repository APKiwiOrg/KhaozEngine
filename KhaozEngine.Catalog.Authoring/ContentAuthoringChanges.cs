using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One submitted batch of row and text intents, applied together or not at all by
/// <see cref="IContentTextAuthoringStore.ApplyChangesAsync"/>. A caller submits intents only: there is no
/// language-declaration command, because a Set is what introduces a language.
/// <para>
/// Both lists are OWNED copies. A row edit's byte payloads are copied too, so a caller reusing its arrays
/// after submitting cannot change what the store applies.
/// </para>
/// <para>
/// Two text intents naming one canonical target in one batch are refused, because the batch would otherwise
/// depend on its own order. Across batches the last intent wins, which the store decides.
/// </para>
/// </summary>
public sealed class ContentAuthoringChanges
{
    /// <summary>Builds one batch.</summary>
    /// <param name="rowEdits">The row intents, in order.</param>
    /// <param name="textEdits">The text intents, in order, one per canonical target.</param>
    /// <exception cref="ArgumentNullException">A list or an entry in one is null.</exception>
    /// <exception cref="ArgumentException">Two text intents name one canonical target.</exception>
    public ContentAuthoringChanges(IReadOnlyList<ContentEdit> rowEdits, IReadOnlyList<ContentTextEdit> textEdits)
    {
        ArgumentNullException.ThrowIfNull(rowEdits);
        ArgumentNullException.ThrowIfNull(textEdits);

        var rows = new ContentEdit[rowEdits.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = (rowEdits[i] ?? throw new ArgumentNullException(
                nameof(rowEdits), FormattableString.Invariant($"Row edit {i} is null."))).Owned();
        }

        RowEdits = rows;
        TextEdits = ContentDraftTextState.CopyEdits(textEdits, nameof(textEdits));
    }

    /// <summary>The row intents, owned.</summary>
    public IReadOnlyList<ContentEdit> RowEdits { get; }

    /// <summary>The text intents, owned, one per canonical target.</summary>
    public IReadOnlyList<ContentTextEdit> TextEdits { get; }
}
