using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>
/// The engine-owned localization keys for the patch-notes chrome text (title, close, empty state, and the
/// per-<see cref="PatchNoteCategory"/> labels), plus the built-in English fallback
/// (<see cref="EnglishDefaults"/>). A game localizes the patch-notes screen by adding these keys to its own
/// catalog and wiring it as <see cref="LocalizationContext.Catalog"/>; <see cref="Resolve"/> then falls back
/// to the English values here whenever a key is absent or no catalog is wired, mirroring the pattern
/// <see cref="UpdateOverlayStrings"/> established for the update overlay.
/// </summary>
public static class PatchNotesStrings
{
    /// <summary>The panel title. No arguments.</summary>
    public static readonly StringId Title = new("patchnotes.title");

    /// <summary>The close action label. No arguments.</summary>
    public static readonly StringId Close = new("patchnotes.close");

    /// <summary>Shown when the parsed <see cref="PatchNotesDocument"/> has no builds. No arguments.</summary>
    public static readonly StringId Empty = new("patchnotes.empty");

    private static readonly StringId CategoryNew = new("patchnotes.category.new");
    private static readonly StringId CategoryMajor = new("patchnotes.category.major");
    private static readonly StringId CategoryMinor = new("patchnotes.category.minor");
    private static readonly StringId CategoryRebalance = new("patchnotes.category.rebalance");
    private static readonly StringId CategoryBug = new("patchnotes.category.bug");
    private static readonly StringId CategoryOther = new("patchnotes.category.other");

    /// <summary>The <see cref="StringId"/> that labels <paramref name="category"/> in the group header.</summary>
    public static StringId CategoryLabel(PatchNoteCategory category) => category switch
    {
        PatchNoteCategory.New => CategoryNew,
        PatchNoteCategory.Major => CategoryMajor,
        PatchNoteCategory.Minor => CategoryMinor,
        PatchNoteCategory.Rebalance => CategoryRebalance,
        PatchNoteCategory.Bug => CategoryBug,
        _ => CategoryOther,
    };

    /// <summary>
    /// Resolves <paramref name="id"/> through the ambient <see cref="LocalizationContext.Catalog"/> when it is
    /// wired AND carries the key; otherwise resolves against <see cref="EnglishDefaults"/>. Never throws and
    /// never shows a raw key for any of the ids declared on this type.
    /// </summary>
    public static string Resolve(StringId id)
    {
        IStringCatalog catalog = LocalizationContext.Catalog is { } c && c.TryGet(id.Key, out _)
            ? c
            : EnglishDefaults;
        return catalog.Get(id.Key);
    }

    /// <summary>
    /// The built-in English default strings, keyed by the ids above. This is the exact text
    /// <see cref="Resolve"/> returns when no wired catalog resolves a key. A game normally provides its own
    /// catalog rather than reading this directly.
    /// </summary>
    public static IStringCatalog EnglishDefaults { get; } = new EnglishDefaultCatalog();

    /// <summary>The English strings as a raw map (same values as <see cref="EnglishDefaults"/>).</summary>
    internal static IReadOnlyDictionary<string, string> EnglishStrings => EnglishDefaultCatalog.Map;

    private sealed class EnglishDefaultCatalog : IStringCatalog
    {
        internal static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["patchnotes.title"] = "Patch Notes",
            ["patchnotes.close"] = "Close",
            ["patchnotes.empty"] = "No patch notes available.",
            ["patchnotes.category.new"] = "New",
            ["patchnotes.category.major"] = "Major",
            ["patchnotes.category.minor"] = "Minor",
            ["patchnotes.category.rebalance"] = "Rebalance",
            ["patchnotes.category.bug"] = "Bug fixes",
            ["patchnotes.category.other"] = "Notes",
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
