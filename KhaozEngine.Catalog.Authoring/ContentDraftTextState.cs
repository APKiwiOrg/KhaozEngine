using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The text half of a draft: ordered text intents, one per canonical target, and the languages the draft
/// INTRODUCES. An introduction is independent of the intents: a Set introduces its language, and replacing
/// that Set with a Remove before publication keeps the introduction, so the language still publishes empty.
/// <para>
/// A draft carrying this object holds a complete, backend-read text state. A draft carrying NULL in its
/// place was built by a row-only route and says nothing about text, which is never proof of absence.
/// </para>
/// </summary>
public sealed class ContentDraftTextState
{
    static readonly ContentTextEdit[] NoEdits = [];
    static readonly ContentTextLanguageDeclaration[] NoIntroductions = [];

    /// <summary>Builds one state from owned copies of both lists.</summary>
    /// <param name="edits">The text intents in ordinal order, one per canonical target.</param>
    /// <param name="introductions">The pending language introductions in order, one per identity.</param>
    /// <exception cref="ArgumentNullException">A list or an entry in one is null.</exception>
    /// <exception cref="ArgumentException">Two intents share a target, or two introductions share an identity or a spelling.</exception>
    public ContentDraftTextState(
        IReadOnlyList<ContentTextEdit> edits,
        IReadOnlyList<ContentTextLanguageDeclaration> introductions)
    {
        Edits = CopyEdits(edits, nameof(edits));
        Introductions = CopyDeclarations(introductions, nameof(introductions));
    }

    /// <summary>A state holding nothing, which a backend that represents text returns for a row-only draft.</summary>
    public static ContentDraftTextState Empty { get; } = new(NoEdits, NoIntroductions);

    /// <summary>The text intents, one per canonical target, in the order they were first applied.</summary>
    public IReadOnlyList<ContentTextEdit> Edits { get; }

    /// <summary>The languages this draft introduces, with their canonical spelling.</summary>
    public IReadOnlyList<ContentTextLanguageDeclaration> Introductions { get; }

    /// <summary>True when the state holds no intent and no introduction.</summary>
    public bool IsEmpty => Edits.Count == 0 && Introductions.Count == 0;

    /// <summary>
    /// Whether <paramref name="other"/> holds the same intents and introductions in the same order. This is
    /// the complete comparison an atomic discard or a commit confirmation turns on.
    /// </summary>
    /// <param name="other">The state to compare.</param>
    public bool IsSameAs(ContentDraftTextState? other)
    {
        if (other is null || other.Edits.Count != Edits.Count || other.Introductions.Count != Introductions.Count)
        {
            return false;
        }

        for (int i = 0; i < Edits.Count; i++)
        {
            if (!Edits[i].Equals(other.Edits[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < Introductions.Count; i++)
        {
            if (!Introductions[i].Equals(other.Introductions[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An owned copy of text intents, refusing a null entry and a repeated canonical target.</summary>
    internal static ContentTextEdit[] CopyEdits(IReadOnlyList<ContentTextEdit> edits, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(edits, parameterName);
        var copy = new ContentTextEdit[edits.Count];
        var seen = new HashSet<ContentTextTarget>();
        for (int i = 0; i < copy.Length; i++)
        {
            ContentTextEdit edit = edits[i] ?? throw new ArgumentNullException(
                parameterName, FormattableString.Invariant($"Text edit {i} is null."));
            if (!seen.Add(edit.Target))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Text edit {i} names type {edit.Target.Type.Value} row '{edit.Target.Key}' field '{edit.Target.FieldName}' language '{edit.Target.Language}' a second time. One batch holds one intent per canonical target."),
                    parameterName);
            }

            copy[i] = edit;
        }

        return copy;
    }

    /// <summary>An owned copy of declarations, refusing a null entry and a repeated identity or spelling.</summary>
    internal static ContentTextLanguageDeclaration[] CopyDeclarations(
        IReadOnlyList<ContentTextLanguageDeclaration> declarations,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(declarations, parameterName);
        var copy = new ContentTextLanguageDeclaration[declarations.Count];
        var languages = new HashSet<string>(StringComparer.Ordinal);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < copy.Length; i++)
        {
            ContentTextLanguageDeclaration declaration = declarations[i] ?? throw new ArgumentNullException(
                parameterName, FormattableString.Invariant($"Declaration {i} is null."));
            if (!languages.Add(declaration.Language) || !tags.Add(declaration.WireTag))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Language '{declaration.Language}' is declared twice. One identity has one spelling."),
                    parameterName);
            }

            copy[i] = declaration;
        }

        return copy;
    }
}
