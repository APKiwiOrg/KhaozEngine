using System;
using System.Diagnostics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfHostProcess
{
    internal static string Run(string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start " + command);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Cold host inspection exceeded its fixed 15-second limit.");
        }
        string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException(command + " failed. " + stderr);
        return stdout.TrimEnd();
    }
}
