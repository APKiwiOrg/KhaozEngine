using System.IO;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Resolve only this compiled checkout. Campaign admission retains its independent workspace checks.
internal static class RendererPerfRepositoryPaths
{
    internal static string Root => SourceRoot();

    static string SourceRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../.."));

    internal static string File(string relative) => Path.Combine(Root, relative);
}
