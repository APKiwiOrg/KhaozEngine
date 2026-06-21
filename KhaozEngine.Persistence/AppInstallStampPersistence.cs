using System;
using KhaozEngine.App;

namespace KhaozEngine.Persistence;

/// <summary>
/// Thin convenience for stamping the install/update record through a <see cref="SettingsManager{T}"/>.
/// The pure resolver (<see cref="AppInstallStamp.Resolve"/>) is the core; this just wires it to the
/// settings the game already persists. Store an <see cref="AppInstallStamp"/> field on your settings
/// DTO and call <see cref="StampInstall"/> once at boot.
/// </summary>
public static class AppInstallStampPersistence
{
    /// <summary>
    /// Resolves the install stamp against the manager's current settings and, if it changed, writes it
    /// back via <paramref name="write"/> and calls <see cref="SettingsManager{T}.Save"/>. A no-op run
    /// (same version) does not save. Mutates the existing settings object in place; it does not replace it.
    /// </summary>
    /// <typeparam name="T">The settings DTO type.</typeparam>
    /// <param name="manager">The settings manager holding the live settings.</param>
    /// <param name="read">Reads the persisted stamp from the settings (return null if never stamped).</param>
    /// <param name="write">Writes the resolved stamp back onto the settings.</param>
    /// <param name="currentVersion">The current app version string (e.g. from <see cref="BuildMetadata"/>).</param>
    /// <param name="utcNow">The current UTC instant; injected so callers control time.</param>
    /// <returns>The resolved stamp plus whether it changed (and was saved).</returns>
    /// <exception cref="ArgumentNullException">Any of <paramref name="manager"/>, <paramref name="read"/>, or <paramref name="write"/> is null.</exception>
    public static AppInstallStampResult StampInstall<T>(
        this SettingsManager<T> manager,
        Func<T, AppInstallStamp?> read,
        Action<T, AppInstallStamp> write,
        string currentVersion,
        DateTime utcNow) where T : new()
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        T settings = manager.Settings;
        AppInstallStampResult result = AppInstallStamp.Resolve(read(settings), currentVersion, utcNow);
        if (result.Changed)
        {
            write(settings, result.Stamp);
            manager.Save();
        }

        return result;
    }
}
