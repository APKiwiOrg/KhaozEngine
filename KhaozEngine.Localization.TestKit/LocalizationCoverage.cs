using System;
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
/// several games hand-rolled: reflect over a keys class, assert every key resolves in the neutral resx AND in each
/// shipped satellite (with parent fallback OFF, so a missing translation fails instead of silently reading the
/// neutral language), and check placeholder-index integrity between the neutral template and each translation.
/// </summary>
/// <remarks>
/// Keys are read off a plain constants class: every <c>public const string</c> field's value, plus every
/// <c>public static readonly</c> <see cref="StringId"/> field's <see cref="StringId.Key"/>. Add a key on either
/// side and the guard covers it the moment it exists. Static and framework-agnostic - call it from an xUnit
/// <c>[Fact]</c>; a gap throws <see cref="LocalizationCoverageException"/>, which fails the test.
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

        var problems = new List<string>();

        // Neutral (invariant) resx: every key must resolve.
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
            sb.Append("Localization coverage failed for ").Append(keysType.Name)
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
