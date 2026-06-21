using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace KhaozEngine.Sfx;

/// <summary>Real <see cref="IProcessRunner"/> over <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <inheritdoc/>
    public bool ToolExists(string exe)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return false;

        bool isWindows = OperatingSystem.IsWindows();
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (File.Exists(Path.Combine(dir, exe))) return true;
            if (isWindows && (File.Exists(Path.Combine(dir, exe + ".exe")) || File.Exists(Path.Combine(dir, exe + ".cmd"))))
                return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public ProcessResult Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new ProcessResult(proc.ExitCode, stdout, stderr);
    }
}
