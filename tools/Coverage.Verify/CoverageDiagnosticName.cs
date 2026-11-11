using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Keeps the diagnostic export from landing on another file of the run.</summary>
internal static class CoverageDiagnosticName
{
    // Linked directories, mounts and prefixes can spell one directory in ways no path comparison can enumerate,
    // but they all keep the file name. So the diagnostic name must be unique within the run, against each run
    // file's own name and against the name of the file a linked run file finally points at. Case and Unicode
    // composition are folded because volumes may fold them.
    internal static bool Collides(string diagnosticPath, IEnumerable<string> runPaths)
    {
        string name = Key(diagnosticPath);
        return runPaths.Any(path => Key(path) == name || FinalTarget(path) is { } target && Key(target) == name);
    }

    // A Windows device prefix skips short-name expansion and a stream suffix names part of another file, so a
    // file name cannot identify either. Diagnostics are refused for such paths instead of guessing.
    internal static bool Unsupported(string path, bool windows) => windows
        && (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0);

    static string Key(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
        .Normalize(NormalizationForm.FormC).ToUpperInvariant();

    static string? FinalTarget(string path)
    {
        // Only an entry that does not exist yet has no target. Any other lookup failure leaves the target name
        // unknown, so it propagates and the invocation is refused instead of passing an unchecked destination.
        try { return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}
