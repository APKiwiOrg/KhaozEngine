using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The string one text edit names: the content type, the row's immutable content key, the declared
/// localized text marker field and the CANONICAL language identity. The derived localization key is a
/// function of the first three (<see cref="ContentTextKey"/>), so no raw key and no key override exists here.
/// <para>
/// It names the row by KEY rather than by definition id, because a pending add has no id until publish. Two
/// targets are equal when their type, key bytes, field name and canonical language are, so <c>en-US</c> and
/// <c>EN-us</c> name one string. Equality is ordinal throughout, like every other catalog key.
/// </para>
/// </summary>
public sealed record ContentTextTarget
{
    /// <summary>Builds one target, normalizing the language.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The row's content key.</param>
    /// <param name="fieldName">The declared localized text marker field.</param>
    /// <param name="language">The language identity in any legal spelling.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> or <paramref name="language"/> is null.</exception>
    /// <exception cref="ArgumentException">The key is empty, the field is blank, or the language breaks the grammar.</exception>
    public ContentTextTarget(ContentTypeId type, ContentKey key, string fieldName, string language)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        if (key.IsEmpty)
        {
            throw new ArgumentException("A text target names a row by its content key, which may not be empty.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("A text target names its localized text marker field.", nameof(fieldName));
        }

        Type = type;
        Key = key;
        FieldName = fieldName;
        Language = ContentTextLanguageTag.Normalize(language);
    }

    /// <summary>The content type.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The row's immutable content key.</summary>
    public ContentKey Key { get; }

    /// <summary>The declared localized text marker field.</summary>
    public string FieldName { get; }

    /// <summary>The canonical, lowered language identity.</summary>
    public string Language { get; }
}
