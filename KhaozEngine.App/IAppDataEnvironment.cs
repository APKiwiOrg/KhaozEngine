using System;

namespace KhaozEngine.App;

/// <summary>
/// Abstraction over the OS / environment facts used to resolve the app-data directory. Internal:
/// games never see it. Exists so <see cref="AppDataPaths"/>'s OS-branching resolution can be
/// exercised deterministically in headless tests via a fake implementation.
/// </summary>
internal interface IAppDataEnvironment
{
    /// <summary>True when running on Windows.</summary>
    bool IsWindows { get; }

    /// <summary>True when running on macOS.</summary>
    bool IsMacOS { get; }

    /// <summary>True when running on Linux.</summary>
    bool IsLinux { get; }

    /// <summary>True when running on Android.</summary>
    bool IsAndroid { get; }

    /// <summary>True when running on iOS.</summary>
    bool IsIOS { get; }

    /// <summary>Maps to <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>.</summary>
    string GetFolderPath(Environment.SpecialFolder folder);

    /// <summary>Maps to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    string? GetEnvironmentVariable(string variable);
}
