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
    /// </summary>
    /// <param name="key">The lookup key whose value is the format template.</param>
    /// <param name="args">The format arguments.</param>
    /// <returns>The formatted string.</returns>
    string Format(string key, params object?[] args);

    /// <summary>
    /// Non-throwing probe. Returns true and the localized value when <paramref name="key"/> is present;
    /// returns false with <paramref name="value"/> set to <paramref name="key"/> when it is absent.
    /// </summary>
    /// <param name="key">The lookup key.</param>
    /// <param name="value">The localized value when present, otherwise <paramref name="key"/>.</param>
    /// <returns>True when the key resolved to a value, false when it was absent.</returns>
    bool TryGet(string key, out string value);
}
