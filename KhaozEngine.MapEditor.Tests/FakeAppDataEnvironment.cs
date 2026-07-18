using System;
using System.Collections.Generic;
using KhaozEngine.App;

namespace KhaozEngine.Tests;

// Wave-3 split duplication. The canonical copy is embedded in
// KhaozEngine.Foundation.Tests/AppDataPathsTests.cs (consumed by the Foundation persistence/app tests).
// MapEditor.Tests keeps its own copy because EditorRecentFilesTests.cs consumes it, and it depends on
// KhaozEngine.App (IAppDataEnvironment) so it cannot live in KhaozEngine.TestSupport (Primitives-only).

/// <summary>Test double for <see cref="IAppDataEnvironment"/> - all facts are settable.</summary>
internal sealed class FakeAppDataEnvironment : IAppDataEnvironment
{
    public bool IsWindows { get; set; }
    public bool IsMacOS { get; set; }
    public bool IsLinux { get; set; }
    public bool IsAndroid { get; set; }
    public bool IsIOS { get; set; }
    public Dictionary<Environment.SpecialFolder, string> Folders { get; } = new();
    public Dictionary<string, string?> EnvVars { get; } = new();
    public int GetFolderPathCalls { get; private set; }

    public string GetFolderPath(Environment.SpecialFolder folder)
    {
        GetFolderPathCalls++;
        return Folders.TryGetValue(folder, out string? value) ? value : string.Empty;
    }

    public string? GetEnvironmentVariable(string variable) =>
        EnvVars.TryGetValue(variable, out string? value) ? value : null;
}
