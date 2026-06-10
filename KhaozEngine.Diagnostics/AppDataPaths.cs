using System;
using System.Collections.Concurrent;
using System.IO;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Resolves the OS-correct per-application data directory (for logs, saves, settings):
/// Windows <c>%APPDATA%\&lt;app&gt;</c>, macOS <c>~/Library/Application Support/&lt;app&gt;</c>,
/// Linux <c>$XDG_DATA_HOME/&lt;app&gt;</c> or <c>~/.local/share/&lt;app&gt;</c>, with fallbacks.
/// The directory is created on first access and the result cached per app name.
/// </summary>
public static class AppDataPaths
{
    private static readonly ConcurrentDictionary<string, string> cache = new();

    /// <summary>Returns (creating if needed) the base data directory for <paramref name="appFolderName"/>.</summary>
    public static string Resolve(string appFolderName)
    {
        if (string.IsNullOrWhiteSpace(appFolderName)) throw new ArgumentException("App folder name is required.", nameof(appFolderName));
        return cache.GetOrAdd(appFolderName, name =>
        {
            string dir = ResolveBase(name);
            try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
            return dir;
        });
    }

    /// <summary>Returns a path under the app's base directory.</summary>
    public static string Combine(string appFolderName, params string[] parts)
    {
        string baseDir = Resolve(appFolderName);
        if (parts is null || parts.Length == 0) return baseDir;
        string[] all = new string[parts.Length + 1];
        all[0] = baseDir;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    private static string ResolveBase(string app)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: SpecialFolder.ApplicationData = %APPDATA% (roaming).
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData)) return Path.Combine(appData, app);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: .NET maps SpecialFolder.ApplicationData to ~/Library/Application Support
            // (same enum as Windows, different OS target). Kept explicit to document per-OS intent.
            string appSupport = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appSupport)) return Path.Combine(appSupport, app);
        }
        else if (OperatingSystem.IsLinux())
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, app);
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(home, ".local", "share", app);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) return Path.Combine(localAppData, app);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "." + app.ToLowerInvariant());
    }
}
