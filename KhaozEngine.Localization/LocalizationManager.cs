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
    public const string DefaultCultureCode = "en-US";

    private readonly ResourceManager _resourceManager;

    /// <summary>
    /// Creates a manager that discovers supported cultures from the given resource manager.
    /// The resource manager must be built against the assembly that owns the satellite
    /// resources (typically the game's own assembly).
    /// </summary>
    /// <param name="resourceManager">The resource manager to probe for localized resource sets.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resourceManager"/> is null.</exception>
    public LocalizationManager(ResourceManager resourceManager)
    {
        _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
    }

    /// <summary>
    /// Sets the current thread's culture and UI culture from a culture code (e.g. "en-US").
    /// </summary>
    /// <param name="cultureCode">A non-empty culture code.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cultureCode"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cultureCode"/> is empty.</exception>
    public static void SetCulture(string cultureCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(cultureCode);

        CultureInfo culture = new CultureInfo(cultureCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    /// <summary>
    /// Retrieves the specific cultures that have a localized resource set in the injected
    /// resource manager, always including <see cref="CultureInfo.InvariantCulture"/> (the base,
    /// non-localized resources).
    /// </summary>
    /// <returns>The list of supported cultures.</returns>
    public List<CultureInfo> GetSupportedCultures()
    {
        List<CultureInfo> supportedCultures = new List<CultureInfo>();

        CultureInfo[] cultures = CultureInfo.GetCultures(CultureTypes.SpecificCultures);

        foreach (CultureInfo culture in cultures)
        {
            try
            {
                ResourceSet? resourceSet = _resourceManager.GetResourceSet(culture, true, false);
                if (resourceSet != null)
                {
                    supportedCultures.Add(culture);
                }
            }
            catch (MissingManifestResourceException)
            {
                // No .resx for this culture; skip it.
            }
        }

        // Always add the default (invariant) culture - the base .resx file.
        supportedCultures.Add(CultureInfo.InvariantCulture);

        return supportedCultures;
    }
}
