using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using KhaozEngine.App;

namespace KhaozEngine.Localization.TestKit;

/// <summary>
/// A reusable localization coverage guard for a game's test project. Collapses the reflection-based coverage test
/// several games hand-rolled: take a key universe (a keys class, the neutral resx's own entries, or an explicit
/// key sequence), assert every key resolves in the neutral resx AND in each shipped satellite (with parent
/// fallback OFF, so a missing translation fails instead of silently reading the neutral language), and check
/// placeholder-index integrity between the neutral template and each translation.
/// </summary>
/// <remarks>
/// Three ways to supply the key universe, all with identical checking semantics:
/// <list type="bullet">
/// <item><description>A plain constants class (<see cref="AssertComplete(Type, ResourceManager, string[])"/>):
/// every <c>public const string</c> field's value, plus every <c>public static readonly</c> <see cref="StringId"/>
/// field's <see cref="StringId.Key"/>.</description></item>
/// <item><description>The neutral resx itself (<see cref="AssertComplete(ResourceManager, string[])"/>): for games
/// that keep keys directly in the neutral resx with no keys class, every string entry of the neutral resource set
/// is the universe.</description></item>
/// <item><description>An explicit key sequence (<see cref="AssertComplete(IEnumerable{string}, ResourceManager, string[])"/>):
/// for a filtered subset, e.g. <see cref="NeutralKeys"/> minus intentionally untranslated keys.</description></item>
/// </list>
/// Static and framework-agnostic - call it from an xUnit <c>[Fact]</c>; a gap throws
/// <see cref="LocalizationCoverageException"/>, which fails the test.
/// </remarks>
public static class LocalizationCoverage
{
    // Captures the argument index of a composite-format placeholder ({0}, {1:N2}, {2,-8}). Escaped literal
    // braces ({{, }}) are stripped before scanning so "{{0}}" (a literal "{0}") is not counted.
    private static readonly Regex PlaceholderIndex = new(@"\{(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Extracts the localization keys declared on <paramref name="keysType"/>: the value of every
    /// <c>public const string</c> field and the <see cref="StringId.Key"/> of every <c>public static readonly</c>
    /// <see cref="StringId"/> field. Exposed so a game can also drive an xUnit <c>[Theory]</c> off the same source.
    /// </summary>
    /// <param name="keysType">The constants class holding the keys.</param>
    /// <returns>The distinct keys in declaration order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keysType"/> is null.</exception>
    public static IReadOnlyList<string> Keys(Type keysType)
    {
        if (keysType is null) throw new ArgumentNullException(nameof(keysType));

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        foreach (FieldInfo field in keysType.GetFields(flags))
        {
            string? key = null;
            if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            {
                key = field.GetRawConstantValue() as string;
            }
            else if (field.FieldType == typeof(StringId))
            {
                key = ((StringId)field.GetValue(null)!).Key;
            }

            if (!string.IsNullOrEmpty(key) && seen.Add(key)) keys.Add(key);
        }

        return keys;
    }

    /// <summary>
    /// Enumerates the keys of every string entry in the neutral (invariant) resource set of
    /// <paramref name="resources"/>, sorted ordinally. This is the key universe
    /// <see cref="AssertComplete(ResourceManager, string[])"/> checks, exposed so a game can drive an xUnit
    /// <c>[Theory]</c> off the same source or filter it before handing it to
    /// <see cref="AssertComplete(IEnumerable{string}, ResourceManager, string[])"/>.
    /// </summary>
    /// <param name="resources">The game's <see cref="ResourceManager"/> (the same one its catalog resolves against).</param>
    /// <returns>The neutral resx's string-entry keys in ordinal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resources"/> is null.</exception>
    /// <exception cref="LocalizationCoverageException">The neutral resource set cannot be loaded.</exception>
    public static IReadOnlyList<string> NeutralKeys(ResourceManager resources)
    {
        if (resources is null) throw new ArgumentNullException(nameof(resources));

        ResourceSet? neutral = SetFor(resources, CultureInfo.InvariantCulture);
        if (neutral is null)
        {
            throw new LocalizationCoverageException(
                $"neutral resx for {resources.BaseName} could not be loaded (no invariant resource set).");
        }

        var keys = new List<string>();
        foreach (DictionaryEntry entry in neutral)
        {
            if (entry.Key is string key && entry.Value is string) keys.Add(key);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    /// Asserts full localization coverage of <paramref name="keysType"/> against <paramref name="resources"/>:
    /// every key present in the neutral resx, every key present in each of <paramref name="satelliteCultures"/>
    /// (parent fallback OFF), and each translation carrying the same set of placeholder indices as its neutral
    /// template. Throws <see cref="LocalizationCoverageException"/> listing every gap when any check fails.
    /// </summary>
    /// <param name="keysType">The constants class holding the localization keys.</param>
    /// <param name="resources">The game's <see cref="ResourceManager"/> (the same one its catalog resolves against).</param>
    /// <param name="satelliteCultures">The shipped satellite culture names, e.g. <c>"es-ES"</c>, <c>"fr-FR"</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keysType"/> or <paramref name="resources"/> is null.</exception>
    /// <exception cref="LocalizationCoverageException">Any key is missing, or a placeholder set does not match.</exception>
    public static void AssertComplete(Type keysType, ResourceManager resources, params string[] satelliteCultures)
    {
        if (resources is null) throw new ArgumentNullException(nameof(resources));

        IReadOnlyList<string> keys = Keys(keysType);
        if (keys.Count == 0)
        {
            throw new LocalizationCoverageException(
                $"No localization keys found on {keysType.FullName}: expected public const string fields "
                + "or public static readonly StringId fields.");
        }

        AssertCore(keys, keysType.Name, resources, satelliteCultures);
    }

    /// <summary>
    /// Asserts full localization coverage of <paramref name="resources"/> using the neutral resx's own string
    /// entries as the key universe (see <see cref="NeutralKeys"/>). For games that keep localization keys directly
    /// in the neutral resx with no keys class. Every neutral key must resolve in each of
    /// <paramref name="satelliteCultures"/> with parent fallback OFF, and each translation must carry the same set
    /// of placeholder indices as its neutral template. Throws <see cref="LocalizationCoverageException"/> listing
    /// every gap when any check fails.
    /// </summary>
    /// <param name="resources">The game's <see cref="ResourceManager"/> (the same one its catalog resolves against).</param>
    /// <param name="satelliteCultures">The shipped satellite culture names, e.g. <c>"es-ES"</c>, <c>"fr-FR"</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resources"/> is null.</exception>
    /// <exception cref="LocalizationCoverageException">The neutral resx cannot be loaded or has no string entries,
    /// any key is missing from a satellite, or a placeholder set does not match.</exception>
    public static void AssertComplete(ResourceManager resources, params string[] satelliteCultures)
    {
        IReadOnlyList<string> keys = NeutralKeys(resources);
        if (keys.Count == 0)
        {
            throw new LocalizationCoverageException(
                $"neutral resx for {resources.BaseName} contains no string entries.");
        }

        AssertCore(keys, resources.BaseName, resources, satelliteCultures);
    }

    /// <summary>
    /// Asserts full localization coverage of an explicit <paramref name="keys"/> sequence against
    /// <paramref name="resources"/>: every key present in the neutral resx, every key present in each of
    /// <paramref name="satelliteCultures"/> (parent fallback OFF), and each translation carrying the same set of
    /// placeholder indices as its neutral template. Use this to check a filtered universe, e.g.
    /// <see cref="NeutralKeys"/> minus keys that are intentionally untranslated. Throws
    /// <see cref="LocalizationCoverageException"/> listing every gap when any check fails.
    /// </summary>
    /// <param name="keys">The localization keys to check. Duplicates and empty entries are checked once/skipped.</param>
    /// <param name="resources">The game's <see cref="ResourceManager"/> (the same one its catalog resolves against).</param>
    /// <param name="satelliteCultures">The shipped satellite culture names, e.g. <c>"es-ES"</c>, <c>"fr-FR"</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> or <paramref name="resources"/> is null.</exception>
    /// <exception cref="LocalizationCoverageException">No keys are supplied, any key is missing, or a placeholder
    /// set does not match.</exception>
    public static void AssertComplete(IEnumerable<string> keys, ResourceManager resources, params string[] satelliteCultures)
    {
        if (keys is null) throw new ArgumentNullException(nameof(keys));
        if (resources is null) throw new ArgumentNullException(nameof(resources));

        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            if (!string.IsNullOrEmpty(key) && seen.Add(key)) distinct.Add(key);
        }

        if (distinct.Count == 0)
        {
            throw new LocalizationCoverageException("No localization keys supplied to check.");
        }

        AssertCore(distinct, "the supplied keys", resources, satelliteCultures);
    }

    private static void AssertCore(
        IReadOnlyList<string> keys, string sourceName, ResourceManager resources, string[]? satelliteCultures)
    {
        var problems = new List<string>();

        // Neutral (invariant) resx: every key must resolve. (Vacuous when the universe came from the neutral
        // resx itself, but kept uniform across all entry points.)
        ResourceSet? neutral = SetFor(resources, CultureInfo.InvariantCulture);
        if (neutral is null)
        {
            problems.Add("neutral resx could not be loaded (no invariant resource set).");
        }
        else
        {
            var missingNeutral = new List<string>();
            foreach (string key in keys)
                if (neutral.GetString(key) is null) missingNeutral.Add(key);
            if (missingNeutral.Count > 0)
                problems.Add($"neutral resx is missing keys: {string.Join(", ", missingNeutral)}");
        }

        // Each satellite: parent fallback OFF, so a missing entry reads as null here (never the neutral value).
        foreach (string cultureName in satelliteCultures ?? Array.Empty<string>())
        {
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo(cultureName); }
            catch (CultureNotFoundException) { problems.Add($"'{cultureName}' is not a valid culture name."); continue; }

            ResourceSet? set = SetFor(resources, culture);
            if (set is null)
            {
                problems.Add(
                    $"{cultureName}: no resource set (is the {cultureName} satellite resx present and embedded?). "
                    + $"All {keys.Count} keys unresolved.");
                continue;
            }

            var missing = new List<string>();
            var placeholderMismatches = new List<string>();
            foreach (string key in keys)
            {
                string? value = set.GetString(key);
                if (value is null) { missing.Add(key); continue; }

                string? neutralValue = neutral?.GetString(key);
                if (neutralValue is null) continue;
                SortedSet<int> expected = PlaceholderIndices(neutralValue);
                SortedSet<int> actual = PlaceholderIndices(value);
                if (!expected.SetEquals(actual))
                {
                    placeholderMismatches.Add(
                        $"{key} (neutral {{{string.Join(",", expected)}}} vs {cultureName} {{{string.Join(",", actual)}}})");
                }
            }

            if (missing.Count > 0)
                problems.Add($"{cultureName} is missing translations: {string.Join(", ", missing)}");
            if (placeholderMismatches.Count > 0)
                problems.Add($"{cultureName} placeholder mismatch: {string.Join("; ", placeholderMismatches)}");
        }

        if (problems.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("Localization coverage failed for ").Append(sourceName)
              .Append(" (").Append(keys.Count).Append(" keys):").Append('\n');
            foreach (string problem in problems) sb.Append(" - ").Append(problem).Append('\n');
            throw new LocalizationCoverageException(sb.ToString().TrimEnd());
        }
    }

    private static ResourceSet? SetFor(ResourceManager resources, CultureInfo culture)
        => resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);

    private static SortedSet<int> PlaceholderIndices(string template)
    {
        // Drop escaped literal braces first so "{{0}}" is not misread as placeholder {0}.
        string scanned = template.Replace("{{", string.Empty).Replace("}}", string.Empty);
        var indices = new SortedSet<int>();
        foreach (Match match in PlaceholderIndex.Matches(scanned))
            if (int.TryParse(match.Groups[1].Value, out int index)) indices.Add(index);
        return indices;
    }
}
