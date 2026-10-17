using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The engine-owned localization keys for the bug report screen and its toasts, plus the built-in English fallback
/// (<see cref="EnglishDefaults"/>). A game localizes any line by adding its <c>bugreport.*</c> key to its own
/// catalog and wiring it as <see cref="LocalizationContext.Catalog"/>. <see cref="Resolve"/> and
/// <see cref="Format"/> fall back to the English here whenever a key is absent or no catalog is wired, mirroring
/// <c>DiagnosticsOverlayStrings</c>. Category labels resolve <c>bugreport.category.&lt;id lowercased&gt;</c> the same
/// way and fall back to the raw id, so a game's own category list localizes too.
/// </summary>
public static class BugReportStrings
{
    /// <summary>The screen title. No arguments.</summary>
    public static readonly StringId Title = new("bugreport.screen.title");

    /// <summary>The label above the description box. No arguments.</summary>
    public static readonly StringId DescriptionLabel = new("bugreport.screen.description.label");

    /// <summary>The empty description box's placeholder. No arguments.</summary>
    public static readonly StringId DescriptionPlaceholder = new("bugreport.screen.description.placeholder");

    /// <summary>The description counter. {0} is the current length, {1} the maximum.</summary>
    public static readonly StringId DescriptionCounter = new("bugreport.screen.description.counter");

    /// <summary>
    /// The label beside the category selector. No arguments. Named apart from <see cref="CategoryLabel(string)"/>,
    /// because a field and a method cannot share a name.
    /// </summary>
    public static readonly StringId CategoryFieldLabel = new("bugreport.screen.category.label");

    /// <summary>The screenshot toggle's label. No arguments.</summary>
    public static readonly StringId ScreenshotToggle = new("bugreport.screen.screenshot.toggle");

    /// <summary>The clip toggle's label. No arguments.</summary>
    public static readonly StringId ClipToggle = new("bugreport.screen.clip.toggle");

    /// <summary>The line shown under a disabled clip toggle. No arguments.</summary>
    public static readonly StringId ClipUnavailable = new("bugreport.screen.clip.unavailable");

    /// <summary>The line saying what the report carries besides the player's input. No arguments.</summary>
    public static readonly StringId Included = new("bugreport.screen.included");

    /// <summary>The line shown while the player is not signed in. No arguments.</summary>
    public static readonly StringId SignedOut = new("bugreport.screen.signed-out");

    /// <summary>The Send button. No arguments.</summary>
    public static readonly StringId Send = new("bugreport.screen.send");

    /// <summary>The Cancel button. No arguments.</summary>
    public static readonly StringId Cancel = new("bugreport.screen.cancel");

    /// <summary>The placeholder drawn where the thumbnails go until they arrive. No arguments.</summary>
    public static readonly StringId Capturing = new("bugreport.screen.capturing");

    /// <summary>The toast for a delivered report. {0} is the server's reference.</summary>
    public static readonly StringId ToastSent = new("bugreport.toast.sent");

    /// <summary>The toast for a report kept for the next sign in. No arguments.</summary>
    public static readonly StringId ToastQueued = new("bugreport.toast.queued");

    /// <summary>The toast for a report kept because the player is signed out. No arguments.</summary>
    public static readonly StringId ToastQueuedSignedOut = new("bugreport.toast.queued-signed-out");

    /// <summary>The toast for a report the server refused. {0} is the error code.</summary>
    public static readonly StringId ToastRejected = new("bugreport.toast.rejected");

    /// <summary>The toast for a report that could not be built. No arguments.</summary>
    public static readonly StringId ToastFailed = new("bugreport.toast.failed");

    /// <summary>The toast after saved reports were delivered. {0} is how many.</summary>
    public static readonly StringId ToastFlushed = new("bugreport.toast.flushed");

    const string CategoryPrefix = "bugreport.category.";

