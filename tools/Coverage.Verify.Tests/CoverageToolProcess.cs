using System;
using System.Diagnostics;
using System.IO;
using KhaozEngine.Tools.Coverage;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>Starts the coverage executable under test. Ordinary tooling CI runs the source-built tool assembly through
/// `dotnet`. An absolute `KE_COVERAGE_TOOL_EXECUTABLE` selects an installed `ke-coverage` instead, so a candidate
/// package runs exactly the same assertions. The selection never installs or changes a global tool.</summary>
internal static class CoverageToolProcess
{
    internal const string ExecutableVariable = "KE_COVERAGE_TOOL_EXECUTABLE";

    /// <summary>The installed executable, or the source-built tool assembly when none is selected.</summary>
    internal static string Selected => Installed() ?? typeof(Program).Assembly.Location;

    internal static ProcessStartInfo Create(string[] args)
    {
        string? installed = Installed();
        var start = new ProcessStartInfo(installed ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (installed == null) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["KE_GPU_TESTS"] = "0";
        return start;
    }

    static string? Installed()
    {
        string? path = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (string.IsNullOrEmpty(path)) return null;
        // A relative name could resolve to some other tool on the search path, which would prove nothing.
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException(ExecutableVariable + " must name an existing executable by absolute path.");
        return path;
    }
}
