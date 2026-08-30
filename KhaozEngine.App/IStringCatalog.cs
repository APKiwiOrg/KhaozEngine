namespace KhaozEngine.App;

/// <summary>
/// Resolves UI strings by key against the current UI culture (<see cref="System.Globalization.CultureInfo.CurrentUICulture"/>,
/// which <see cref="LocalizationManager.SetCulture"/> drives). A thin, game-agnostic lookup contract over a
/// string source, kept separate from <see cref="LocalizationManager"/> (which owns culture discovery + switching)
/// so the two compose rather than merging into one facade.
/// </summary>
public interface IStringCatalog
{
    /// <summary>
    /// Resolves <paramref name="key"/> against <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>.
    /// Never throws for a missing key: an absent key returns the key itself (a visible, non-fatal placeholder).
    /// </summary>
    /// <param name="key">The lookup key.</param>
    /// <returns>The localized value, or <paramref name="key"/> when it is absent.</returns>
    string Get(string key);

    /// <summary>
    /// Culture-aware <see cref="string.Format(System.IFormatProvider, string, object?[])"/> of the resolved
    /// template: <c>string.Format(CurrentUICulture, Get(key), args)</c>. An absent key formats the key itself.
    /// Never throws on a malformed template either: it falls back to the unformatted template. Implement it
    /// through <see cref="SafeFormat"/>, which is where that guarantee lives.
    /// </summary>
    /// <param name="key">The lookup key whose value is the format template.</param>
    /// <param name="args">The format arguments.</param>
    /// <returns>The formatted string, or the unformatted template when the arguments cannot be applied to it.</returns>
    string Format(string key, params object?[] args);

    /// <summary>
    /// The never-throwing <see cref="string.Format(System.IFormatProvider, string, object?[])"/> that every
    /// <see cref="Format"/> implementation routes through, and the reason <see cref="Format"/> can promise not
    /// to throw.
    /// <para>A malformed template is translator-authored CONTENT arriving as data, not a caller bug. A
    /// translation carrying <c>{1}</c> where the call site passes one argument throws
    /// <see cref="System.FormatException"/> the instant that text is drawn, and Gui draw code resolves
    /// <see cref="LocalizedText"/> inside the frame loop with nothing above it to catch, so one bad line in one
    /// language used to end the process. Falling back to the unformatted <paramref name="template"/> leaves the
    /// text visibly wrong, which is what a content defect should look like, without taking the game down.</para>
    /// <para>This is a backstop, not the check. Catch the defect ahead of time with the placeholder-integrity
    /// pass in <c>LocalizationCoverage.AssertComplete</c> (KhaozEngine.Localization.TestKit), which fails a
    /// build when a translated value's placeholders diverge from the neutral template.</para>
    /// </summary>
    /// <param name="culture">The format provider, each implementation's own: the current UI culture for a
    /// translated catalog, invariant for a built-in English default set.</param>
    /// <param name="template">The already-resolved format template (typically <c>Get(key)</c>).</param>
    /// <param name="args">The format arguments. Null is treated as none.</param>
    /// <returns>The formatted string, or <paramref name="template"/> verbatim when formatting it throws.</returns>
    static string SafeFormat(System.IFormatProvider culture, string template, object?[]? args)
    {
        try
        {
            return string.Format(culture, template, args ?? System.Array.Empty<object?>());
        }
        catch (System.FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// Non-throwing probe. Returns true and the localized value when <paramref name="key"/> is present;
    /// returns false with <paramref name="value"/> set to <paramref name="key"/> when it is absent.
    /// </summary>
    /// <param name="key">The lookup key.</param>
    /// <param name="value">The localized value when present, otherwise <paramref name="key"/>.</param>
    /// <returns>True when the key resolved to a value, false when it was absent.</returns>
    bool TryGet(string key, out string value);
}
