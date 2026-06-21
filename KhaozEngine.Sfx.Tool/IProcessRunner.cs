using System.Collections.Generic;

namespace KhaozEngine.Sfx;

/// <summary>Result of running an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Abstraction over launching external tools (ffmpeg / oggenc), so the probe and encoder are unit-testable
/// without a real process (per the engine's "no real device in unit tests" rule).
/// </summary>
public interface IProcessRunner
{
    /// <summary>True if <paramref name="exe"/> resolves on PATH.</summary>
    bool ToolExists(string exe);
    /// <summary>Runs <paramref name="exe"/> with <paramref name="args"/> to completion and captures output.</summary>
    ProcessResult Run(string exe, IReadOnlyList<string> args);
}
