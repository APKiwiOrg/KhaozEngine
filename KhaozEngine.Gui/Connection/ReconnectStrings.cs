using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>
/// The engine-owned localization keys for <see cref="ReconnectScreen"/> (title, planned-update title,
/// reassurance line, attempt line, retry line), plus the built-in English fallback
/// (<see cref="EnglishDefaults"/>). A game localizes the screen by adding these <c>reconnect.*</c> keys to its
/// own catalog and wiring it as <see cref="LocalizationContext.Catalog"/>. <see cref="ReconnectScreenTheme"/>'s
/// defaults then resolve through it and fall back to the English values here whenever a key is absent or no
/// catalog is wired. Mirrors <c>BootStrings</c> / <c>UpdateOverlayStrings</c>.
/// </summary>
/// <remarks>
/// Format arguments per key (mirrored by <see cref="EnglishDefaults"/>): <see cref="AttemptLine"/> takes
/// <c>{0}</c> = the current attempt number. <see cref="RetryLine"/> takes <c>{0}</c> = whole seconds until the
/// next retry. The other keys take no arguments.
/// </remarks>
public static class ReconnectStrings
{
    /// <summary>Title shown while reconnecting after an unplanned drop. No arguments.</summary>
    public static readonly StringId Title = new("reconnect.title");

    /// <summary>Title shown during a planned server update/maintenance window. No arguments.</summary>
    public static readonly StringId PlannedTitle = new("reconnect.planned.title");

    /// <summary>Reassurance sub-line drawn under the title. No arguments.</summary>
    public static readonly StringId Reassurance = new("reconnect.reassurance");

    /// <summary>The reconnect-attempt counter line. Takes <c>{0}</c> = the attempt number.</summary>
    public static readonly StringId AttemptLine = new("reconnect.attempt");

    /// <summary>The retry-countdown line. Takes <c>{0}</c> = whole seconds until the next retry.</summary>
    public static readonly StringId RetryLine = new("reconnect.retry");

    /// <summary>
    /// Resolves a reconnect <see cref="StringId"/> to display text: the ambient
    /// <see cref="LocalizationContext.Catalog"/> when it is wired AND carries the key, otherwise the built-in
    /// English in <see cref="EnglishDefaults"/>. Never throws for a missing key.
    /// </summary>
    public static string Resolve(StringId id) => FallbackCatalog.Get(id.Key);

    /// <summary>
    /// A catalog that resolves through the ambient <see cref="LocalizationContext.Catalog"/> when it is wired AND
    /// carries the key, else the built-in English (<see cref="EnglishDefaults"/>). Read live, so a runtime locale
    /// switch shows on the next draw. Resolve a reconnect <see cref="LocalizedText"/> against THIS
    /// (<c>text.Resolve(ReconnectStrings.FallbackCatalog)</c>) rather than <c>text.Resolve()</c> so an engine
    /// <c>reconnect.*</c> key still shows English when a game wires no catalog (a raw
    /// <see cref="LocalizedText.Raw"/> value returns verbatim regardless, and a game's own key resolves through
    /// its wired catalog).
    /// </summary>
    public static IStringCatalog FallbackCatalog { get; } = new FallbackStringCatalog();

    sealed class FallbackStringCatalog : IStringCatalog
    {
        public string Get(string key)
            => LocalizationContext.Catalog is { } c && c.TryGet(key, out string? v) ? v : EnglishDefaults.Get(key);

        public string Format(string key, params object?[] args)
            => LocalizationContext.Catalog is { } c && c.TryGet(key, out _)
                ? c.Format(key, args)
                : EnglishDefaults.Format(key, args);

        public bool TryGet(string key, out string value)
            => (LocalizationContext.Catalog is { } c && c.TryGet(key, out value)) || EnglishDefaults.TryGet(key, out value);
    }

    /// <summary>The built-in English default text, keyed by the ids above. What <see cref="ReconnectScreen"/>
    /// renders when no wired catalog resolves a key. Formats with <see cref="CultureInfo.InvariantCulture"/>.</summary>
    public static IStringCatalog EnglishDefaults { get; } = new EnglishDefaultCatalog();

    sealed class EnglishDefaultCatalog : IStringCatalog
    {
        internal static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reconnect.title"] = "Connection lost",
            ["reconnect.planned.title"] = "Server is updating",
            ["reconnect.reassurance"] = "You'll reconnect automatically.",
            ["reconnect.attempt"] = "Attempt {0}",
            ["reconnect.retry"] = "Retrying in {0}s",
        };

        public string Get(string key) => Map.TryGetValue(key, out string? v) ? v : key;

        public string Format(string key, params object?[] args)
            => string.Format(CultureInfo.InvariantCulture, Get(key), args);

        public bool TryGet(string key, out string value)
        {
            if (Map.TryGetValue(key, out string? v)) { value = v; return true; }
            value = key;
            return false;
        }
    }
}
