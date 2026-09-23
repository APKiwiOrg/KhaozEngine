using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>
/// The engine-owned localization keys for the player-facing text <see cref="DiagnosticsHud"/> draws, plus the
/// built-in English fallback (<see cref="EnglishDefaults"/>). A game localizes it by adding these
/// <c>diagnostics.overlay.*</c> keys to its own catalog and wiring it as <see cref="LocalizationContext.Catalog"/>.
/// <see cref="Resolve"/> falls back to the English here whenever a key is absent or no catalog is wired, mirroring
/// <see cref="PatchNotesStrings"/> and <see cref="UpdateOverlayStrings"/>.
/// </summary>
public static class DiagnosticsOverlayStrings
{
    /// <summary>The title of the built-in Build section, which names the running app and its version. No arguments.</summary>
    public static readonly StringId BuildTitle = new("diagnostics.overlay.build.title");

    /// <summary>
    /// Resolves <paramref name="id"/> through the ambient <see cref="LocalizationContext.Catalog"/> when it is
    /// wired AND carries the key, otherwise against <see cref="EnglishDefaults"/>. Never throws and never shows a
    /// raw key for any id declared on this type.
    /// </summary>
    public static string Resolve(StringId id)
    {
        IStringCatalog catalog = LocalizationContext.Catalog is { } c && c.TryGet(id.Key, out _)
            ? c
            : EnglishDefaults;
        return catalog.Get(id.Key);
    }

    /// <summary>
    /// The built-in English default strings, keyed by the ids above. This is the exact text <see cref="Resolve"/>
    /// returns when no wired catalog resolves a key. A game normally provides its own catalog rather than reading
    /// this directly.
    /// </summary>
    public static IStringCatalog EnglishDefaults { get; } = new EnglishDefaultCatalog();

    private sealed class EnglishDefaultCatalog : IStringCatalog
    {
        internal static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["diagnostics.overlay.build.title"] = "Build",
        };

        public string Get(string key) => Map.TryGetValue(key, out string? v) ? v : key;

        public string Format(string key, params object?[] args)
            => IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, Get(key), args);

        public bool TryGet(string key, out string value)
        {
            if (Map.TryGetValue(key, out string? v)) { value = v; return true; }
            value = key;
            return false;
        }
    }
}
