using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// The key shape of contracts 5.3, shared by validation and authoring preconditions. This predicate leaves
/// <see cref="ContentKey"/> construction permissive so a sweep can report every malformed key.
/// </summary>
public static class ContentKeyRules
{
    /// <summary>The longest legal content key, which matches the consumers' own key columns.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The whole rule as one sentence for a refusal message to append after the defect.</summary>
    public static string Rule { get; } = FormattableString.Invariant(
        $"A key is 1 to {MaxKeyLength} characters of a-z, 0-9 and underscore, with no leading digit, no leading or trailing underscore and no double underscore.");

    /// <summary>Names the first defect, or null for a legal key, using UTF-16 diagnostic positions and lengths.</summary>
    /// <param name="key">The candidate key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static string? Defect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Defect(default, key.AsSpan());
    }

    /// <summary>Names the first defect, or null for a legal key, using UTF-8 diagnostic positions and lengths.</summary>
    /// <param name="key">The candidate key's UTF-8 bytes.</param>
    public static string? Defect(ReadOnlySpan<byte> key) => Defect(key, default);

    static string? Defect(ReadOnlySpan<byte> utf8, ReadOnlySpan<char> text)
    {
        // Legal keys are ASCII in either representation. Keep each caller's existing diagnostic units for
        // malformed non-ASCII input without encoding a string or materialising a runtime key.
        bool isText = !text.IsEmpty;
        int length = isText ? text.Length : utf8.Length;
        if (length == 0)
        {
            return "empty";
        }

        if (length > MaxKeyLength)
        {
            return FormattableString.Invariant($"{length} characters long");
        }

        bool previousUnderscore = false;
        for (int i = 0; i < length; i++)
        {
            int character = isText ? (int)text[i] : utf8[i];
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return FormattableString.Invariant($"outside the character set at position {i}");
            }

            if (character == '_' && previousUnderscore)
            {
                return FormattableString.Invariant($"a double underscore at position {i}");
            }

            previousUnderscore = character == '_';
        }

        int first = isText ? (int)text[0] : utf8[0];
        if (first is >= '0' and <= '9')
        {
            return "a leading digit";
        }

        return first == '_' ? "a leading underscore" : previousUnderscore ? "a trailing underscore" : null;
    }
}
