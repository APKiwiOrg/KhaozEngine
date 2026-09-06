using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;
using KhaozEngine.Updates;

namespace KhaozEngine.Gui;

/// <summary>
/// The engine-owned localization keys for the default <see cref="UpdateOverlayTheme"/> text, one per
/// <see cref="UpdateState"/> line, plus the built-in English fallback (<see cref="EnglishDefaults"/>).
/// A game localizes the overlay by adding these keys to its own catalog and wiring it as
/// <see cref="LocalizationContext.Catalog"/>; the default theme then resolves through it, and falls back to
/// the English values here whenever a key is absent or no catalog is wired (see <see cref="UpdateOverlayTheme"/>).
/// The keys match the <c>update.overlay.*</c> set Ruinborne already authored, so a game can drop a bespoke
/// theme override and just supply catalog entries.
/// </summary>
/// <remarks>
/// Format arguments per key (mirrored by <see cref="EnglishDefaults"/>): the titles that show a version take
/// <c>{0}</c> = the remote version; the action bodies take <c>{0}</c> = the trigger-key label; the downloading
/// body takes <c>{0}</c> = files downloaded, <c>{1}</c> = total files, <c>{2}</c> = MB downloaded,
/// <c>{3}</c> = total MB. <see cref="DismissHint"/> takes <c>{0}</c> = the dismiss-key label. The other lines
/// take no arguments.
///
/// The <c>*.required</c> variants are used for a REQUIRED update (the client auto-downloads then auto-restarts,
/// so no keypress prompt applies): the required titles that show a version take <c>{0}</c> = the remote version,
/// and every required body takes no arguments (there is no trigger key to name). The downloading/applying/failed
/// BODIES have no required variant (their shared text carries no key prompt). A game overriding
/// <see cref="UpdateOverlayTheme.TitleFor(UpdateState, IUpdateStatus)"/> / <see cref="UpdateOverlayTheme.BodyFor"/>
/// selects these itself off <see cref="IUpdateStatus.IsRequired"/>.
/// </remarks>
public static class UpdateOverlayStrings
{
    /// <summary>Title for <see cref="UpdateState.UpdateAvailable"/>. Takes <c>{0}</c> = remote version.</summary>
    public static readonly StringId AvailableTitle = new("update.overlay.available.title");

    /// <summary>Body for <see cref="UpdateState.UpdateAvailable"/>. Takes <c>{0}</c> = trigger-key label.</summary>
    public static readonly StringId AvailableBody = new("update.overlay.available.body");

    /// <summary>Title for <see cref="UpdateState.Downloading"/>. No arguments.</summary>
    public static readonly StringId DownloadingTitle = new("update.overlay.downloading.title");

    /// <summary>Body for <see cref="UpdateState.Downloading"/>. Takes <c>{0}</c> files, <c>{1}</c> total files,
    /// <c>{2}</c> MB, <c>{3}</c> total MB.</summary>
    public static readonly StringId DownloadingBody = new("update.overlay.downloading.body");

    /// <summary>Title for <see cref="UpdateState.ReadyToApply"/>. Takes <c>{0}</c> = remote version.</summary>
    public static readonly StringId ReadyTitle = new("update.overlay.ready.title");

    /// <summary>Body for <see cref="UpdateState.ReadyToApply"/>. Takes <c>{0}</c> = trigger-key label.</summary>
    public static readonly StringId ReadyBody = new("update.overlay.ready.body");

    /// <summary>Title for <see cref="UpdateState.Applying"/>. No arguments.</summary>
    public static readonly StringId ApplyingTitle = new("update.overlay.applying.title");

    /// <summary>Body for <see cref="UpdateState.Applying"/>. No arguments.</summary>
    public static readonly StringId ApplyingBody = new("update.overlay.applying.body");

    /// <summary>Title for <see cref="UpdateState.Failed"/>. No arguments.</summary>
    public static readonly StringId FailedTitle = new("update.overlay.failed.title");

    /// <summary>Body for <see cref="UpdateState.Failed"/>. Takes <c>{0}</c> = trigger-key label.</summary>
    public static readonly StringId FailedBody = new("update.overlay.failed.body");

    /// <summary>
    /// Body for <see cref="UpdateState.Failed"/> once the session's apply budget is spent
    /// (<see cref="IUpdateStatus.ApplyAttemptsExhausted"/>), replacing <see cref="FailedBody"/> because there
    /// is no retry left to prompt for. No arguments.
    /// </summary>
    public static readonly StringId FailedBodyExhausted = new("update.overlay.failed.body.exhausted");

    /// <summary>Title for <see cref="UpdateState.Untrusted"/>. No arguments.</summary>
    public static readonly StringId UntrustedTitle = new("update.overlay.untrusted.title");

