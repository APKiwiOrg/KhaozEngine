using System;

namespace KhaozEngine.App;

/// <summary>Default <see cref="IAppDataEnvironment"/> over the real operating system and environment.</summary>
internal sealed class SystemAppDataEnvironment : IAppDataEnvironment
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsMacOS => OperatingSystem.IsMacOS();
    public bool IsLinux => OperatingSystem.IsLinux();
    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);
}
