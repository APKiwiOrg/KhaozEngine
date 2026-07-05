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
}
