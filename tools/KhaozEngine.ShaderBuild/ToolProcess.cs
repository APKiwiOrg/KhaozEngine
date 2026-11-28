using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace KhaozEngine.ShaderBuild;

/// <summary>Runs a command-line tool to completion and captures its exit code and both output streams.</summary>
internal static class ToolProcess
{
    /// <summary>A hang guard far beyond any real compile, so a stuck tool fails the build instead of stalling it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public static (int ExitCode, string Output, string Error) Run(
        string executable, IEnumerable<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var limit = timeout ?? DefaultTimeout;
        if (!process.WaitForExit(limit))
        {
            // Killing the whole tree closes every inherited pipe handle, so the pending reads complete.
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new ShaderBuildException(
                ShaderBuildFailure.ToolTimedOut,
                $"{Path.GetFileName(executable)} ({executable}) did not finish within {limit} and was stopped.");
        }
        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
