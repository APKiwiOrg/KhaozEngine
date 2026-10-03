using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One declared language without a hash: its canonical identity and the exact WIRE spelling a manifest names
/// it by. A pending introduction carries no hash because no chunk exists for it until publish.
/// <para>
/// The wire spelling must map to the identity under <see cref="ContentTextLanguageTag"/>, which is what lets
/// a published <c>en-US</c> keep its historical spelling while <c>en-us</c> stays its identity. A spelling
/// that maps elsewhere, or breaks the grammar, is refused rather than repaired.
/// </para>
/// </summary>
public sealed record ContentTextLanguageDeclaration
{
    /// <summary>Builds one declaration.</summary>
    /// <param name="language">The identity, in any legal spelling, stored canonical.</param>
    /// <param name="wireTag">The exact spelling a manifest carries, which must map to the same identity.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A tag breaks the grammar, or the spelling maps to another identity.</exception>
    public ContentTextLanguageDeclaration(string language, string wireTag)
    {
        Language = ContentTextLanguageTag.Normalize(language);
        ArgumentNullException.ThrowIfNull(wireTag);
        if (!string.Equals(ContentTextLanguageTag.Normalize(wireTag), Language, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Wire tag '{wireTag}' does not spell language '{Language}'."),
                nameof(wireTag));
        }

        WireTag = wireTag;
    }

    /// <summary>The canonical, lowered identity.</summary>
    public string Language { get; }

    /// <summary>The exact spelling a manifest names the language by.</summary>
    public string WireTag { get; }

    /// <summary>A new declaration, whose wire spelling is its canonical identity.</summary>
    /// <param name="language">The identity in any legal spelling.</param>
    public static ContentTextLanguageDeclaration Introduce(string language)
    {
        string canonical = ContentTextLanguageTag.Normalize(language);
        return new ContentTextLanguageDeclaration(canonical, canonical);
    }
}
