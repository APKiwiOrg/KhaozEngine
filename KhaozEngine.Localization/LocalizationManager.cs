using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;

namespace KhaozEngine.Localization;

/// <summary>
/// Manages localization settings for a game: retrieving the cultures backed by satellite
/// resources, and setting the current thread culture.
/// </summary>
public class LocalizationManager
{
    /// <summary>
    /// The culture code the game defaults to.
    /// </summary>
    public const string DEFAULT_CULTURE_CODE = "en-US";

    /// <summary>
    /// Sets the current thread's culture and UI culture from a culture code (e.g. "en-US").
    /// </summary>
    /// <param name="cultureCode">A non-empty culture code.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cultureCode"/> is null or empty.</exception>
    public static void SetCulture(string cultureCode)
    {
        if (string.IsNullOrEmpty(cultureCode))
            throw new ArgumentNullException(nameof(cultureCode), "A culture code must be provided.");

        CultureInfo culture = new CultureInfo(cultureCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }
}
