using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One language a committed version RECORDS: its canonical identity, the exact wire spelling its manifests
/// carry, and the hash of its KECT chunk. A language with no values is still recorded, with the hash of its
/// empty chunk, because a declared language stays constructible for a caller's default.
/// </summary>
public sealed record ContentTextLanguage
{
    /// <summary>Builds one recorded language.</summary>
    /// <param name="language">The identity, stored canonical.</param>
    /// <param name="wireTag">The exact manifest spelling, which must map to the identity.</param>
    /// <param name="hash">The text chunk's content address, lower hex.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A tag is illegal, the spelling maps elsewhere, or the hash is not lower hex.</exception>
    public ContentTextLanguage(string language, string wireTag, string hash)
    {
        var declaration = new ContentTextLanguageDeclaration(language, wireTag);
        Language = declaration.Language;
        WireTag = declaration.WireTag;
        Hash = RequireHash(hash, nameof(hash));
    }

    /// <summary>The canonical, lowered identity.</summary>
    public string Language { get; }

    /// <summary>The exact spelling both manifests carry.</summary>
    public string WireTag { get; }

    /// <summary>The text chunk's content address, lower hex.</summary>
    public string Hash { get; }

    /// <summary>The same language without its hash, which is how the next draft sees it declared.</summary>
    public ContentTextLanguageDeclaration Declaration => new(Language, WireTag);

    /// <summary>A content address checked as non-empty lower hex.</summary>
    /// <param name="hash">The address.</param>
    /// <param name="parameterName">The argument a refusal names.</param>
    internal static string RequireHash(string hash, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(hash, parameterName);
        if (hash.Length == 0)
        {
            throw new ArgumentException("A content address may not be empty.", parameterName);
        }

        foreach (char c in hash)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"Content address '{hash}' is not lower hex."), parameterName);
            }
        }

        return hash;
    }
}
