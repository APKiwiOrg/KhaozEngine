using System;
using System.Runtime.InteropServices;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// Maps the running OS/architecture to a .NET runtime identifier used as the update "platform"
/// (build channel). The default set covers desktop publish targets; games may override the platform
/// string on <see cref="UpdateServiceOptions"/> if they publish under different identifiers.
/// </summary>
public static class UpdatePlatform
{
    /// <summary>Resolves the runtime id for the current process.</summary>
    public static string ResolveRuntimeId()
    {
        return Map(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture);
    }

    /// <summary>
    /// Pure OS/arch to runtime-id mapping. Windows =&gt; win-x64; macOS arm64 =&gt; osx-arm64; macOS
    /// otherwise =&gt; osx-x64; anything else =&gt; linux-x64.
    /// </summary>
    public static string Map(bool isWindows, bool isMacOs, Architecture architecture)
    {
        if (isWindows) return "win-x64";
        if (isMacOs) return architecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return "linux-x64";
    }
}
