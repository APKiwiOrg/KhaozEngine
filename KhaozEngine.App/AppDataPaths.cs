using System;
using System.IO;

namespace KhaozEngine.App;

/// <summary>
/// Resolves the OS-correct application-data directory (for saves, settings, logs) under a given
/// app folder name, and exposes conventional file paths inside it.
/// <list type="bullet">
///   <item>Windows: <c>%APPDATA%\&lt;appFolderName&gt;\</c></item>
///   <item>macOS: <c>~/Library/Application Support/&lt;appFolderName&gt;/</c></item>
///   <item>Linux: <c>$XDG_DATA_HOME/&lt;appFolderName&gt;/</c> (else <c>~/.local/share/&lt;appFolderName&gt;/</c>)</item>
/// </list>
/// </summary>
public sealed class AppDataPaths
{
    private readonly string appFolderName;
    private readonly IAppDataEnvironment environment;
    private readonly Lazy<string> resolvedBaseDir;

    /// <summary>Creates a resolver for the given app folder name using the real OS environment.</summary>
    /// <exception cref="ArgumentException"><paramref name="appFolderName"/> is null, empty, or whitespace.</exception>
    public AppDataPaths(string appFolderName)
        : this(appFolderName, new SystemAppDataEnvironment())
    {
    }

    internal AppDataPaths(string appFolderName, IAppDataEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(appFolderName))
        {
            throw new ArgumentException("An app folder name must be provided.", nameof(appFolderName));
        }

        this.appFolderName = appFolderName;
        this.environment = environment;
        this.resolvedBaseDir = new Lazy<string>(CreateBaseDirectory);
    }

    /// <summary>
    /// The root app-data directory. Resolved and created on first access, then cached; resolution
    /// and directory creation happen exactly once even under concurrent access (backed by
    /// <see cref="Lazy{T}"/>).
    /// </summary>
    public string BaseDirectory => resolvedBaseDir.Value;

    private string CreateBaseDirectory()
    {
        string dir = ResolveBaseDirectory();
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Full path to <c>save.json</c> in the app-data directory.</summary>
    public string SaveFilePath => Path.Combine(BaseDirectory, "save.json");

    /// <summary>Full path to <c>settings.json</c> in the app-data directory.</summary>
    public string SettingsFilePath => Path.Combine(BaseDirectory, "settings.json");

    /// <summary>Full path to <c>game.log</c> in the app-data directory.</summary>
    public string LogFilePath => Path.Combine(BaseDirectory, "game.log");

    /// <summary>Full path to <c>game.prev.log</c> in the app-data directory.</summary>
    public string PreviousLogFilePath => Path.Combine(BaseDirectory, "game.prev.log");

    /// <summary>Full path to <paramref name="fileName"/> in the app-data directory.</summary>
    public string GetFilePath(string fileName) => Path.Combine(BaseDirectory, fileName);

    private string ResolveBaseDirectory()
    {
        if (environment.IsWindows)
        {
            string appData = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(appData, appFolderName);
            }
        }
        else if (environment.IsMacOS)
        {
            string appSupport = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appSupport))
            {
                return Path.Combine(appSupport, appFolderName);
            }
        }
        else if (environment.IsLinux)
        {
            string? xdgDataHome = environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                return Path.Combine(xdgDataHome, appFolderName);
            }

            string? home = environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share", appFolderName);
            }
        }

        string localAppData = environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, appFolderName);
        }

        string homeDir = environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, $".{appFolderName.ToLowerInvariant()}");
    }
}
