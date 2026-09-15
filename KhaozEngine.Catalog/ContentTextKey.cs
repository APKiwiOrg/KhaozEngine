using System;
using System.Text;

namespace KhaozEngine.Catalog;

/// <summary>
/// The ONE derivation of a content string's localization key, contracts 12.1:
/// <c>&lt;type key&gt;.&lt;content key&gt;.&lt;field&gt;</c>.
/// <para>
/// DERIVED, never authored and never stored. An authored key rots independently of the row it names, which
/// is exactly what happened to the two consumer keys that dropped an underscore every other key kept: two
/// rows out of thirty-five were enough to make every downstream tool do a lookup instead of a
/// concatenation. There is no authored override here and no place to put one.
/// </para>
/// <para>
/// The dot is unambiguous because it never appears inside a segment: the type key and the content key are
/// the <c>a-z0-9_</c> set of contracts 5.3, and the field name is drawn from the type's own declared field
/// list. A key therefore splits on the dot exactly.
/// </para>
/// </summary>
public static class ContentTextKey
{
    /// <summary>
    /// The total key length bound of contracts 12.2, which is three 64 character segments plus two dots. It
    /// is REACHABLE: three widest segments derive 194 characters, so <c>KEC0030</c> has something to check
    /// and this is not a bound that can only be hit by a malformed key.
    /// </summary>
    public const int MaxKeyLength = 192;

    /// <summary>The separator, which never appears inside a segment.</summary>
    public const char Separator = '.';

    /// <summary>
    /// Derives the key. The content key arrives as the UTF-8 bytes the runtime already holds, so a caller
    /// does not materialise a row key just to build one. This allocates and is the authoring, publish and
    /// diagnostic path rather than a frame-loop one.
    /// </summary>
    public static string Derive(string typeKey, ReadOnlySpan<byte> contentKeyUtf8, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(typeKey);
        ArgumentNullException.ThrowIfNull(fieldName);

        string contentKey = contentKeyUtf8.IsEmpty ? string.Empty : Encoding.UTF8.GetString(contentKeyUtf8);
        return string.Concat(typeKey, ".", contentKey, ".", fieldName);
    }

    /// <summary>
    /// True when the three segments would derive a key past <see cref="MaxKeyLength"/>, answered from the
    /// LENGTHS alone so a caller can check a whole type's rows without building a string for each.
    /// </summary>
    /// <param name="typeKey">The content type's key.</param>
    /// <param name="contentKeyLength">The row key's length in characters.</param>
    /// <param name="fieldName">The schema field's name.</param>
    public static bool ExceedsBound(string typeKey, int contentKeyLength, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(typeKey);
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentOutOfRangeException.ThrowIfNegative(contentKeyLength);

        return typeKey.Length + 1 + contentKeyLength + 1 + fieldName.Length > MaxKeyLength;
    }
}
