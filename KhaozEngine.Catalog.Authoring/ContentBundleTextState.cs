using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One complete text value as a bundle carries it: the canonical target, by type, content key, marker field
/// and language rather than by a raw derived key, and the complete value.
/// </summary>
public sealed record ContentBundleTextValue
{
    /// <summary>Builds one value.</summary>
    /// <param name="target">The string the value belongs to.</param>
    /// <param name="value">The complete value, at most <see cref="ContentTextEdit.MaxValueBytes"/> strict UTF-8 bytes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The value is invalid UTF-8 or too long.</exception>
    public ContentBundleTextValue(ContentTextTarget target, string value)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        Value = ContentTextEdit.RequireValue(value, nameof(value));
    }

    /// <summary>The string the value belongs to.</summary>
    public ContentTextTarget Target { get; }

    /// <summary>The complete value.</summary>
    public string Value { get; }
}

/// <summary>
/// The protected TEXT SECTION of a bundle: every declared language with its wire spelling, empty languages
/// included, and every complete value. A bundle carrying one is text-bearing, and no row-only route imports
/// or exports it, because only its row half would land.
/// </summary>
public sealed class ContentBundleTextState
{
    /// <summary>Builds one text section from owned copies.</summary>
    /// <param name="languages">Every declared language, one per identity and spelling.</param>
    /// <param name="values">Every complete value, one per target, each in a declared language.</param>
    /// <exception cref="ArgumentNullException">A list or an entry is null.</exception>
    /// <exception cref="ArgumentException">A language or a target repeats, or a value names an undeclared language.</exception>
    public ContentBundleTextState(
        IReadOnlyList<ContentTextLanguageDeclaration> languages,
        IReadOnlyList<ContentBundleTextValue> values)
    {
        Languages = ContentDraftTextState.CopyDeclarations(languages, nameof(languages));
        ArgumentNullException.ThrowIfNull(values);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguageDeclaration language in Languages)
        {
            declared.Add(language.Language);
        }

        var copy = new ContentBundleTextValue[values.Count];
        var targets = new HashSet<ContentTextTarget>();
        for (int i = 0; i < copy.Length; i++)
        {
            ContentBundleTextValue value = values[i] ?? throw new ArgumentNullException(
                nameof(values), FormattableString.Invariant($"Value {i} is null."));
            if (!targets.Add(value.Target) || !declared.Contains(value.Target.Language))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Bundle text value {i} repeats a target or names undeclared language '{value.Target.Language}'."),
                    nameof(values));
            }

            copy[i] = value;
        }

        Values = copy;
    }

    /// <summary>Every declared language.</summary>
    public IReadOnlyList<ContentTextLanguageDeclaration> Languages { get; }

    /// <summary>Every complete value.</summary>
    public IReadOnlyList<ContentBundleTextValue> Values { get; }

    /// <summary>True when the section declares no language and carries no value.</summary>
    public bool IsEmpty => Languages.Count == 0 && Values.Count == 0;
}
