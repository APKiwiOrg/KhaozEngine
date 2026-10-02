using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// ONE temporal text value: the type, definition id, marker field and canonical language it belongs to, its
/// complete value, the version it became valid in and the version it was replaced in, or null while live.
/// Text is temporal like rows, so "what was this name at version 40" is a query and a translation-only
/// publish never rewrites the row it names.
/// </summary>
public sealed record ContentTextRevision
{
    /// <summary>Builds one revision.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="definitionId">The row's definition id, at least 1.</param>
    /// <param name="fieldName">The localized text marker field.</param>
    /// <param name="language">The language, stored canonical.</param>
    /// <param name="value">The complete value, at most <see cref="ContentTextEdit.MaxValueBytes"/> strict UTF-8 bytes.</param>
    /// <param name="validFromVersion">The version it became valid in, at least 1.</param>
    /// <param name="replacedInVersion">The version it was replaced in, above <paramref name="validFromVersion"/>, or null while live.</param>
    /// <exception cref="ArgumentNullException">A string is null.</exception>
    /// <exception cref="ArgumentException">The field is blank, the language is illegal, or the value is invalid or too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An id or a version is out of range.</exception>
    public ContentTextRevision(
        ContentTypeId type,
        int definitionId,
        string fieldName,
        string language,
        string value,
        int validFromVersion,
        int? replacedInVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(definitionId, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(validFromVersion, 1);
        ArgumentNullException.ThrowIfNull(fieldName);
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("A text revision names its localized text marker field.", nameof(fieldName));
        }

        if (replacedInVersion is int replaced)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(replaced, validFromVersion, nameof(replacedInVersion));
        }

        Value = ContentTextEdit.RequireValue(value, nameof(value));
        Type = type;
        DefinitionId = definitionId;
        FieldName = fieldName;
        Language = ContentTextLanguageTag.Normalize(language);
        ValidFromVersion = validFromVersion;
        ReplacedInVersion = replacedInVersion;
    }

    /// <summary>The content type.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The row's definition id.</summary>
    public int DefinitionId { get; }

    /// <summary>The localized text marker field.</summary>
    public string FieldName { get; }

    /// <summary>The canonical language.</summary>
    public string Language { get; }

    /// <summary>The complete value.</summary>
    public string Value { get; }

    /// <summary>The version it became valid in.</summary>
    public int ValidFromVersion { get; }

    /// <summary>The version it was replaced in, or null while live.</summary>
    public int? ReplacedInVersion { get; }

    /// <summary>Whether the revision is the visible value at <paramref name="versionNumber"/>.</summary>
    /// <param name="versionNumber">The version.</param>
    public bool IsLiveAt(int versionNumber)
        => ValidFromVersion <= versionNumber && (ReplacedInVersion is not int replaced || replaced > versionNumber);

    /// <summary>Whether <paramref name="other"/> names the same string: type, id, field and language.</summary>
    /// <param name="other">The revision to compare.</param>
    public bool IsSameString(ContentTextRevision other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Type == other.Type
            && DefinitionId == other.DefinitionId
            && string.Equals(FieldName, other.FieldName, StringComparison.Ordinal)
            && string.Equals(Language, other.Language, StringComparison.Ordinal);
    }

    /// <summary>The same revision closed at <paramref name="versionNumber"/>.</summary>
    /// <param name="versionNumber">The version that replaces it.</param>
    public ContentTextRevision ClosedIn(int versionNumber)
        => new(Type, DefinitionId, FieldName, Language, Value, ValidFromVersion, versionNumber);
}
