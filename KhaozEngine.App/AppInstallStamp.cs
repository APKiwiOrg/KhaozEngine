using System;

namespace KhaozEngine.App;

/// <summary>
/// A local record of when the current app version first ran on this machine and when it last changed.
/// Persistence-agnostic and serializable: store it inside whatever settings/save DTO a game already
/// persists (see <see cref="Resolve"/>). This is the local first-ran / updated stamp, distinct from the
/// build's release date (a per-game build property surfaced via <see cref="BuildMetadata"/>).
/// </summary>
/// <param name="Version">The app version string this stamp was last resolved against.</param>
/// <param name="FirstInstalledAtUtc">When any version first ran on this machine. Preserved across upgrades.</param>
/// <param name="UpdatedAtUtc">When the running version last changed (equals <see cref="FirstInstalledAtUtc"/> on first run).</param>
public sealed record AppInstallStamp(string Version, DateTime FirstInstalledAtUtc, DateTime UpdatedAtUtc)
{
    /// <summary>
    /// Resolves the stamp for the current run. Pure and deterministic: <paramref name="utcNow"/> is injected,
    /// so there is no hidden <see cref="DateTime.UtcNow"/> and headless / snapshot replay stays stable.
    /// <list type="bullet">
    /// <item>First run (<paramref name="previous"/> is null): both dates are set to <paramref name="utcNow"/>; reports changed.</item>
    /// <item>Same version: returns <paramref name="previous"/> untouched (same reference); reports not changed.</item>
    /// <item>Different version (upgrade <b>or</b> downgrade): <see cref="FirstInstalledAtUtc"/> is preserved while
    /// <see cref="Version"/> and <see cref="UpdatedAtUtc"/> are bumped; reports changed. Version strings are compared
    /// for ordinal inequality only - no semver ordering, so a downgrade is treated exactly like an upgrade.</item>
    /// </list>
    /// </summary>
    /// <param name="previous">The previously persisted stamp, or null if the app has never recorded one.</param>
    /// <param name="currentVersion">The current app version string (e.g. from <see cref="BuildMetadata"/>).</param>
    /// <param name="utcNow">The current UTC instant; injected so callers control time.</param>
    /// <returns>The resolved stamp plus a flag indicating whether it differs from <paramref name="previous"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="currentVersion"/> is null.</exception>
    public static AppInstallStampResult Resolve(AppInstallStamp? previous, string currentVersion, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        if (previous is null)
        {
            return new AppInstallStampResult(new AppInstallStamp(currentVersion, utcNow, utcNow), Changed: true);
        }

        if (string.Equals(previous.Version, currentVersion, StringComparison.Ordinal))
        {
            return new AppInstallStampResult(previous, Changed: false);
        }

        return new AppInstallStampResult(previous with { Version = currentVersion, UpdatedAtUtc = utcNow }, Changed: true);
    }
}

/// <summary>The outcome of <see cref="AppInstallStamp.Resolve"/>: the resolved stamp and whether it changed.</summary>
/// <param name="Stamp">The resolved stamp (the previous one untouched when <paramref name="Changed"/> is false).</param>
/// <param name="Changed">True when the stamp differs from the previous one and should be persisted.</param>
public readonly record struct AppInstallStampResult(AppInstallStamp Stamp, bool Changed);