    /// <summary>
    /// Resolves <paramref name="id"/> through the ambient <see cref="LocalizationContext.Catalog"/> when it is wired
    /// and carries the key, otherwise against <see cref="EnglishDefaults"/>. Never throws.
    /// </summary>
    public static string Resolve(StringId id) => CatalogFor(id.Key).Get(id.Key);

    /// <summary>
    /// Formats <paramref name="id"/>'s template with <paramref name="args"/>. A wired catalog formats with its own
    /// culture, the English fallback with the invariant culture. A template the arguments do not fit comes back
    /// unformatted rather than throwing.
    /// </summary>
    public static string Format(StringId id, params object?[] args) => CatalogFor(id.Key).Format(id.Key, args);

    /// <summary>
    /// The player-facing label for a category id: <c>bugreport.category.&lt;id lowercased&gt;</c> through the wired
    /// catalog, then the English defaults, then <paramref name="categoryId"/> itself.
    /// </summary>
    public static string CategoryLabel(string categoryId)
    {
        ArgumentNullException.ThrowIfNull(categoryId);
        string key = CategoryPrefix + categoryId.ToLowerInvariant();
        if (LocalizationContext.Catalog is { } wired && wired.TryGet(key, out string value)) return value;
        return EnglishDefaults.TryGet(key, out value) ? value : categoryId;
    }

    /// <summary>
    /// The built-in English strings, keyed by the ids above and the six default category keys. This is the exact
    /// text <see cref="Resolve"/> returns when no wired catalog resolves a key.
    /// </summary>
    public static IStringCatalog EnglishDefaults { get; } = new EnglishDefaultCatalog();

    static IStringCatalog CatalogFor(string key) =>
        LocalizationContext.Catalog is { } wired && wired.TryGet(key, out _) ? wired : EnglishDefaults;

    sealed class EnglishDefaultCatalog : IStringCatalog
    {
        static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bugreport.screen.title"] = "Report a bug",
            ["bugreport.screen.description.label"] = "What happened?",
            ["bugreport.screen.description.placeholder"] = "Describe what you saw and what you expected to happen.",
            ["bugreport.screen.description.counter"] = "{0}/{1}",
            ["bugreport.screen.category.label"] = "Category",
            ["bugreport.screen.screenshot.toggle"] = "Include screenshot",
            ["bugreport.screen.clip.toggle"] = "Include the last few seconds of video",
            ["bugreport.screen.clip.unavailable"] = "Video is not available on this device",
            ["bugreport.screen.included"] = "Your account and your game and system info are included.",
            ["bugreport.screen.signed-out"] = "You are not signed in. This report will be sent after you sign in.",
            ["bugreport.screen.send"] = "Send",
            ["bugreport.screen.cancel"] = "Cancel",
            ["bugreport.screen.capturing"] = "Capturing...",
            ["bugreport.toast.sent"] = "Bug report sent. Reference {0}",
            ["bugreport.toast.queued"] = "Bug report saved. It will be sent the next time you sign in.",
            ["bugreport.toast.queued-signed-out"] = "Bug report saved. It will be sent after you sign in.",
            ["bugreport.toast.rejected"] = "Bug report not accepted ({0}).",
            ["bugreport.toast.failed"] = "The bug report could not be prepared.",
            ["bugreport.toast.flushed"] = "{0} saved bug reports sent.",
            ["bugreport.category.gameplay"] = "Gameplay",
            ["bugreport.category.visual"] = "Visual",
            ["bugreport.category.audio"] = "Audio",
            ["bugreport.category.performance"] = "Performance",
            ["bugreport.category.interface"] = "Interface",
            ["bugreport.category.other"] = "Other",
        };

        public string Get(string key) => Map.TryGetValue(key, out string? v) ? v : key;

        public string Format(string key, params object?[] args)
            => IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, Get(key), args);

        public bool TryGet(string key, out string value)
        {
            if (Map.TryGetValue(key, out string? v))
            {
                value = v;
                return true;
            }
            value = key;
            return false;
        }
    }
}
