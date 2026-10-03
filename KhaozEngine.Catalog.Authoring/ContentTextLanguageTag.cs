using System;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The ONE grammar an authored language identity is checked and normalized under: ASCII letters, digits and
/// hyphens, 1 to 35 bytes, no empty segment, and a first segment that starts with a letter. Letters are
/// lowered invariantly, so <c>EN-us</c> and <c>en-US</c> are one identity, <c>en-us</c>.
/// <para>
/// <b>It is not BCP-47 registry validation and it consults no installed culture.</b> A server whose operating
/// system lacks a culture must still author it, and a private tag such as <c>qz-123</c> is legal. Underscores
/// and non-ASCII aliases are refused rather than converted, because a silent conversion would make two
/// spellings one identity without anyone deciding that they are.
/// </para>
/// <para>
/// A published language keeps its historical WIRE spelling (<see cref="ContentTextLanguageDeclaration"/>),
/// which this grammar maps to its canonical identity. New declarations use the canonical spelling.
/// </para>
/// </summary>
public static class ContentTextLanguageTag
{
    /// <summary>The longest legal tag in bytes, which is also its length in characters because it is ASCII.</summary>
    public const int MaxBytes = 35;

    /// <summary>The canonical identity of <paramref name="tag"/>.</summary>
    /// <param name="tag">The tag as submitted or as a manifest spells it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tag"/> breaks the grammar.</exception>
    public static string Normalize(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return TryNormalize(tag, out string? canonical)
            ? canonical
            : throw new ArgumentException(
                FormattableString.Invariant(
                    $"Language tag '{tag}' is not ASCII letters, digits and hyphens of 1 to {MaxBytes} bytes with no empty segment and a first segment starting with a letter."),
                nameof(tag));
    }

    /// <summary>Whether <paramref name="tag"/> is legal, and its canonical identity when it is.</summary>
    /// <param name="tag">The tag to check.</param>
    /// <param name="canonical">The lowered identity, populated only when this returns true.</param>
    public static bool TryNormalize(string? tag, [NotNullWhen(true)] out string? canonical)
    {
        canonical = null;
        if (string.IsNullOrEmpty(tag) || tag.Length > MaxBytes || !char.IsAsciiLetter(tag[0]))
        {
            return false;
        }

        Span<char> lowered = stackalloc char[tag.Length];
        bool segmentOpen = false;
        for (int i = 0; i < tag.Length; i++)
        {
            char current = tag[i];
            if (current == '-')
            {
                if (!segmentOpen)
                {
                    return false;
                }

                segmentOpen = false;
            }
            else if (char.IsAsciiLetter(current) || char.IsAsciiDigit(current))
            {
                segmentOpen = true;
            }
            else
            {
                return false;
            }

            lowered[i] = char.ToLowerInvariant(current);
        }

        if (!segmentOpen)
        {
            return false;
        }

        canonical = new string(lowered);
        return true;
    }
}
