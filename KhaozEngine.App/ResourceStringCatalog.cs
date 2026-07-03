using System;
using System.Globalization;
using System.Resources;

namespace KhaozEngine.App;

/// <summary>
/// An <see cref="IStringCatalog"/> backed by a standard-library <see cref="ResourceManager"/> (satellite
/// <c>.resx</c> resources). A missing key returns the key itself; lookups read
/// <see cref="CultureInfo.CurrentUICulture"/> live, so <see cref="LocalizationManager.SetCulture"/> takes effect
/// without re-creating the catalog.
/// </summary>
public sealed class ResourceStringCatalog : IStringCatalog
{
    private readonly ResourceManager _resources;

    /// <summary>
    /// Creates a catalog over the given resource manager (typically the same one a
    /// <see cref="LocalizationManager"/> was constructed with - see <see cref="LocalizationManager.Catalog"/>).
    /// </summary>
    /// <param name="resources">The resource manager to resolve keys against.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resources"/> is null.</exception>
    public ResourceStringCatalog(ResourceManager resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    /// <inheritdoc />
    public string Get(string key)
        => _resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <inheritdoc />
    public string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), args);

    /// <inheritdoc />
    public bool TryGet(string key, out string value)
    {
        value = Get(key);
        // Get returns the key itself when absent, so a resolved value differs from the key.
        return !string.Equals(value, key, StringComparison.Ordinal);
    }
}
