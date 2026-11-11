using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Keeps the diagnostic export from ever landing on another file of the run.</summary>
internal static class CoverageDiagnosticName
{
    // A directory link, a device prefix or a second mount can spell one destination in ways no path comparison
    // can enumerate. Every spelling of one directory entry still ends in the same file name, so the diagnostic
    // name must be unique within the run. Case and Unicode composition are folded because volumes may fold them.
    internal static bool Collides(string diagnosticPath, IEnumerable<string> runPaths)
    {
        string name = Key(diagnosticPath);
        return runPaths.Any(path => Key(path) == name);
    }

    static string Key(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
        .Normalize(NormalizationForm.FormC).ToUpperInvariant();
}
