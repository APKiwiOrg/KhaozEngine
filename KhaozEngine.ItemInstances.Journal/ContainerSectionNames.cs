using System;
using System.Globalization;
using KhaozEngine.ItemInstances;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// Spec 5.2's projection section names for a paged container: <c>&lt;container&gt;/p&lt;NN&gt;</c>, zero
/// padded to two digits, with three or more digits used unpadded above page 99. <c>bank/p00</c> through
/// <c>bank/p09</c> for a 1,000 slot bank, <c>bag/p00</c> for a 30 slot bag, <c>worn/p00</c> for an 11 slot
/// worn set.
/// <para>
/// <b>Nothing needs escaping.</b> A journal section name is an identity capped at
/// <see cref="JournalLimits.EngineMaximumIdentityCharacters"/> characters over <c>[A-Za-z0-9._:/-]</c>
/// (<c>JournalProjectionWrite.cs:13</c>, <c>JournalLimits.cs:86-98</c>), and the slash is in that set.
/// <see cref="Format"/> refuses BOTH halves of that rule, a container name outside the character set and a
/// formatted name over the cap, so a name this type writes is always a name the journal accepts. The bound
/// is on the name this WRITES rather than on the container, because the suffix is three characters at page 0
/// and five at page 100, and bounding the input would leave a container that formats at page 99 and throws
/// at page 100. Without it the throw arrives from
/// <c>JournalValidation.Identity</c> at the far end of a commit, with the batch already closed and its pages
/// already dirty.
/// </para>
/// <para>
/// <b><see cref="TryParse"/> is the ONE place a name is taken apart</b>, and it is what spec 13 row 10 uses
/// to check a page against the section it arrived in. It is CANONICAL rather than tolerant: it accepts
/// exactly what <see cref="Format"/> writes, so <c>bank/p007</c> is refused rather than read as page 7. A
/// tolerant parse would let two readers disagree about which section a page belongs in, which is the
/// failure row 10 exists to catch.
/// </para>
/// <para>
/// <b>It answers false rather than throwing</b>, because a section name arrives from a store. Its sibling
/// <see cref="Format"/> throws, because a container name and a page index arrive from code.
/// </para>
/// </summary>
public static class ContainerSectionNames
{
    /// <summary>The character that separates the container from its page number.</summary>
    public const char Separator = '/';

    /// <summary>The character a page number opens with, so a container's own sections are tellable from
    /// anything else a stream files under a slash.</summary>
    public const char PagePrefix = 'p';

    /// <summary>
    /// The section name one page of one container is filed under.
    /// </summary>
    /// <param name="container">The container's own name, <c>bank</c>, <c>bag</c> or <c>worn</c> in spec
    /// 5.2's examples. It carries no slash of its own: one level keeps the parse unambiguous.</param>
    /// <param name="pageIndex">Which page of the container this is, from 0 through
    /// <see cref="ItemContainerPage.MaxPageIndex"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="container"/> is empty, carries a character
    /// outside <c>[A-Za-z0-9._:-]</c>, which is the journal's identity set less the separator, or is long
    /// enough that the name this writes would pass
    /// <see cref="JournalLimits.EngineMaximumIdentityCharacters"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative or above
    /// <see cref="ItemContainerPage.MaxPageIndex"/>, which is the largest page a stored header can
    /// name.</exception>
    public static string Format(string container, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageIndex, ItemContainerPage.MaxPageIndex);
        if (container.Length == 0 || !IsContainerName(container))
        {
            throw new ArgumentException(
                "A container name is one or more of [A-Za-z0-9._:-], which is the journal's identity set less the separator this scheme spends.",
                nameof(container));
        }

        // "D2" IS the padding rule: it pads to two digits and leaves a wider number alone, so page 9 is p09
        // and page 563 is p563 with no branch of its own.
        Span<char> prefix = stackalloc char[] { Separator, PagePrefix };
        string name = string.Concat(container, prefix, pageIndex.ToString("D2", CultureInfo.InvariantCulture));
        if (name.Length > JournalLimits.EngineMaximumIdentityCharacters)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Container '{container}' is {container.Length} characters, and its page {pageIndex} section name would be {name.Length}, over the journal's {JournalLimits.EngineMaximumIdentityCharacters} character identity cap."),
                nameof(container));
        }

        return name;
    }

    /// <summary>
    /// Takes a stored section name apart, answering false for anything this scheme did not write. That
    /// covers every other section a stream files (<c>profile</c>, <c>skills</c>, <c>quests</c>), so a load
    /// path walks a whole stream's sections and keeps the ones that are its own.
    /// </summary>
    /// <param name="sectionName">The name as the store holds it.</param>
    /// <param name="container">The container name, null when the answer is false.</param>
    /// <param name="pageIndex">The page index, 0 when the answer is false.</param>
    public static bool TryParse(string? sectionName, out string? container, out int pageIndex)
    {
        container = null;
        pageIndex = 0;
        if (sectionName is null) return false;

        int separator = sectionName.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= sectionName.Length) return false;
        if (sectionName[separator + 1] != PagePrefix) return false;

        ReadOnlySpan<char> name = sectionName.AsSpan(0, separator);
        ReadOnlySpan<char> digits = sectionName.AsSpan(separator + 2);
        if (!IsContainerName(name) || !IsCanonicalPageNumber(digits)) return false;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)) return false;
        if (parsed > ItemContainerPage.MaxPageIndex) return false;

        container = sectionName[..separator];
        pageIndex = parsed;
        return true;
    }

    /// <summary>Whether a stored name is one of this container's pages, which is the question a load path
    /// asks of every section on a stream.</summary>
    /// <param name="sectionName">The name as the store holds it.</param>
    /// <param name="container">The container being loaded.</param>
    /// <param name="pageIndex">The page index, 0 when the answer is false.</param>
    public static bool IsPageOf(string? sectionName, string container, out int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (TryParse(sectionName, out string? parsed, out pageIndex)
            && string.Equals(parsed, container, StringComparison.Ordinal))
        {
            return true;
        }

        pageIndex = 0;
        return false;
    }

    /// <summary>The journal's identity set less the separator, which is what keeps one level of name.</summary>
    static bool IsContainerName(ReadOnlySpan<char> container)
    {
        foreach (char c in container)
        {
            bool allowed = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c is '.' or '_' or ':' or '-';
            if (!allowed) return false;
        }

        return container.Length > 0;
    }

    /// <summary>
    /// The padding rule, read back: exactly two digits below 100, and no leading zero above it. This is
    /// what makes the parse canonical, and it is cheaper than formatting the name again to compare.
    /// </summary>
    static bool IsCanonicalPageNumber(ReadOnlySpan<char> digits)
    {
        if (digits.Length < 2) return false;
        foreach (char c in digits)
        {
            if (c is < '0' or > '9') return false;
        }

        return digits.Length == 2 || digits[0] != '0';
    }
}