    /// <summary>Body for <see cref="UpdateState.Untrusted"/>. No arguments.</summary>
    public static readonly StringId UntrustedBody = new("update.overlay.untrusted.body");

    /// <summary>
    /// The dismiss prompt drawn under the body on a panel the player is allowed to decline
    /// (<see cref="UpdateOverlayView.IsDismissible"/>, and never for a required update). Takes
    /// <c>{0}</c> = the dismiss-key label.
    /// </summary>
    public static readonly StringId DismissHint = new("update.overlay.dismiss.hint");

    // --- Required-update variants (auto-download + auto-apply; no keypress prompt) ---

    /// <summary>Required-update title for <see cref="UpdateState.UpdateAvailable"/>. Takes <c>{0}</c> = remote version.</summary>
    public static readonly StringId AvailableTitleRequired = new("update.overlay.available.title.required");

    /// <summary>Required-update body for <see cref="UpdateState.UpdateAvailable"/>. No arguments.</summary>
    public static readonly StringId AvailableBodyRequired = new("update.overlay.available.body.required");

    /// <summary>Required-update title for <see cref="UpdateState.Downloading"/>. No arguments.</summary>
    public static readonly StringId DownloadingTitleRequired = new("update.overlay.downloading.title.required");

    /// <summary>Required-update title for <see cref="UpdateState.ReadyToApply"/>. Takes <c>{0}</c> = remote version.</summary>
    public static readonly StringId ReadyTitleRequired = new("update.overlay.ready.title.required");

    /// <summary>Required-update body for <see cref="UpdateState.ReadyToApply"/>. No arguments.</summary>
    public static readonly StringId ReadyBodyRequired = new("update.overlay.ready.body.required");

    /// <summary>Required-update title for <see cref="UpdateState.Applying"/>. No arguments.</summary>
    public static readonly StringId ApplyingTitleRequired = new("update.overlay.applying.title.required");

    /// <summary>Required-update title for <see cref="UpdateState.Failed"/>. No arguments.</summary>
    public static readonly StringId FailedTitleRequired = new("update.overlay.failed.title.required");

    /// <summary>
    /// The built-in English default templates, keyed by the ids above. This is the exact text the default
    /// <see cref="UpdateOverlayTheme"/> renders when no wired catalog resolves a key, byte-identical to the
    /// engine's historical hardcoded English. It formats with <see cref="CultureInfo.InvariantCulture"/> (the
    /// pre-localization overlay always formatted the download line invariantly), so the fallback never shifts
    /// with the machine locale. A game normally provides its own catalog rather than reading this directly.
    /// </summary>
    public static IStringCatalog EnglishDefaults { get; } = new EnglishDefaultCatalog();

    /// <summary>The English templates as a raw map (same values as <see cref="EnglishDefaults"/>).</summary>
    internal static IReadOnlyDictionary<string, string> EnglishTemplates => EnglishDefaultCatalog.Map;

    private sealed class EnglishDefaultCatalog : IStringCatalog
    {
        internal static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["update.overlay.available.title"] = "Update Available - v{0}",
            ["update.overlay.available.body"] = "Press [{0}] to download",
            ["update.overlay.downloading.title"] = "Downloading Update...",
            ["update.overlay.downloading.body"] = "Downloading {0}/{1} files ({2:0.0}/{3:0.0} MB)",
            ["update.overlay.ready.title"] = "Update v{0} Ready",
            ["update.overlay.ready.body"] = "Press [{0}] to restart and apply",
            ["update.overlay.applying.title"] = "Applying Update...",
            ["update.overlay.applying.body"] = "Game will restart shortly",
            ["update.overlay.failed.title"] = "Update Failed",
            ["update.overlay.failed.body"] = "Press [{0}] to retry",
            ["update.overlay.failed.body.exhausted"] = "This update could not be installed",
            ["update.overlay.untrusted.title"] = "Updates Cannot Be Verified",
            ["update.overlay.untrusted.body"] = "This build cannot verify updates",
            ["update.overlay.dismiss.hint"] = "Press [{0}] to dismiss",
            ["update.overlay.available.title.required"] = "Required Update - v{0}",
            ["update.overlay.available.body.required"] = "A required update is downloading",
            ["update.overlay.downloading.title.required"] = "Downloading Required Update...",
            ["update.overlay.ready.title.required"] = "Required Update v{0} Ready",
            ["update.overlay.ready.body.required"] = "Restarting to apply",
            ["update.overlay.applying.title.required"] = "Applying Required Update...",
            ["update.overlay.failed.title.required"] = "Required Update Failed",
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
