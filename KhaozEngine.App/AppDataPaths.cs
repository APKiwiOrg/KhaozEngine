using System;
using System.IO;

namespace KhaozEngine.App;

/// <summary>
/// Resolves the OS-correct application-data directory (for saves, settings, logs) under a
/// publisher root, and exposes conventional file paths inside it. Layout is
/// <c>&lt;os-base&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c> so every game from one publisher nests together.
/// <list type="bullet">
///   <item>Windows: <c>%APPDATA%\&lt;publisher&gt;\&lt;appName&gt;\</c></item>
///   <item>macOS: <c>~/Library/Application Support/&lt;publisher&gt;/&lt;appName&gt;/</c></item>
///   <item>Linux: <c>$XDG_DATA_HOME/&lt;publisher&gt;/&lt;appName&gt;/</c> (else <c>~/.local/share/&lt;publisher&gt;/&lt;appName&gt;/</c>)</item>
///   <item>Android / iOS: <c>&lt;app-sandbox&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c></item>
/// </list>
/// </summary>
public sealed class AppDataPaths
{
    private readonly Func<string> resolveBaseDirectory;
    private readonly Lazy<string> resolvedBaseDir;

    /// <summary>Creates a resolver for the given publisher and app name using the real OS environment.</summary>
    /// <exception cref="ArgumentException"><paramref name="publisher"/> or <paramref name="appName"/> is null, empty, or whitespace.</exception>
    public AppDataPaths(string publisher, string appName)
        : this(publisher, appName, new SystemAppDataEnvironment())
    {
    }

    internal AppDataPaths(string publisher, string appName, IAppDataEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            throw new ArgumentException("A publisher name must be provided.", nameof(publisher));
        }
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new ArgumentException("An app name must be provided.", nameof(appName));
        }

        resolveBaseDirectory = () => ResolveBaseDirectory(publisher, appName, environment);
        this.resolvedBaseDir = new Lazy<string>(CreateBaseDirectory);
    }

    private AppDataPaths(string baseDirectory)
    {
        resolveBaseDirectory = () => baseDirectory;
        resolvedBaseDir = new Lazy<string>(CreateBaseDirectory);
    }

    /// <summary>
    /// Creates a resolver rooted at an explicit fully-qualified directory. Use this for portable installs,
    /// tooling, and tests that must not touch the current user's application-data directory. The directory is
    /// normalized immediately and created lazily on first path access, matching the standard resolver.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> is null, empty, whitespace, or not
    /// fully qualified.</exception>
    public static AppDataPaths FromDirectory(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("A base directory must be provided.", nameof(baseDirectory));
        }
        if (!Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ArgumentException("The base directory must be fully qualified.", nameof(baseDirectory));
        }

        return new AppDataPaths(Path.GetFullPath(baseDirectory));
    }

    /// <summary>
    /// The root app-data directory. Resolved and created on first access, then cached; resolution
    /// and directory creation happen exactly once even under concurrent access (backed by
    /// <see cref="Lazy{T}"/>).
    /// </summary>
    public string BaseDirectory => resolvedBaseDir.Value;

    private string CreateBaseDirectory()
    {
        string dir = resolveBaseDirectory();
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

    private static string ResolveBaseDirectory(string publisher, string appName, IAppDataEnvironment environment)
    {
        // Mobile sandboxes are checked first so a platform that also reports a desktop flag
        // cannot shadow them.
        if (environment.IsAndroid || environment.IsIOS)
        {
            string sandbox = environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(sandbox))
            {
                return Nest(sandbox, publisher, appName);
            }
        }
        else if (environment.IsWindows)
        {
            string appData = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Nest(appData, publisher, appName);
            }
        }
        else if (environment.IsMacOS)
        {
            string appSupport = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appSupport))
            {
                return Nest(appSupport, publisher, appName);
            }
        }
        else if (environment.IsLinux)
        {
            string? xdgDataHome = environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                return Nest(xdgDataHome, publisher, appName);
            }

            string? home = environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share", publisher, appName);
            }
        }

        string localAppData = environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Nest(localAppData, publisher, appName);
        }

        string homeDir = environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, "." + publisher.ToLowerInvariant(), appName);
    }

    private static string Nest(string baseDir, string publisher, string appName) =>
        Path.Combine(baseDir, publisher, appName);
}
