using System.IO;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed class RendererPerfWorkflowText(string text)
{
    readonly string _text = text.ReplaceLineEndings("\n");

    internal string Job(string name) => _text.Split("  " + name + ":\n", 2)[1];

    internal static string Selector([CallerFilePath] string source = "")
    {
        string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
        return File.ReadAllText(Path.Combine(repo, "scripts/renderer-perf-workflow.ps1")).ReplaceLineEndings("\n");
    }

    internal static RendererPerfWorkflowText Read([CallerFilePath] string source = "")
    {
        string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
        return new(File.ReadAllText(Path.Combine(repo, ".github/workflows/gpu-hosted-bench.yml")));
    }
}
