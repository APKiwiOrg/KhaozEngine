using System.Reflection;
using System.Resources;

namespace KhaozEngine.App;

/// <summary>
/// The ambient <see cref="IStringCatalog"/> that <see cref="LocalizedText.Resolve()"/> reads when no catalog
/// is passed explicitly. An app sets this once at startup (there is no global <see cref="ServiceLocator"/>
/// singleton, and threading a catalog through every Gui draw call would be invasive). Null is legal - a
/// localizable <see cref="LocalizedText"/> then renders its key as a visible placeholder, never throwing.
/// Because <see cref="ResourceStringCatalog"/> reads <c>CurrentUICulture</c> live and <see cref="LocalizedText"/>
/// re-resolves on every access, a runtime locale switch shows up on the next draw with nothing to invalidate.
/// </summary>
public static class LocalizationContext
{
    /// <summary>The ambient catalog, or null when unset.</summary>
    public static IStringCatalog? Catalog { get; set; }

    /// <summary>
    /// Installs a <see cref="ResourceStringCatalog"/> over <paramref name="resources"/> as the ambient
    /// <see cref="Catalog"/> and returns it. The one-liner every game used to hand-wire in a bridge class whose
    /// entire body was <c>Catalog = new ResourceStringCatalog(...)</c>. Call once at startup, before any
    /// <see cref="LocalizedText"/> sink draws. Idempotent: a repeat call just replaces the ambient catalog with an
    /// equivalent fresh one. Culture stays live - the catalog reads <c>CurrentUICulture</c> at resolve time, so a
    /// runtime <see cref="LocalizationManager.SetCulture"/> takes effect on the next draw with nothing captured.
    /// </summary>
    /// <param name="resources">The resource manager (satellite <c>.resx</c>) to resolve keys against.</param>
    /// <returns>The catalog that was installed, for a caller (e.g. a test) that also wants a direct reference.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="resources"/> is null.</exception>
    public static ResourceStringCatalog WireResx(ResourceManager resources)
    {
        var catalog = new ResourceStringCatalog(resources);
        Catalog = catalog;
        return catalog;
    }

    /// <summary>
    /// Convenience overload of <see cref="WireResx(ResourceManager)"/> that builds the
    /// <see cref="ResourceManager"/> from a resx base name and its owning assembly, collapsing the
    /// <c>new ResourceManager(baseName, assembly)</c> step some games spelled out by hand.
    /// </summary>
    /// <param name="baseName">The root name of the resources, e.g. <c>"MyGame.Localization.Resources"</c>.</param>
    /// <param name="assembly">The assembly the satellite resources are embedded in.</param>
    /// <returns>The catalog that was installed.</returns>
    public static ResourceStringCatalog WireResx(string baseName, Assembly assembly)
        => WireResx(new ResourceManager(baseName, assembly));
}
