using System;
using System.IO;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>A private scratch root for one command line test under the OS temporary root. It holds session copies,
/// staging, generated campaigns and child process temporary files, and is removed with everything in it.</summary>
internal sealed class CoverageCliScratch : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-cli-" + Guid.NewGuid().ToString("N"));

    internal CoverageCliScratch()
    {
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
