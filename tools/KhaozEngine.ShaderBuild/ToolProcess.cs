using System.Collections.Generic;
using System.Diagnostics;

namespace KhaozEngine.ShaderBuild;

/// <summary>Runs a command-line tool to completion and captures its exit code and both output streams.</summary>
internal static class ToolProcess
{
    public static (int ExitCode, string Output, string Error) Run(string executable, IEnumerable<string> arguments, string? workingDirectory = null)
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
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        var errorText = error.GetAwaiter().GetResult();
        process.WaitForExit();
        return (process.ExitCode, output, errorText);
    }
}
