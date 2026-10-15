using System;
using System.Reflection;
using System.Resources;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.App;

/// <summary>
/// The ambient <see cref="IStringCatalog"/> that <see cref="LocalizedText.Resolve()"/> reads when no catalog
/// is passed explicitly. Reads select the innermost <see cref="Use"/> scope, then the current process default.
/// An app sets that default once at startup. Null is legal, including as an explicit scoped override - a
/// localizable <see cref="LocalizedText"/> then renders its key as a visible placeholder, never throwing.
/// Because <see cref="ResourceStringCatalog"/> reads <c>CurrentUICulture</c> live and <see cref="LocalizedText"/>
/// re-resolves on every access, a runtime locale switch shows up on the next draw with nothing to invalidate.
/// </summary>
public static class LocalizationContext
{
    private static readonly AmbientScope<IStringCatalog?> scopes = new();
    private static IStringCatalog? catalog;

    /// <summary>
    /// The current async-flow catalog, falling back to the process default. Assignment always changes the
    /// process default, even inside a scope. Null renders localizable keys as visible placeholders.
    /// </summary>
    public static IStringCatalog? Catalog
    {
        get => scopes.GetValueOrDefault(Volatile.Read(ref catalog));
        set => Volatile.Write(ref catalog, value);
    }

    /// <summary>
    /// Borrows a catalog for this async flow, including across awaits and in child tasks. Null explicitly
    /// overrides the process catalog. Dispose in reverse creation order in each flow. Disposing an active
    /// outer scope throws without changing selection. Repeating disposal after removal is a no-op in that
    /// flow. Inherited child copies dispose independently. The catalog is never disposed by the scope.
    /// </summary>
    public static IDisposable Use(IStringCatalog? catalog) => scopes.Use(catalog);

    /// <summary>
    /// Installs a <see cref="ResourceStringCatalog"/> over <paramref name="resources"/> as the process
    /// <see cref="Catalog"/> default and returns it. Active scopes keep their override. The one-liner every
    /// game used to hand-wire in a bridge class whose
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
