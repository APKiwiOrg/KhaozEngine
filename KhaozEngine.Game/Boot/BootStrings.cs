using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;

namespace KhaozEngine.Game
{
    /// <summary>
    /// The engine-owned localization keys for the boot screen (step labels, status lines, error messages, button
    /// captions), plus a built-in English fallback (<see cref="EnglishDefaults"/>). A game localizes the boot screen
    /// by adding these <c>boot.*</c> keys to its own catalog and wiring it as
    /// <see cref="LocalizationContext.Catalog"/>. The screen then resolves through it and falls back to the English
    /// values here whenever a key is absent or no catalog is wired. Mirrors <c>UpdateOverlayStrings</c> /
    /// <c>PatchNotesStrings</c>. All boot copy is a <see cref="StringId"/> so it never reaches a sink as a bare
    /// literal (the KELOC analyzer would flag that).
    /// </summary>
    public static class BootStrings
    {
        /// <summary>Default title shown above the bar. No arguments.</summary>
        public static readonly StringId Title = new("boot.title");

        /// <summary>Label for the built-in update-check step (<see cref="UpdateBootStep"/>). No arguments.</summary>
        public static readonly StringId StepUpdate = new("boot.step.update");

        /// <summary>Label for the built-in server-status step (<see cref="ServerStatusBootStep"/>). No arguments.</summary>
        public static readonly StringId StepServerStatus = new("boot.step.serverStatus");

        /// <summary>Generic "loading" label a game can reuse for an asset-warm-up step. No arguments.</summary>
        public static readonly StringId StepLoading = new("boot.step.loading");

        /// <summary>Status line shown while the app hands off to a relaunch to apply an update. No arguments.</summary>
        public static readonly StringId Restarting = new("boot.status.restarting");

        /// <summary>Heading for the failure state. No arguments.</summary>
        public static readonly StringId ErrorTitle = new("boot.error.title");

        /// <summary>Failure message for the server-status min-version gate (client below the required version). No arguments.</summary>
        public static readonly StringId ErrorUpdateRequired = new("boot.error.updateRequired");

        /// <summary>Failure message when the server is unavailable (down / restarting) and configured to block. No arguments.</summary>
        public static readonly StringId ErrorServerUnavailable = new("boot.error.serverUnavailable");

        /// <summary>Failure message for an unexpected step error (the raw exception is logged, never shown). No arguments.</summary>
        public static readonly StringId ErrorGeneric = new("boot.error.generic");

        /// <summary>Caption for the retry button. No arguments.</summary>
        public static readonly StringId Retry = new("boot.button.retry");

        /// <summary>Caption for the quit button. No arguments.</summary>
        public static readonly StringId Quit = new("boot.button.quit");

        /// <summary>
        /// Resolves a boot <see cref="StringId"/> to display text: the ambient
        /// <see cref="LocalizationContext.Catalog"/> when it is wired AND carries the key, otherwise the built-in
        /// English in <see cref="EnglishDefaults"/>. Never throws for a missing key.
        /// </summary>
        public static string Resolve(StringId id) => FallbackCatalog.Get(id.Key);

        /// <summary>
        /// A catalog that resolves through the ambient <see cref="LocalizationContext.Catalog"/> when it is wired AND
        /// carries the key, else the built-in English (<see cref="EnglishDefaults"/>). Read live, so a runtime locale
        /// switch shows on the next draw. Resolve a boot <see cref="LocalizedText"/> against THIS
        /// (<c>text.Resolve(BootStrings.FallbackCatalog)</c>) rather than <c>text.Resolve()</c> so an engine
        /// <c>boot.*</c> key still shows English when a game wires no catalog (a raw <see cref="LocalizedText.Raw"/>
        /// value returns verbatim regardless, and a game's own key resolves through its wired catalog).
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

        /// <summary>The built-in English default text, keyed by the ids above. What the screen renders when no wired
        /// catalog resolves a key. Formats with <see cref="CultureInfo.InvariantCulture"/>.</summary>
        public static IStringCatalog EnglishDefaults { get; } = new EnglishDefaultCatalog();

        sealed class EnglishDefaultCatalog : IStringCatalog
        {
            internal static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["boot.title"] = "Starting",
                ["boot.step.update"] = "Checking for updates",
                ["boot.step.serverStatus"] = "Contacting server",
                ["boot.step.loading"] = "Loading",
                ["boot.status.restarting"] = "Restarting to apply update",
                ["boot.error.title"] = "Startup failed",
                ["boot.error.updateRequired"] = "A required update is available. Update to continue.",
                ["boot.error.serverUnavailable"] = "The server is unavailable. Try again later.",
                ["boot.error.generic"] = "Something went wrong while starting up.",
                ["boot.button.retry"] = "Retry",
                ["boot.button.quit"] = "Quit",
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
}
