using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The key shape of contracts 5.3, as ONE predicate with ONE sentence describing it.
/// <para>
/// <b>It is public because the rule has three callers at three layers and they are not one call site.</b> The
/// validator's <c>KEC0001</c> sweep walks the rows a candidate already holds. A fork's copy key never reaches
/// that sweep, because the row it would go on does not exist until publish. And the admin boundary has to
/// refuse an add's key BEFORE the edit enters the draft, because a draft carrying a malformed key is wedged:
/// every later validate reports the defect, every later publish refuses, and the only removal on the
/// authoring seam is a discard, which takes every other pending edit in the draft with it.
/// </para>
/// <para>
/// <b>The rule is not <see cref="ContentKey"/>'s own.</b> A bad key has to reach the sweep intact, so that a
/// bulk import reports every one of them in a single pass rather than throwing on the first. That is why this
/// is a predicate a caller consults rather than a constructor that refuses.
/// </para>
/// </summary>
public static class ContentKeyShape
{
    /// <summary>The longest legal content key, contracts 5.3, which matches the consumers' own key columns.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>
    /// The whole rule as one sentence, for a refusal message to append after the defect it names. It lives
    /// here so a fourth caller does not become a fourth wording of the same rule.
    /// </summary>
    public static string Rule { get; } = FormattableString.Invariant(
        $"A key is 1 to {MaxKeyLength} characters of a-z, 0-9 and underscore, with no leading digit, no leading or trailing underscore and no double underscore.");

    /// <summary>
    /// The FIRST defect in <paramref name="key"/> as a phrase that completes "which is ...", or null when the
    /// key is well formed.
    /// </summary>
    /// <param name="key">The candidate key.</param>
    /// <returns>The defect phrase, or null when the key is legal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static string? Defect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length == 0)
        {
            return "empty";
        }

        if (key.Length > MaxKeyLength)
        {
            return FormattableString.Invariant($"{key.Length} characters long");
        }

        for (int i = 0; i < key.Length; i++)
        {
            char character = key[i];
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return FormattableString.Invariant($"outside the character set at position {i}");
            }

            if (character == '_' && i > 0 && key[i - 1] == '_')
            {
                return FormattableString.Invariant($"a double underscore at position {i}");
            }
        }

        if (key[0] is >= '0' and <= '9')
        {
            return "a leading digit";
        }

        return key[0] == '_' ? "a leading underscore" : key[^1] == '_' ? "a trailing underscore" : null;
    }
}
